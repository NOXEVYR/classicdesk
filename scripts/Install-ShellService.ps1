[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('Validate','Inspect','InstallDisabled','StageUpdate','StageLayout','EnableNextBoot','DisableNextBoot','RemoveStopped')][string]$Action,
    [string]$Bundle,
    [string]$Installation,
    [string]$ExpectedBundleHash
)
# Do not inherit incompatible PowerShell 7 modules when launched by a .NET process.
$env:PSModulePath=$PSHOME+'\Modules'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$ErrorActionPreference='Stop'
$serviceName='ClassicDeskShell'
$expectedHost='14603CC429E3A109AAD6133E163131DCEC890F2F8F7D614554B435CE2468A67D'
$expectedRuntime='02FB7D1F8FD886CBEF1569DA87F481ACD433B1E8C8BD064A4B7EED66A253FA2C'
$expectedScope='%SystemRoot%\System32\winlogon.exe|%SystemRoot%\System32\userinit.exe|%SystemRoot%\explorer.exe|%SystemRoot%\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe'
$adminSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$usersSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
$base=Join-Path $env:ProgramW6432 'ClassicDesk'
$serviceBase=Join-Path $base 'ShellService'

function Assert-NoLinks([string]$Path) {
    for($current=[IO.Path]::GetFullPath($Path);$current;$current=[IO.Path]::GetDirectoryName($current)) {
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Reparse path rejected'}
    }
}
function Inside([string]$Root,[string]$Relative) {
    if([IO.Path]::IsPathRooted($Relative) -or ($Relative -split '[/\\]' | Where-Object {$_ -in @('','..','.') -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0})) {throw 'Invalid manifest path'}
    $target=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if(-not $target.StartsWith($Root.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Path escaped bundle'}
    Assert-NoLinks $target
    return $target
}
function Read-Bundle([string]$Root) {
    Assert-NoLinks $Root
    $metadata=Join-Path $Root 'service-package.json'
    Assert-NoLinks $metadata
    if($ExpectedBundleHash -notmatch '^[0-9A-Fa-f]{64}$' -or (Get-FileHash -LiteralPath $metadata).Hash -ne $ExpectedBundleHash) {throw 'Bundle changed since the read-only review'}
    if((Get-Item -LiteralPath $metadata).Length -gt 2MB) {throw 'Oversized bundle manifest'}
    $plan=Get-Content -LiteralPath $metadata -Encoding UTF8 -Raw | ConvertFrom-Json
    if($plan.SchemaVersion -ne 1 -or $plan.State -ne 'prepared-not-installed' -or $plan.ServiceName -ne $serviceName -or $plan.HostSha256 -ne $expectedHost -or $plan.RuntimeManifest -ne $expectedRuntime -or $plan.EngineInclude -ne $expectedScope) {throw 'Unreviewed service bundle'}
    if($plan.Files.Count -lt 25 -or $plan.Files.Count -gt 512) {throw 'Invalid file count'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $total=0L
    foreach($item in $plan.Files) {
        $path=Inside $Root $item.Path
        if(-not $seen.Add($item.Path.Replace('\','/')) -or $item.Bytes -lt 0 -or $item.Bytes -gt 64MB -or $item.Sha256 -notmatch '^[0-9A-F]{64}$') {throw 'Invalid asset entry'}
        $total+=$item.Bytes
        if($total -gt 256MB -or (Get-Item -LiteralPath $path).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $item.Sha256) {throw "Asset verification failed: $($item.Path)"}
    }
    $actual=@(Get-ChildItem -LiteralPath $Root -Recurse -Force -File)
    if($actual.Count -ne ($seen.Count+1)) {throw 'Unexpected files in service bundle'}
    foreach($file in $actual) {
        Assert-NoLinks $file.FullName
        $relative=$file.FullName.Substring($Root.TrimEnd('\').Length+1).Replace('\','/')
        if($relative -ne 'service-package.json' -and -not $seen.Contains($relative)) {throw 'Unlisted bundle file'}
    }
    if((Get-FileHash -LiteralPath (Join-Path $Root 'ClassicDeskShell.exe')).Hash -ne $expectedHost -or
       (Get-FileHash -LiteralPath (Join-Path $Root 'Runtime/runtime-assets-manifest.json')).Hash -ne $expectedRuntime) {throw 'Pinned code manifest mismatch'}
    $runtime=Get-Content -LiteralPath (Join-Path $Root 'Runtime/runtime-assets-manifest.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    foreach($asset in $runtime.assets) {
        $item=@($plan.Files | Where-Object Path -eq ('Runtime/'+$asset.path))
        if($item.Count -ne 1 -or $item[0].Bytes -ne $asset.bytes -or $item[0].Sha256 -ne $asset.sha256) {throw 'Runtime asset differs from pinned manifest'}
    }
    return $plan
}
function Assert-LayoutSource($Plan) {
    if(-not $Plan.LayoutSource -or -not $Plan.TargetProfile){throw 'Layout update requires source and target profile'}
    $previous=Read-OwnedInstall $Plan.LayoutSource.Installation
    $receiptPath=Join-Path $Plan.LayoutSource.Installation 'install-record.json'
    if((Get-FileHash -LiteralPath $receiptPath).Hash -ne $Plan.LayoutSource.ReceiptSha256){throw 'Scheduled layout changed; review again'}
    if($previous.RuntimeManifest -ne $expectedRuntime -or $previous.OwnerSid -ne $Plan.OwnerSid -or
       ($previous.Source | ConvertTo-Json -Compress -Depth 10) -ne ($Plan.Source | ConvertTo-Json -Compress -Depth 10)){throw 'Layout update must preserve ownership and original recovery source'}
    if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $Plan.OwnerSid){throw 'Update account differs from installation owner'}
    if($previous.Files.Count -ne $Plan.Files.Count){throw 'Layout update cannot add code or files'}
    foreach($item in $Plan.Files) {
        $original=@($previous.Files | Where-Object Path -eq $item.Path)
        if($original.Count -ne 1){throw 'Unknown layout update file'}
        $oldFile=Inside $Plan.LayoutSource.Installation $item.Path
        $newFile=Inside $Bundle $item.Path
        if($item.Path -match '^Runtime/AppData/Engine/(ModsWritable|Symbols)/'){continue}
        if((Get-FileHash -LiteralPath $oldFile).Hash -ne $original[0].Sha256){throw 'Installed fixed file changed'}
        if($item.Path -match '^Runtime/AppData/Engine/Mods/[^/]+\.ini$') {
            # Preserve code selection, process scope and all execution-policy headers.
            $oldText=[IO.File]::ReadAllText($oldFile)
            $newText=[IO.File]::ReadAllText($newFile)
            $oldParts=$oldText -split '\[Settings\]\r?\n',2
            $newParts=$newText -split '\[Settings\]\r?\n',2
            if($oldParts.Count -ne 2 -or $newParts.Count -ne 2 -or
               ($oldParts[0] -replace '(?m)^Disabled=[01]\r?$','Disabled=') -ne ($newParts[0] -replace '(?m)^Disabled=[01]\r?$','Disabled=') -or
               $newParts[1] -match '(?m)^\['){throw 'Layout update changed module execution policy'}
        } elseif($item.Sha256 -ne $original[0].Sha256 -or $item.Bytes -ne $original[0].Bytes){throw 'Layout update changed a fixed asset'}
    }
    return $previous
}
function Assert-SourceCurrent($Plan) {
    if($Action -eq 'StageLayout') {$null=Assert-LayoutSource $Plan;return}
    if($Plan.LayoutSource -or $Plan.TargetProfile){throw 'Use StageLayout for a layout update'}
    if([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $Plan.OwnerSid) {throw 'Install must retain the preparing user identity'}
    $journal=Join-Path $env:LOCALAPPDATA ('ClassicDesk/NativeTransactions/'+([guid]$Plan.Source.JournalId).ToString('N')+'.json')
    Assert-NoLinks $journal
    if((Get-FileHash -LiteralPath $journal).Hash -ne $Plan.Source.Revision) {throw 'Applied profile/recovery record changed; prepare again'}
    if(Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Startup')) 'ClassicDesk-resume.vbs')) {throw 'Old login resume is still installed'}
    if(Get-Service -Name Windhawk -ErrorAction SilentlyContinue) {throw 'An existing Windhawk service must not be replaced'}
}
function Set-ProtectedAcl([string]$Path,[bool]$Directory,[string]$CacheSid='') {
    Assert-NoLinks $Path
    $acl=if($Directory){[Security.AccessControl.DirectorySecurity]::new()}else{[Security.AccessControl.FileSecurity]::new()}
    $acl.SetOwner($adminSid);$acl.SetAccessRuleProtection($true,$false)
    $inherit=if($Directory){[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'}else{[Security.AccessControl.InheritanceFlags]::None}
    foreach($sid in @($adminSid,$systemSid)) {$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl',$inherit,'None','Allow'))}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($usersSid,'ReadAndExecute',$inherit,'None','Allow'))
    if($CacheSid){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($CacheSid),'Modify',$inherit,'None','Allow'))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Assert-Protected([string]$Path) {
    Assert-NoLinks $Path
    $acl=Get-Acl -LiteralPath $Path
    if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @($adminSid.Value,$systemSid.Value)) {throw 'Installation owner is not protected'}
    $write=[int][Security.AccessControl.FileSystemRights]'Write,Delete,ChangePermissions,TakeOwnership'
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if($rule.AccessControlType -eq 'Allow' -and ([int]$rule.FileSystemRights -band $write) -and $rule.IdentityReference.Value -notin @($adminSid.Value,$systemSid.Value)) {throw 'Installation directory is writable by an unprivileged account'}
    }
}
function Ensure-ProtectedDirectory([string]$Path) {
    Assert-NoLinks $Path
    if(Test-Path -LiteralPath $Path){Assert-Protected $Path;return}
    New-Item -ItemType Directory -Path $Path -ErrorAction Stop | Out-Null
    Set-ProtectedAcl $Path $true
}
function Read-OwnedInstall([string]$Root,[bool]$Previous=$false) {
    $Root=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    if(-not $Root.StartsWith($serviceBase+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Not a ClassicDesk service installation'}
    Assert-Protected $base;Assert-Protected $serviceBase;Assert-Protected $Root
    $receiptPath=Join-Path $Root 'install-record.json'
    Assert-Protected $receiptPath
    if((Get-Item -LiteralPath $receiptPath).Length -gt 2MB){throw 'Oversized install receipt'}
    $receipt=Get-Content -LiteralPath $receiptPath -Encoding UTF8 -Raw | ConvertFrom-Json
    $image='"'+(Join-Path $Root 'ClassicDeskShell.exe')+'" --service'
    $hostPin=if($Previous){'1ABC2CF83F340CE243313294EFD7443B9F33A484F3BEB2A37823CC3886C73545'}else{$expectedHost}
    if($receipt.SchemaVersion -ne 1 -or $receipt.ServiceName -ne $serviceName -or $receipt.ImagePath -ne $image -or $receipt.HostSha256 -ne $hostPin) {throw 'Installation ownership mismatch'}
    $key=Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$serviceName)
    if($key.ImagePath -ne $image -or $key.ObjectName -ne 'LocalSystem') {throw 'Service identity changed; no action taken'}
    if((Get-FileHash -LiteralPath (Join-Path $Root 'ClassicDeskShell.exe')).Hash -ne $hostPin) {throw 'Service executable changed'}
    return $receipt
}

if($Action -in @('Validate','InstallDisabled','StageUpdate','StageLayout')) {
    if(-not $Bundle){throw 'Bundle is required'}
    $Bundle=[IO.Path]::GetFullPath($Bundle).TrimEnd('\')
    $plan=Read-Bundle $Bundle
    Assert-SourceCurrent $plan
    if($Action -eq 'Validate') {
        [pscustomobject]@{Verified=$true;Files=$plan.Files.Count;Bytes=($plan.Files | Measure-Object Bytes -Sum).Sum;ServiceCreated=$false;EngineStarted=$false;UnifiedMachineLayout=$true};return
    }
}
if($Action -eq 'Inspect') {
    if(-not $Installation){throw 'An explicit owned Installation is required'}
    $receipt=Read-OwnedInstall $Installation
    $inspection=Start-Process -FilePath (Join-Path $Installation 'ClassicDeskShell.exe') -ArgumentList '--inspect-protected' -WindowStyle Hidden -Wait -PassThru
    if($inspection.ExitCode -ne 0){throw 'Protected installation inspection failed'}
    $service=Get-Service -Name $serviceName
    [pscustomobject]@{Installation=$Installation;ServiceState=$service.Status.ToString();StartType=$service.StartType.ToString();AssetsVerified=$true;HostSettingsChanged=$false;EngineStarted=$false};return
}
$identity=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {throw 'Windows administrator confirmation is required; no changes made'}

if($Action -in @('InstallDisabled','StageUpdate','StageLayout')) {
    $previousRoot=$null;$previousReceipt=$null
    if($Action -eq 'StageUpdate') {
        if(-not $Installation){throw 'Current owned installation is required for an update'}
        $previousRoot=[IO.Path]::GetFullPath($Installation).TrimEnd('\')
        $previousReceipt=Read-OwnedInstall $previousRoot $true
        if($previousReceipt.OwnerSid -ne $plan.OwnerSid -or
           ($previousReceipt.Source.AppliedProfile | ConvertTo-Json -Compress -Depth 8) -ne ($plan.Source.AppliedProfile | ConvertTo-Json -Compress -Depth 8)) {throw 'Update must retain the installed owner and applied layout'}
        $beforeService=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
        if($beforeService.StartMode -ne 'Auto' -or $beforeService.State -ne 'Running'){throw 'This update requires the reviewed automatic running service'}
        if($previousReceipt.RuntimeManifest -ne '49C4EF0FB577AC4D053973F46FADD4B3F8AF6948863E63FDCD8B3E3AD5EAF423'){throw 'Unreviewed previous runtime'}
    } elseif($Action -eq 'StageLayout') {
        if(-not $Installation -or [IO.Path]::GetFullPath($Installation).TrimEnd('\') -ne $plan.LayoutSource.Installation){throw 'Layout source installation mismatch'}
        $previousRoot=$plan.LayoutSource.Installation
        $previousReceipt=Assert-LayoutSource $plan
        $beforeService=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
        if($beforeService.State -ne 'Running' -or $beforeService.StartMode -ne 'Auto'){throw 'Layout staging requires an automatic running service'}
    } elseif(Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {throw 'A service with this name already exists; not taking ownership'}
    $Installation=Join-Path $serviceBase ([guid]::NewGuid().ToString('N'))
    if(-not $PSCmdlet.ShouldProcess($Installation,'Prepare protected service files; update only next startup when upgrading; never stop or start the current engine')) {return}
    Ensure-ProtectedDirectory $base;Ensure-ProtectedDirectory $serviceBase;Ensure-ProtectedDirectory $Installation
    # Serialize concurrent staging attempts under the protected installation root.
    $updateLock=[IO.File]::Open((Join-Path $serviceBase 'update.lock'),'OpenOrCreate','ReadWrite','None')
    try {
    Assert-SourceCurrent $plan
    foreach($item in $plan.Files) {
        $source=Inside $Bundle $item.Path;$target=Inside $Installation $item.Path
        $parent=[IO.Path]::GetDirectoryName($target)
        $missing=[Collections.Generic.Stack[string]]::new()
        for($folder=$parent;-not (Test-Path -LiteralPath $folder);$folder=[IO.Path]::GetDirectoryName($folder)){$missing.Push($folder)}
        while($missing.Count){Ensure-ProtectedDirectory ($missing.Pop())}
        $bytes=[IO.File]::ReadAllBytes($source)
        $hash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-','')
        if($bytes.Length -ne $item.Bytes -or $hash -ne $item.Sha256){throw 'Source changed while copying'}
        $file=[IO.File]::Open($target,'CreateNew','Write','None')
        try{$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
        Set-ProtectedAcl $target $false
    }
    foreach($folder in @('Runtime/AppData/Engine/ModsWritable','Runtime/AppData/Engine/Symbols')) {
        $cache=Inside $Installation $folder
        if(-not (Test-Path -LiteralPath $cache)){Ensure-ProtectedDirectory $cache}
        Set-ProtectedAcl $cache $true $plan.OwnerSid
        foreach($entry in Get-ChildItem -LiteralPath $cache -Force -Recurse){Set-ProtectedAcl $entry.FullName $entry.PSIsContainer $plan.OwnerSid}
    }
    $inspection=Start-Process -FilePath (Join-Path $Installation 'ClassicDeskShell.exe') -ArgumentList '--inspect-protected' -WindowStyle Hidden -Wait -PassThru
    if($inspection.ExitCode -ne 0){throw 'Protected copy did not pass native inspection; service not registered'}
    Assert-SourceCurrent $plan
    $image='"'+(Join-Path $Installation 'ClassicDeskShell.exe')+'" --service'
    $receipt=[ordered]@{SchemaVersion=1;ServiceName=$serviceName;ImagePath=$image;HostSha256=$expectedHost;RuntimeManifest=$expectedRuntime;BundleSha256=$ExpectedBundleHash;OwnerSid=$plan.OwnerSid;Source=$plan.Source;Files=$plan.Files;InstalledUtc=[DateTime]::UtcNow;Status='installed-disabled';ServiceStarted=$false;UnifiedMachineLayout=$true}
    if($plan.TargetProfile){$receipt.TargetProfile=$plan.TargetProfile;$receipt.LayoutSource=$plan.LayoutSource}
    if($previousRoot){$receipt.PreviousInstallation=$previousRoot;$receipt.Status='staged-next-boot';$receipt.PreviousProcessId=$beforeService.ProcessId}
    $receiptPath=Join-Path $Installation 'install-record.json'
    [IO.File]::WriteAllText($receiptPath,($receipt | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false));Set-ProtectedAcl $receiptPath $false
    if($previousRoot) {
        $null=Read-OwnedInstall $previousRoot ($Action -eq 'StageUpdate')
        Assert-SourceCurrent $plan
        $current=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
        if($current.ProcessId -ne $beforeService.ProcessId -or $current.State -ne 'Running' -or $current.StartMode -ne 'Auto'){throw 'Service changed during staging; startup path unchanged'}
        $change=Invoke-CimMethod -InputObject $current -MethodName Change -Arguments @{PathName=$image}
        if($change.ReturnValue -ne 0){throw "SCM startup update failed: $($change.ReturnValue)"}
    } else {
        New-Service -Name $serviceName -BinaryPathName $image -DisplayName 'ClassicDesk Shell Loader' -Description 'ClassicDesk verified shell components; settings window is not required. Disable startup to recover on the next boot.' -StartupType Disabled -DependsOn EventLog | Out-Null
    }
    $null=Read-OwnedInstall $Installation
    [pscustomobject]@{Installation=$Installation;Status=$receipt.Status;ServiceStarted=$false;PreviousInstallation=$previousRoot};return
    } finally {$updateLock.Dispose()}
}

if(-not $Installation){throw 'An explicit owned Installation is required'}
$Installation=[IO.Path]::GetFullPath($Installation).TrimEnd('\')
$receipt=Read-OwnedInstall $Installation
$service=Get-Service -Name $serviceName
switch($Action) {
    'EnableNextBoot' {
        if($service.Status -ne 'Stopped'){throw 'Service is already running; not replacing or restarting it'}
        Assert-SourceCurrent $receipt
        foreach($item in $receipt.Files) {
            $path=Inside $Installation $item.Path
            if((Get-Item -LiteralPath $path).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $item.Sha256){throw 'Installed files changed before first activation'}
        }
        $inspection=Start-Process -FilePath (Join-Path $Installation 'ClassicDeskShell.exe') -ArgumentList '--inspect-protected' -WindowStyle Hidden -Wait -PassThru
        if($inspection.ExitCode -ne 0){throw 'Protected installation no longer passes inspection'}
        if($PSCmdlet.ShouldProcess($serviceName,'Enable automatic startup for the next Windows boot; do not start now')) {
            Set-Service -Name $serviceName -StartupType Automatic
            New-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$serviceName) -Name DelayedAutoStart -Value 0 -PropertyType DWord -Force | Out-Null
            [pscustomobject]@{Status='enabled-next-boot';ServiceStarted=$false;FirstFrameVerified=$false}
        }
    }
    'DisableNextBoot' {
        if($PSCmdlet.ShouldProcess($serviceName,'Disable next startup; keep this desktop session intact')) {
            Set-Service -Name $serviceName -StartupType Disabled
            [pscustomobject]@{Status='disabled-next-boot';CurrentServiceState=$service.Status.ToString();ServiceStopped=$false}
        }
    }
    'RemoveStopped' {
        if($service.Status -ne 'Stopped' -or $service.StartType -ne 'Disabled'){throw 'Disable startup and wait for a stopped service before removing registration'}
        if($PSCmdlet.ShouldProcess($serviceName,'Remove only the stopped owned service registration; keep installation and recovery files')) {
            & "$env:SystemRoot/System32/sc.exe" delete $serviceName
            if($LASTEXITCODE -ne 0){throw 'Service registration removal was not confirmed'}
        }
    }
}
