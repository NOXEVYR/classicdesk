param([Parameter(Mandatory)][string]$ReviewedBundle,[Parameter(Mandatory)][string]$OutputDirectory)
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
function Get-Service {param($Name,$ErrorAction) [pscustomobject]@{Status='Running';StartType='Automatic'} }
function Get-CimInstance {param($ClassName,$Filter) [pscustomobject]@{State='Running';StartMode='Auto';ProcessId=12345;PathName=$fakeState.Image} }
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
'@
# Install replacement functions after the real declarations, before any action.
$code=$source.Replace("`$base=Join-Path `$env:ProgramW6432 'ClassicDesk'",'$base=$testBase')
$marker="if(`$Action -in @('Validate','InstallDisabled','StageUpdate','StageLayout')) {"
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
    $script:fakeState=@{Image='';Writes=0}
    $fakeState.Image='"'+(Join-Path $installed 'ClassicDeskShell.exe')+'" --service'
    $fakeState.Writes=0
    $receipt=[ordered]@{SchemaVersion=1;ServiceName='ClassicDeskShell';ImagePath=$fakeState.Image;HostSha256=$plan.HostSha256;RuntimeManifest=$plan.RuntimeManifest;OwnerSid=$plan.OwnerSid;Source=$plan.Source;Files=$plan.Files}
    $receiptPath=Join-Path $installed 'install-record.json'
    [IO.File]::WriteAllText($receiptPath,($receipt | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
    $targetProfile=$plan.Source.AppliedProfile | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $targetProfile.IconSize=28
    $plan | Add-Member TargetProfile $targetProfile
    $sizing=$plan.Files | Where-Object Path -eq 'Runtime/AppData/Engine/Mods/taskbar-icon-size.ini'
    $sizingFile=Join-Path $bundle $sizing.Path
    [IO.File]::WriteAllText($sizingFile,([IO.File]::ReadAllText($sizingFile) -replace '(?m)^IconSize=\d+','IconSize=28'),[Text.UnicodeEncoding]::new($false,$true))
    $sizing.Bytes=(Get-Item -LiteralPath $sizingFile).Length;$sizing.Sha256=(Get-FileHash -LiteralPath $sizingFile).Hash
    $plan | Add-Member LayoutSource ([pscustomobject]@{Installation=$installed;ReceiptSha256=(Get-FileHash -LiteralPath $receiptPath).Hash})
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
$failed=@($results | Where-Object {-not $_.passed}).Count
$report=[pscustomobject]@{passed=$results.Count-$failed;failed=$failed;realServiceWrites=0;realEngineStarts=0;checks=$results}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'report.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 6
if($failed){exit 1}
