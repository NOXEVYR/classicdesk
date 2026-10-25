param([Parameter(Mandatory)][string]$ReviewedBundle,[Parameter(Mandatory)][string]$OutputDirectory,[string]$PreviousHost)
$ErrorActionPreference='Stop'
$ReviewedBundle=[IO.Path]::GetFullPath($ReviewedBundle)
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new isolated directory'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../scripts/Install-ShellService.ps1') -Raw
$results=[Collections.Generic.List[object]]::new()
# Execute the real installer control flow against fixture files. Every machine-facing
# primitive is replaced in this child script scope; no SCM, registry, ACL or engine writes.
$mocks=@'
function Get-Service {param($Name,$ErrorAction) if($Name -eq 'Windhawk'){return $null};[pscustomobject]@{Status=$fakeState.State;StartType=if($fakeState.StartMode -eq 'Auto'){'Automatic'}else{$fakeState.StartMode}} }
function Get-CimInstance {param($ClassName,$Filter) [pscustomobject]@{State=$fakeState.State;StartMode=$fakeState.StartMode;ProcessId=$fakeState.ProcessId;PathName=$fakeState.Image} }
function Invoke-CimMethod {param($InputObject,$MethodName,$Arguments)
    if($MethodName -ne 'Change' -or $Arguments.Count -ne 1 -or -not $Arguments.PathName){throw 'Unexpected SCM mutation'}
    $fakeState.Image=$Arguments.PathName;$fakeState.Writes++
    [pscustomobject]@{ReturnValue=0}
}
function Get-ItemProperty {param($LiteralPath) [pscustomobject]@{ImagePath=$fakeState.Image;ObjectName='LocalSystem'} }
function Start-Process {param($FilePath,$ArgumentList,[switch]$Wait,[switch]$PassThru,$WindowStyle)
    if($ArgumentList -ne '--inspect-protected'){throw 'Attempt to start an engine'}
    [pscustomobject]@{ExitCode=0}
}
function Set-ProtectedAcl {param($Path,$Directory,$CacheSid) }
function Assert-Protected {param($Path) if(-not [IO.Path]::GetFullPath($Path).StartsWith($testBase,[StringComparison]::OrdinalIgnoreCase)){throw 'Escaped fixture'} }
function Inspect-Native {param($Root,[switch]$Protected,[switch]$Compatibility)
    if($Compatibility){Assert-CompatibilityResult ([pscustomobject]@{state=$fakeState.Compatibility;engineStarted=$false;acceptanceWritten=$false})}
}
function Assert-ExplorerStartupReady {param($Receipt) if(-not $Receipt.OwnerSid){throw 'Fixture owner missing'} }
function Set-Service {param($Name,$StartupType) if($Name -ne 'ClassicDeskShell' -or $StartupType -ne 'Automatic'){throw 'Unexpected startup mutation'};$fakeState.Enabled=$true;$fakeState.StartMode='Auto' }
function New-ItemProperty {param($LiteralPath,$Name,$Value,$PropertyType,[switch]$Force) if($Name -ne 'DelayedAutoStart' -or $Value -ne 0){throw 'Unexpected registry mutation'} }
'@
# Install replacement functions after the real declarations, before any action.
$code=$source.Replace("`$base=Join-Path `$env:ProgramW6432 'ClassicDesk'",'$base=$testBase')
$marker="if(`$Action -in @('Validate','InstallDisabled','StageUpdate','StageLayout','StageHost')) {"
$code=$code.Replace($marker,$mocks+"`n"+$marker)
$code=$code.Replace("if(-not `$identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))",'if($false)')
$harness=Join-Path $OutputDirectory 'isolated-installer.ps1'
[IO.File]::WriteAllText($harness,$code,[Text.UTF8Encoding]::new($false))
function Test([string]$Name,[scriptblock]$Body) {
    try {& $Body;$results.Add([pscustomobject]@{name=$Name;passed=$true;error=''})}
    catch {$results.Add([pscustomobject]@{name=$Name;passed=$false;error=$_.Exception.Message})}
}
function Check($Value) {if(-not $Value){throw 'Assertion failed'}}
function Fixture {
    $script:testBase=Join-Path $OutputDirectory ([guid]::NewGuid().ToString('N'))
    $installed=Join-Path $testBase 'ShellService/installed'
    $bundle=Join-Path $testBase 'prepared'
    New-Item -ItemType Directory -Path $installed -Force | Out-Null
    Copy-Item -LiteralPath $ReviewedBundle -Destination $bundle -Recurse
    $plan=Get-Content -LiteralPath (Join-Path $bundle 'service-package.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach($item in $plan.Files){$to=Join-Path $installed $item.Path;New-Item -ItemType Directory -Path (Split-Path $to) -Force | Out-Null;Copy-Item -LiteralPath (Join-Path $bundle $item.Path) -Destination $to}
    $script:fakeState=@{Image='';Writes=0;State='Running';StartMode='Auto';ProcessId=12345;Compatibility='compatibility-unverified';Enabled=$false}
    $fakeState.Image='"'+(Join-Path $installed 'ClassicDeskShell.exe')+'" --service'
    $fakeState.Writes=0
    $fakeState.LocalAppData=Join-Path $testBase 'LocalAppData'
    $journal=Join-Path $fakeState.LocalAppData ('ClassicDesk/NativeTransactions/'+([guid]$plan.Source.JournalId).ToString('N')+'.json')
    New-Item -ItemType Directory -Path (Split-Path $journal) -Force | Out-Null
    [IO.File]::WriteAllText($journal,'synthetic-recovery-journal')
    $plan.Source.Revision=(Get-FileHash -LiteralPath $journal).Hash
    $receipt=[ordered]@{SchemaVersion=1;ServiceName='ClassicDeskShell';ImagePath=$fakeState.Image;HostSha256=$plan.HostSha256;RuntimeManifest=$plan.RuntimeManifest;OwnerSid=$plan.OwnerSid;Source=$plan.Source;Files=$plan.Files}
    $receiptPath=Join-Path $installed 'install-record.json'
    [IO.File]::WriteAllText($receiptPath,($receipt | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    $targetProfile=$plan.Source.AppliedProfile | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $targetProfile.IconSize=28
    $plan | Add-Member TargetProfile $targetProfile -Force
    $sizing=$plan.Files | Where-Object Path -eq 'Runtime/AppData/Engine/Mods/taskbar-icon-size.ini'
    $sizingFile=Join-Path $bundle $sizing.Path
    [IO.File]::WriteAllText($sizingFile,([IO.File]::ReadAllText($sizingFile) -replace '(?m)^IconSize=\d+','IconSize=28'),[Text.UnicodeEncoding]::new($false,$true))
    $sizing.Bytes=(Get-Item -LiteralPath $sizingFile).Length;$sizing.Sha256=(Get-FileHash -LiteralPath $sizingFile).Hash
    $plan | Add-Member -Force LayoutSource ([pscustomobject]@{Installation=$installed;ReceiptSha256=(Get-FileHash -LiteralPath $receiptPath).Hash})
    [pscustomobject]@{Installed=$installed;Bundle=$bundle;Plan=$plan;Receipt=$receiptPath}
}
function Seal($Fixture) {
    $file=Join-Path $Fixture.Bundle 'service-package.json'
    [IO.File]::WriteAllText($file,($Fixture.Plan | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    (Get-FileHash -LiteralPath $file).Hash
}
function Reject([scriptblock]$Run,[string]$Expected){$rejected=$false;try {& $Run | Out-Null}catch{if($_.Exception.Message -notlike ('*'+$Expected+'*')){throw};$rejected=$true};Check $rejected;Check ($fakeState.Writes -eq 0)}
Test 'stage changes only next-start path and retains previous installation' {
    $f=Fixture;$hash=Seal $f
    $result=& $harness -Action StageLayout -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash
    Check ($fakeState.Writes -eq 1);Check ($result.ServiceStarted -eq $false);Check ($result.PreviousInstallation -eq $f.Installed)
    $new=Get-Content -LiteralPath (Join-Path $result.Installation 'install-record.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($new.PreviousProcessId -eq 12345);Check ($new.Status -eq 'staged-next-boot');Check ($new.TargetProfile.IconSize -eq $f.Plan.TargetProfile.IconSize)
    Check (Test-Path -LiteralPath $f.Receipt)
}
Test 'stale receipt rejected before SCM mutation' {
    $f=Fixture;$f.Plan.LayoutSource.ReceiptSha256='A'*64;$hash=Seal $f
    Reject {& $harness -Action StageLayout -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash} 'Scheduled layout changed'
}
Test 'disabled layout stages without enabling and later accepted receipt can enable' {
    $f=Fixture;$fakeState.State='Stopped';$fakeState.StartMode='Disabled';$fakeState.ProcessId=0;$hash=Seal $f
    $result=& $harness -Action StageLayout -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash
    Check ($fakeState.Writes -eq 1);Check ($fakeState.StartMode -eq 'Disabled');Check (-not $fakeState.Enabled)
    $previousLocalData=$env:LOCALAPPDATA
    try {
        $env:LOCALAPPDATA=$fakeState.LocalAppData
        $blocked=$false
        try {& $harness -Action EnableNextBoot -Installation $result.Installation | Out-Null} catch {if($_.Exception.Message -notlike '*compatibility has not been accepted*'){throw};$blocked=$true}
        Check $blocked;Check (-not $fakeState.Enabled);Check ($fakeState.StartMode -eq 'Disabled')
        $fakeState.Compatibility='compatible'
        & $harness -Action EnableNextBoot -Installation $result.Installation | Out-Null
        Check $fakeState.Enabled;Check ($fakeState.State -eq 'Stopped')
    } finally {$env:LOCALAPPDATA=$previousLocalData}
}
Test 'compatibility query success is not acceptance' {
    $functions=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)
    $definition=$functions.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Assert-CompatibilityResult'},$false)
    . ([scriptblock]::Create($definition.Extent.Text))
    foreach($state in @('compatibility-unverified','compatibility-changed','compatibility-unavailable','')) {
        $blocked=$false;try {Assert-CompatibilityResult ([pscustomobject]@{state=$state;engineStarted=$false;acceptanceWritten=$false})}catch{$blocked=$true};Check $blocked
    }
    Assert-CompatibilityResult ([pscustomobject]@{state='compatible';engineStarted=$false;acceptanceWritten=$false})
}
Test 'module process scope cannot be changed even with resealed metadata' {
    $f=Fixture;$entry=$f.Plan.Files | Where-Object Path -eq 'Runtime/AppData/Engine/Mods/taskbar-start-button-position.ini'
    $path=Join-Path $f.Bundle $entry.Path
    $text=[IO.File]::ReadAllText($path).Replace('Include=explorer.exe|StartMenuExperienceHost.exe','Include=*')
    [IO.File]::WriteAllText($path,$text,[Text.UnicodeEncoding]::new($false,$true))
    $entry.Bytes=(Get-Item -LiteralPath $path).Length;$entry.Sha256=(Get-FileHash -LiteralPath $path).Hash;$hash=Seal $f
    Reject {& $harness -Action StageLayout -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash} 'module execution policy'
}
Test 'changed installed code rejects update' {
    $f=Fixture;[IO.File]::WriteAllText((Join-Path $f.Installed 'ClassicDeskShell.exe'),'modified');$hash=Seal $f
    Reject {& $harness -Action StageLayout -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash} 'Service executable changed'
}
Test 'what-if does not change service or create a protected version' {
    $f=Fixture;$hash=Seal $f
    & $harness -Action StageLayout -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash -WhatIf | Out-Null
    Check ($fakeState.Writes -eq 0);Check (@(Get-ChildItem -LiteralPath (Join-Path $testBase 'ShellService') -Directory).Count -eq 1)
}
if($PreviousHost) {
    if((Get-FileHash -LiteralPath $PreviousHost).Hash -ne '312445A8EACF90EC793A9B8C4640A2AF53CCA3CAE6C0EA5DE35365F17B1BC4A9'){throw 'Wrong previous-host fixture'}
    function HostFixture {
        $f=Fixture
        $receipt=Get-Content -LiteralPath $f.Receipt -Raw -Encoding UTF8 | ConvertFrom-Json
        $f.Plan.TargetProfile=$receipt.Source.AppliedProfile
        foreach($item in $f.Plan.Files) {
            if($item.Path -eq 'ClassicDeskShell.exe'){continue}
            Copy-Item -LiteralPath (Join-Path $f.Installed $item.Path) -Destination (Join-Path $f.Bundle $item.Path) -Force
            $original=$receipt.Files | Where-Object Path -eq $item.Path
            $item.Bytes=$original.Bytes;$item.Sha256=$original.Sha256
        }
        Copy-Item -LiteralPath $PreviousHost -Destination (Join-Path $f.Installed 'ClassicDeskShell.exe') -Force
        $receipt.HostSha256=(Get-FileHash -LiteralPath $PreviousHost).Hash
        $old=$receipt.Files | Where-Object Path -eq 'ClassicDeskShell.exe'
        $old.Sha256=$receipt.HostSha256;$old.Bytes=(Get-Item -LiteralPath $PreviousHost).Length
        [IO.File]::WriteAllText($f.Receipt,($receipt | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
        $f.Plan.LayoutSource.ReceiptSha256=(Get-FileHash -LiteralPath $f.Receipt).Hash
        $fakeState.State='Stopped';$fakeState.StartMode='Disabled';$fakeState.ProcessId=0
        return $f
    }
    Test 'host upgrade stages pinned host and retains profile without starting' {
        $f=HostFixture;$hash=Seal $f
        $result=& $harness -Action StageHost -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash
        Check ($fakeState.Writes -eq 1 -and $fakeState.StartMode -eq 'Disabled' -and -not $result.ServiceStarted)
        $new=Get-Content -LiteralPath (Join-Path $result.Installation 'install-record.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        Check ($new.HostSha256 -eq $f.Plan.HostSha256 -and $new.PreviousInstallation -eq $f.Installed)
        Check (-not (Test-Path -LiteralPath (Join-Path $result.Installation 'cold-diagnostic.consumed.bin')))
    }
    Test 'host upgrade validation performs no SCM writes' {
        $f=HostFixture;$hash=Seal $f
        $r=& $harness -Action StageHost -ValidateOnly -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash
        Check ($r.Verified -and $fakeState.Writes -eq 0)
    }
    Test 'host upgrade refuses to stop running desktop' {
        $f=HostFixture;$fakeState.State='Running';$fakeState.ProcessId=123;$hash=Seal $f
        Reject {& $harness -Action StageHost -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash} 'stopped disabled'
    }
    Test 'host upgrade rejects profile change' {
        $f=HostFixture;$f.Plan.TargetProfile=$f.Plan.TargetProfile | ConvertTo-Json | ConvertFrom-Json
        $f.Plan.TargetProfile.IconSize=39;$hash=Seal $f
        Reject {& $harness -Action StageHost -Bundle $f.Bundle -Installation $f.Installed -ExpectedBundleHash $hash} 'exact saved profile'
    }
}
$failed=@($results | Where-Object {-not $_.passed}).Count
$report=[pscustomobject]@{passed=$results.Count-$failed;failed=$failed;realServiceWrites=0;realEngineStarts=0;checks=$results}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'report.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 6
if($failed){exit 1}
