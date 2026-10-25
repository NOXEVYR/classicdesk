[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Validate','Stage')][string]$Action,
    [Parameter(Mandatory)][string]$Installation,
    [Parameter(Mandatory)][string]$Bundle,
    [string]$ExpectedBundleHash,
    [string]$ReportPath
)
$ErrorActionPreference='Stop'
$env:PSModulePath=$PSHOME+'\Modules'
$repo=Split-Path $PSScriptRoot -Parent
$serviceName='ClassicDeskShell'
$expectedHost='14603CC429E3A109AAD6133E163131DCEC890F2F8F7D614554B435CE2468A67D'
$oldRuntime='02FB7D1F8FD886CBEF1569DA87F481ACD433B1E8C8BD064A4B7EED66A253FA2C'
$newHost='D08EE9E4B2D9E3F92A704FEEE20E63BAC57DEAAEE6E2A6FB3351427C46BA9650'
$newRuntime='12CA9CD52EA5D64FED8136253CEB0BDB9C7FEC8D51D95388C83F408F4BD41F9F'
$newModule='E3F6EF523414F4895CF8B6CCA1EA374C71FEE8AAB1D78D4CB3F29F0052412393'
$oldDll='Runtime/AppData/Engine/Mods/64/taskbar-start-button-position_1.3.2.dll'
$newDll='Runtime/AppData/Engine/Mods/64/taskbar-start-button-position_1.3.2-classicdesk.1.dll'
$modIni='Runtime/AppData/Engine/Mods/taskbar-start-button-position.ini'
$manifestFile='Runtime/runtime-assets-manifest.json'
$adminSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$usersSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
$base=Join-Path $env:ProgramW6432 'ClassicDesk'
$serviceBase=Join-Path $base 'ShellService'
# Reuse the existing path/ownership/ACL checks without executing installer actions.
$parseErrors=$null;$tokens=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-ShellService.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Installer helper source has syntax errors'}
foreach($name in @('Assert-NoLinks','Inside','Set-ProtectedAcl','Assert-Protected','Ensure-ProtectedDirectory','Read-OwnedInstall')) {
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$false)
    if(-not $definition){throw "Missing helper: $name"}
    . ([scriptblock]::Create($definition.Extent.Text))
}
$Installation=[IO.Path]::GetFullPath($Installation).TrimEnd('\')
$Bundle=[IO.Path]::GetFullPath($Bundle).TrimEnd('\')
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Inspect-Native([string]$Root,[switch]$Protected) {
    Write-Verbose "Inspect native assets: $Root"
    $argument=if($Protected){'--inspect-protected'}else{'--inspect'}
    # Start-Process -Wait can wait for the caller's job descendants after UAC.
    # Wait only for this read-only inspector and bound its lifetime.
    $info=[Diagnostics.ProcessStartInfo]::new()
    $info.FileName=Join-Path $Root 'ClassicDeskShell.exe';$info.Arguments=$argument
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $proc=[Diagnostics.Process]::Start($info)
    try {
        if(-not $proc.WaitForExit(30000)){$proc.Kill();throw 'Read-only native inspection timed out'}
        $output=$proc.StandardOutput.ReadToEnd();$errorText=$proc.StandardError.ReadToEnd()
        if($proc.ExitCode -ne 0){throw "Native fixed assets failed verification: $errorText"}
        Write-Verbose $output.Trim()
    } finally {$proc.Dispose()}
}
function Current-Source {
    Write-Verbose 'Validate installed ownership and fixed files'
    $receipt=Read-OwnedInstall $Installation
    if($receipt.RuntimeManifest -ne $oldRuntime){throw 'Repair is limited to the reviewed 0.11.14 service runtime'}
    if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $receipt.OwnerSid){throw 'Repair must retain the installation owner'}
    foreach($item in $receipt.Files) {
        $file=Inside $Installation $item.Path
        if($item.Path -match '^Runtime/AppData/Engine/(ModsWritable|Symbols)/'){continue}
        if((Get-Item -LiteralPath $file).Length -ne $item.Bytes -or (Hash $file) -ne $item.Sha256){throw "Installed fixed asset changed: $($item.Path)"}
    }
    Inspect-Native $Installation -Protected
    return $receipt
}
function Verify-Bundle {
    Write-Verbose 'Validate prepared repair bundle'
    $receipt=Current-Source
    $metadata=Inside $Bundle 'service-package.json'
    if($ExpectedBundleHash -notmatch '^[A-Fa-f0-9]{64}$' -or (Hash $metadata) -ne $ExpectedBundleHash){throw 'Prepared bundle changed since review'}
    if((Get-Item -LiteralPath $metadata).Length -gt 2MB){throw 'Oversized repair receipt'}
    $plan=Get-Content -LiteralPath $metadata -Raw -Encoding UTF8 | ConvertFrom-Json
    if($plan.SchemaVersion -ne 1 -or $plan.State -ne 'prepared-startup-repair' -or $plan.HostSha256 -ne $newHost -or $plan.RuntimeManifest -ne $newRuntime -or
        $plan.RepairSource.Installation -ne $Installation -or $plan.RepairSource.ReceiptSha256 -ne (Hash (Join-Path $Installation 'install-record.json'))){throw 'Unreviewed repair source or code'}
    $allowed=@('ClassicDeskShell.exe',$manifestFile,$modIni,$newDll)
    $expected=@($receipt.Files | ForEach-Object {if($_.Path -eq $oldDll){$newDll}else{$_.Path}})
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($item in $plan.Files) {
        $file=Inside $Bundle $item.Path
        if(-not $seen.Add($item.Path) -or $item.Path -notin $expected -or $item.Bytes -lt 0 -or $item.Bytes -gt 64MB -or
            (Get-Item -LiteralPath $file).Length -ne $item.Bytes -or (Hash $file) -ne $item.Sha256){throw "Invalid repair file: $($item.Path)"}
        if($item.Path -notin $allowed -and $item.Path -notmatch '^Runtime/AppData/Engine/(ModsWritable|Symbols)/') {
            $old=@($receipt.Files | Where-Object Path -eq $item.Path)
            if($old.Count -ne 1 -or $old[0].Sha256 -ne $item.Sha256){throw 'Repair changed unrelated assets'}
        }
    }
    if($seen.Count -ne $expected.Count -or @(Get-ChildItem -LiteralPath $Bundle -Recurse -File -Force).Count -ne $seen.Count+1){throw 'Missing or extra repair files'}
    if((Hash (Join-Path $Bundle 'ClassicDeskShell.exe')) -ne $newHost -or (Hash (Inside $Bundle $newDll)) -ne $newModule -or (Hash (Inside $Bundle $manifestFile)) -ne $newRuntime){throw 'Repair code pin mismatch'}
    $originalIni=[IO.File]::ReadAllText((Inside $Installation $modIni))
    $updatedIni=$originalIni.Replace('taskbar-start-button-position_1.3.2.dll','taskbar-start-button-position_1.3.2-classicdesk.1.dll').Replace('Version=1.3.2','Version=1.3.2-classicdesk.1')
    if([IO.File]::ReadAllText((Inside $Bundle $modIni)) -ne $updatedIni){throw 'Repair changed module settings or process scope'}
    Inspect-Native $Bundle
    return $plan
}
if($Action -eq 'Prepare') {
    $receipt=Current-Source
    if(Test-Path -LiteralPath $Bundle){throw 'Use a new bundle directory'}
    Assert-NoLinks $Bundle
    New-Item -ItemType Directory -Path $Bundle | Out-Null
    foreach($item in $receipt.Files) {
        $relative=if($item.Path -eq $oldDll){$newDll}else{$item.Path}
        $source=Inside $Installation $item.Path
        if($item.Path -eq 'ClassicDeskShell.exe'){$source=Join-Path $repo 'artifacts/startup-fix-20261014/service-build/ClassicDeskShell.exe'}
        if($item.Path -eq $oldDll){$source=Join-Path $repo ('artifacts/startup-fix-20261014/'+[IO.Path]::GetFileName($newDll))}
        if($item.Path -eq $manifestFile){$source=Join-Path $repo 'runtime/windows-x64-assets-startup-fix.json'}
        $target=Inside $Bundle $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
    }
    $iniFile=Inside $Bundle $modIni
    $text=[IO.File]::ReadAllText($iniFile).Replace('taskbar-start-button-position_1.3.2.dll','taskbar-start-button-position_1.3.2-classicdesk.1.dll').Replace('Version=1.3.2','Version=1.3.2-classicdesk.1')
    [IO.File]::WriteAllText($iniFile,$text,[Text.UnicodeEncoding]::new($false,$true))
    $files=@(Get-ChildItem -LiteralPath $Bundle -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{Path=$_.FullName.Substring($Bundle.Length+1).Replace('\','/');Bytes=$_.Length;Sha256=(Hash $_.FullName)}
    })
    $plan=[ordered]@{SchemaVersion=1;State='prepared-startup-repair';HostSha256=$newHost;RuntimeManifest=$newRuntime;Files=$files;
        RepairSource=@{Installation=$Installation;ReceiptSha256=(Hash (Join-Path $Installation 'install-record.json'))}}
    $metadata=Join-Path $Bundle 'service-package.json'
    [IO.File]::WriteAllText($metadata,($plan | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
    $ExpectedBundleHash=Hash $metadata
    $null=Verify-Bundle
    [pscustomobject]@{Prepared=$true;Bundle=$Bundle;ExpectedBundleHash=$ExpectedBundleHash;EngineStarted=$false};return
}
$plan=Verify-Bundle
Write-Verbose 'Bundle verified; check administrator and service state'
if($Action -eq 'Validate'){[pscustomobject]@{Verified=$true;Files=$plan.Files.Count;EngineStarted=$false;HostSettingsChanged=$false};return}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator access is required to stage the verified repair'}
$service=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
if($service.State -ne 'Stopped' -or $service.StartMode -ne 'Disabled' -or $service.StartName -ne 'LocalSystem'){throw 'Repair requires the stopped, disabled owned service'}
$newInstallation=Join-Path $serviceBase ([guid]::NewGuid().ToString('N'))
if(-not $PSCmdlet.ShouldProcess($newInstallation,'Install verified startup repair and enable the next boot; leave the current desktop untouched')){return}
Write-Verbose 'Acquire installation lock and copy to protected directory'
$lock=[IO.File]::Open((Join-Path $serviceBase 'update.lock'),'OpenOrCreate','ReadWrite','None')
try {
    $plan=Verify-Bundle
    $receipt=Current-Source
    Ensure-ProtectedDirectory $newInstallation
    foreach($item in $plan.Files) {
        $source=Inside $Bundle $item.Path;$target=Inside $newInstallation $item.Path
        $parents=[Collections.Generic.Stack[string]]::new()
        for($p=[IO.Path]::GetDirectoryName($target);-not (Test-Path -LiteralPath $p);$p=[IO.Path]::GetDirectoryName($p)){$parents.Push($p)}
        while($parents.Count){Ensure-ProtectedDirectory ($parents.Pop())}
        $bytes=[IO.File]::ReadAllBytes($source)
        $digest=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-','')
        if($bytes.Length -ne $item.Bytes -or $digest -ne $item.Sha256){throw 'Bundle changed while staging'}
        $file=[IO.File]::Open($target,'CreateNew','Write','None')
        try{$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
        Set-ProtectedAcl $target $false
    }
    foreach($folder in @('Runtime/AppData/Engine/ModsWritable','Runtime/AppData/Engine/Symbols')) {
        $cache=Join-Path $newInstallation $folder
        if(-not(Test-Path -LiteralPath $cache)){Ensure-ProtectedDirectory $cache}
        Set-ProtectedAcl $cache $true $receipt.OwnerSid
        Get-ChildItem -LiteralPath $cache -Recurse -Force | ForEach-Object {Set-ProtectedAcl $_.FullName $_.PSIsContainer $receipt.OwnerSid}
    }
    Inspect-Native $newInstallation -Protected
    $image='"'+(Join-Path $newInstallation 'ClassicDeskShell.exe')+'" --service'
    $newReceipt=[ordered]@{SchemaVersion=1;ServiceName=$serviceName;ImagePath=$image;HostSha256=$newHost;RuntimeManifest=$newRuntime;
        BundleSha256=$ExpectedBundleHash;OwnerSid=$receipt.OwnerSid;Source=$receipt.Source;Files=$plan.Files;
        PreviousInstallation=$Installation;RepairSource=$plan.RepairSource;RepairScriptSha256=(Hash $PSCommandPath);InstalledUtc=[DateTime]::UtcNow;Status='startup-repair-next-boot';ServiceStarted=$false;UnifiedMachineLayout=$true}
    if($receipt.TargetProfile){$newReceipt.TargetProfile=$receipt.TargetProfile}
    $record=Join-Path $newInstallation 'install-record.json'
    [IO.File]::WriteAllText($record,($newReceipt | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false));Set-ProtectedAcl $record $false
    $null=Verify-Bundle
    $current=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
    if($current.PathName -ne $service.PathName -or $current.State -ne 'Stopped' -or $current.StartMode -ne 'Disabled'){throw 'Service changed during staging; startup unchanged'}
    $change=Invoke-CimMethod -InputObject $current -MethodName Change -Arguments @{PathName=$image;StartMode='Automatic'}
    if($change.ReturnValue -ne 0){throw "SCM update failed: $($change.ReturnValue)"}
    $after=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
    if($after.PathName -ne $image -or $after.StartMode -ne 'Auto' -or $after.State -ne 'Stopped'){throw 'SCM update result requires inspection'}
    $result=[pscustomobject]@{Staged=$true;Installation=$newInstallation;PreviousInstallation=$Installation;ServiceState=$after.State;StartMode=$after.StartMode;EngineStarted=$false;RestartRequired=$true}
    if($ReportPath){[IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath),($result | ConvertTo-Json),[Text.UTF8Encoding]::new($false))}
    $result
} finally {$lock.Dispose()}
