param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$sourcePath=Join-Path $PSScriptRoot '../../scripts/Install-ShellService.ps1'
$source=Get-Content -LiteralPath $sourcePath -Raw
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw ('Installer parse failed: '+($parseErrors.Message -join '; '))}
$functions=@('Initialize-CurrentSessionNative','Assert-ExpectedExplorerIdentity','Read-ClassicDeskServiceState','Invoke-StartCurrentSession') | ForEach-Object {
    $name=$_
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    if(-not $node){throw "Missing installer function: $name"}
    $node.Extent.Text
}
$sandbox=@'
$serviceName='ClassicDeskShell'
$script:fake=$null
function Reset-Fake {
    $root=Join-Path $env:TEMP ('ClassicDesk-current-session-'+[guid]::NewGuid().ToString('N'))
    $script:fake=[ordered]@{
        Root=$root;OwnerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        Image='"'+(Join-Path $root 'ClassicDeskShell.exe')+'" --service'
        State='Stopped';StartMode='Disabled';ProcessId=0;EngineState='running-current-session-unverified';EngineProcessId=4512
        CompatibilityState='compatibility-unverified';Fingerprint=('a'*64);ExplorerPid=1234;SessionId=1;Creation='134048437240000000'
        Calls=[Collections.Generic.List[string]]::new();Arguments=@();FailStart=$false;TimeoutStart=$false;MismatchEnginePid=$false;NativeDisablesDuringStart=$false
    }
}
function Read-OwnedInstall($Root) { if($Root -ne $fake.Root){throw 'wrong installation'};[pscustomobject]@{OwnerSid=$fake.OwnerSid;Files=@(1..25)} }
function Assert-InstalledReceiptFiles($Receipt,[string]$Root) { $fake.Calls.Add('hashes') }
function Assert-NoLinks([string]$Path) { }
function Assert-Protected([string]$Path) { }
function Get-ClassicDeskServiceSnapshot { [pscustomobject]@{State=$fake.State;StartMode=$fake.StartMode;ProcessId=[uint32]$fake.ProcessId;Image=$fake.Image} }
function Inspect-Native([string]$Root,[switch]$Protected,[switch]$Compatibility,[switch]$AllowUnverified) {
    if($Compatibility){return [pscustomobject]@{state=$fake.CompatibilityState;fingerprint=$fake.Fingerprint;engineStarted=$false;acceptanceWritten=$false}}
}
function Get-CurrentExplorerIdentity { [pscustomobject]@{ProcessId=$fake.ExplorerPid;SessionId=$fake.SessionId;CurrentSessionId=$fake.SessionId;CreationFileTime=$fake.Creation;ImagePath=(Join-Path $env:SystemRoot 'explorer.exe')} }
function Read-ClassicDeskServiceState([string]$Root) { [pscustomobject]@{State=$fake.EngineState;ProcessId=[uint32]$(if($fake.MismatchEnginePid){$fake.EngineProcessId+1}else{$fake.EngineProcessId});Error=[uint32]0;LastWriteTimeUtc=[DateTime]::UtcNow} }
function Invoke-ClassicDeskScm([string]$Operation,[string[]]$Arguments=@()) {
    $fake.Calls.Add($Operation)
    switch($Operation) {
        'Manual' {$fake.StartMode='Manual'}
        'Disabled' {$fake.StartMode='Disabled'}
        'Start' {
            $fake.Arguments=@($Arguments)
            if($fake.FailStart){throw 'synthetic start failure'}
            $fake.State='Running';$fake.StartMode=if($fake.NativeDisablesDuringStart){'Disabled'}else{'Manual'};$fake.ProcessId=4512
        }
        'Stop' {$fake.State='Stopped';$fake.ProcessId=0}
    }
}
function Wait-ClassicDeskServiceState([string]$Wanted,[int]$TimeoutSeconds=45) {
    if($Wanted -eq 'Running' -and $fake.TimeoutStart){throw 'synthetic startup timeout'}
    if($Wanted -eq 'Running' -and $fake.State -ne 'Running'){throw 'synthetic not running'}
    if($Wanted -eq 'Stopped' -and $fake.State -ne 'Stopped'){throw 'synthetic not stopped'}
    [pscustomobject]@{State=$fake.State;StartMode=$fake.StartMode;ProcessId=[uint32]$fake.ProcessId;Image=$fake.Image}
}
function Read-ClassicDeskServiceState([string]$Root) { [pscustomobject]@{State=$fake.EngineState;ProcessId=[uint32]$(if($fake.MismatchEnginePid){$fake.EngineProcessId+1}else{$fake.EngineProcessId});Error=[uint32]0} }
function Stop-ClassicDeskServiceBounded([int]$TimeoutSeconds=20) { $fake.Calls.Add('bounded-stop');$fake.State='Stopped';$fake.ProcessId=0 }
'@
Invoke-Expression ($sandbox+"`n"+($functions -join "`n"))
$realReadServiceState=(Get-Item Function:\Read-ClassicDeskServiceState).ScriptBlock
Invoke-Expression 'function Read-ClassicDeskServiceState([string]$Root) { [pscustomobject]@{State=$fake.EngineState;ProcessId=[uint32]$(if($fake.MismatchEnginePid){$fake.EngineProcessId+1}else{$fake.EngineProcessId});Error=[uint32]0;LastWriteTimeUtc=[DateTime]::UtcNow} }'
$checks=[Collections.Generic.List[object]]::new()
function Test([string]$Name,[scriptblock]$Body) {
    try {Reset-Fake;& $Body;$checks.Add([pscustomobject]@{name=$Name;passed=$true;error=''})}
    catch {$checks.Add([pscustomobject]@{name=$Name;passed=$false;error=$_.Exception.Message})}
}
function Check($Value) {if(-not $Value){throw 'Assertion failed'}}
function Reject([scriptblock]$Body,[string]$Expected) {
    $failed=$false
    try {& $Body | Out-Null} catch {if($_.Exception.Message -notlike ('*'+$Expected+'*')){throw};$failed=$true}
    Check $failed
}
Test 'SCM wrappers compile and local manager accepts a read-only connection' {
    Initialize-CurrentSessionNative
    Check ([bool]('ClassicDeskCurrentSessionNative' -as [type]))
    $flags=[Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::NonPublic
    Check ([bool][ClassicDeskCurrentSessionNative].GetMethod('OpenSCManagerW',$flags))
    Check ([bool][ClassicDeskCurrentSessionNative].GetMethod('ChangeServiceConfigW',$flags))
    Check ([bool][ClassicDeskCurrentSessionNative].GetMethod('ChangeStartType',[Reflection.BindingFlags]::Static -bor [Reflection.BindingFlags]::Public))
    $manager=[ClassicDeskCurrentSessionNative]::OpenLocalManager(0x0001)
    Check ($manager -ne [IntPtr]::Zero)
    [void][ClassicDeskCurrentSessionNative]::CloseServiceHandle($manager)
}
Test 'native service state parser reads the exact one-session state and PID' {
    $fixture=Join-Path $env:TEMP ('ClassicDesk-state-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $statePath=Join-Path $fixture 'service-state.ini'
    try {
        [IO.File]::WriteAllText($statePath,"[Service]`r`nProcessId=4512`r`nBootElapsedMs=123`r`nError=0`r`nState=running-current-session-unverified`r`n",[Text.Encoding]::ASCII)
        $state=& $realReadServiceState $fixture
        Check ($state.State -eq 'running-current-session-unverified' -and $state.ProcessId -eq 4512 -and $state.Error -eq 0 -and $state.LastWriteTimeUtc)
    } finally {Remove-Item -LiteralPath $fixture -Force -Recurse -ErrorAction SilentlyContinue}
}
Test 'one-session start passes exactly five bound arguments and remains disabled' {
    $json=Invoke-StartCurrentSession $fake.Root 1234 1 $fake.Creation $fake.Fingerprint | ConvertFrom-Json
    Check ($json.State -eq 'running-current-session-unverified' -and $json.CurrentSessionStarted -and $json.StartMode -eq 'Disabled')
    Check ($json.ExplorerPid -eq 1234 -and $json.SessionId -eq 1 -and -not $json.StartupAcceptanceWritten)
    Check ($fake.Arguments.Count -eq 5 -and $fake.Arguments[0] -eq 'session-only' -and $fake.Arguments[1] -eq '1234' -and $fake.Arguments[2] -eq '1' -and $fake.Arguments[3] -eq $fake.Creation -and $fake.Arguments[4] -ceq $fake.Fingerprint)
    Check ($fake.StartMode -eq 'Disabled' -and $fake.State -eq 'Running')
}
Test 'native host may disable next boot before reporting its live session state' {
    $fake.NativeDisablesDuringStart=$true
    $json=Invoke-StartCurrentSession $fake.Root 1234 1 $fake.Creation $fake.Fingerprint | ConvertFrom-Json
    Check ($json.CurrentSessionStarted -and $json.StartMode -eq 'Disabled' -and $fake.StartMode -eq 'Disabled' -and $fake.State -eq 'Running')
}
Test 'wrong installation owner is rejected before service mutation' {
    $fake.OwnerSid='S-1-5-21-999-999-999-1001'
    Reject {Invoke-StartCurrentSession $fake.Root 1234 1 $fake.Creation $fake.Fingerprint} 'owner'
    Check ($fake.Calls.Count -eq 0)
}
Test 'non-stopped or non-disabled service is rejected before service mutation' {
    $fake.State='Running';$fake.StartMode='Disabled';$fake.ProcessId=12
    Reject {Invoke-StartCurrentSession $fake.Root 1234 1 $fake.Creation $fake.Fingerprint} 'Stopped and Disabled'
    Check ($fake.Calls.Count -eq 0)
}
Test 'changed native fingerprint is rejected before service mutation' {
    $fake.Fingerprint='b'*64
    Reject {Invoke-StartCurrentSession $fake.Root 1234 1 $fake.Creation ('a'*64)} 'fingerprint differs'
    Check ($fake.Calls.Count -eq 1 -and $fake.Calls[0] -eq 'hashes')
}
Test 'stale GetShellWindow Explorer identity is rejected before service mutation' {
    $fake.ExplorerPid=999
    Reject {Invoke-StartCurrentSession $fake.Root 1234 1 $fake.Creation $fake.Fingerprint} 'no longer matches'
    Check ($fake.Calls -notcontains 'Manual' -and $fake.Calls -notcontains 'Start')
}
Test 'timeout requests current service stop and always disables next boot' {
    $fake.TimeoutStart=$true
    Reject {Invoke-StartCurrentSession $fake.Root 1234 1 $fake.Creation $fake.Fingerprint} 'startup timeout'
    Check ($fake.Calls -contains 'bounded-stop' -and $fake.Calls[-1] -eq 'Disabled' -and $fake.StartMode -eq 'Disabled')
}
Test 'SCM start exception requests stop and always disables next boot' {
    $fake.FailStart=$true
    Reject {Invoke-StartCurrentSession $fake.Root 1234 1 $fake.Creation $fake.Fingerprint} 'synthetic start failure'
    Check ($fake.Calls -contains 'bounded-stop' -and $fake.Calls[-1] -eq 'Disabled' -and $fake.StartMode -eq 'Disabled')
}
Test 'native PID mismatch is not reported as a successful start' {
    $fake.MismatchEnginePid=$true
    Reject {Invoke-StartCurrentSession $fake.Root 1234 1 $fake.Creation $fake.Fingerprint} 'did not confirm'
    Check ($fake.Calls -contains 'bounded-stop' -and $fake.Calls[-1] -eq 'Disabled')
}
Test 'Explorer creation time must be decimal and match exactly' {
    $observed=Get-CurrentExplorerIdentity
    Reject {Assert-ExpectedExplorerIdentity $observed 1234 1 'not-a-filetime'} 'creation FILETIME is invalid'
    Reject {Assert-ExpectedExplorerIdentity $observed 4321 1 $fake.Creation} 'no longer matches'
    Assert-ExpectedExplorerIdentity $observed 1234 1 $fake.Creation
}
$failed=@($checks | Where-Object {-not $_.passed}).Count
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report=[pscustomobject]@{passed=$checks.Count-$failed;failed=$failed;realScmWrites=0;realServiceStarts=0;engineStarts=0;checks=$checks}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'current-session-installer.json') -Encoding UTF8
$report | ConvertTo-Json -Depth 5
if($failed){exit 1}
