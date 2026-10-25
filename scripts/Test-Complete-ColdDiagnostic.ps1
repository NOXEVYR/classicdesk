[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new isolated output directory'}
$null=New-Item -ItemType Directory -Path $OutputDirectory
. (Join-Path $PSScriptRoot 'Complete-ColdDiagnostic.ps1') -Installation $OutputDirectory -ExpectedHostSha256 ('a'*64)
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[scriptblock]$Body,[switch]$Reject) {
    $rejected=$false
    try {& $Body}catch{$rejected=$true;$message=$_.Exception.Message}
    $ok=if($Reject){$rejected}else{-not $rejected}
    $results.Add([pscustomobject]@{Name=$Name;Passed=$ok;Detail=if($ok){''}else{$message}})
    if(-not $ok){throw "Test failed: $Name $message"}
}
$snapshot=[pscustomobject]@{Installation=$OutputDirectory;HostSha256='a'*64;Fingerprint='b'*64;BootEvidence='monotonic-uptime-reset-v2';PreparedUptimeMs=10800000;TrialId='2'*32;ConsumedSha256='c'*64;InstallRecordSha256='d'*64;ServiceProcessId=123;ServiceCreationFileTimeUtc=[DateTime]::UtcNow.AddMinutes(-5).ToFileTimeUtc().ToString();Compatibility='compatibility-unverified'}
function New-Report {
    $record=$snapshot | ConvertTo-Json | ConvertFrom-Json
    $record | Add-Member Schema 1
    $record | Add-Member Decision 'accepted'
    $record | Add-Member Operator 'SYNTHETIC TEST ONLY'
    $record | Add-Member ObservedUtc ([DateTime]::UtcNow.AddMinutes(-1).ToString('o'))
    $record | Add-Member Evidence @([pscustomobject]@{Path='synthetic.txt';Sha256='e'*64})
    $checks=@{}
    foreach($name in @('ColdBoot','Taskbar','Win10Explorer','Drag','TopClick')){$checks[$name]=[pscustomobject]@{Result='pass';Observation='Synthetic unit fixture, never real acceptance';Evidence=@('synthetic.txt')}}
    $record | Add-Member Checks ([pscustomobject]$checks)
    return $record
}
Check 'explicit synthetic report accepted by pure policy' {Assert-Report (New-Report) $snapshot}
foreach($field in @('Installation','HostSha256','Fingerprint','BootEvidence','PreparedUptimeMs','TrialId','ConsumedSha256','InstallRecordSha256','ServiceProcessId','ServiceCreationFileTimeUtc')) {
    Check "report rejects changed $field" {$r=New-Report;$r.$field='different';Assert-Report $r $snapshot} -Reject
}
foreach($name in @('ColdBoot','Taskbar','Win10Explorer','Drag','TopClick')) {
    Check "report rejects missing $name pass" {$r=New-Report;$r.Checks.$name.Result='pending';Assert-Report $r $snapshot} -Reject
}
Check 'report rejects empty evidence reference' {$r=New-Report;$r.Checks.Drag.Evidence=$null;Assert-Report $r $snapshot} -Reject
Check 'report rejects unbound evidence reference' {$r=New-Report;$r.Checks.Drag.Evidence=@('unknown');Assert-Report $r $snapshot} -Reject
Check 'report rejects missing operator' {$r=New-Report;$r.Operator='';Assert-Report $r $snapshot} -Reject
Check 'report rejects pending decision' {$r=New-Report;$r.Decision='pending';Assert-Report $r $snapshot} -Reject
Check 'report rejects past observation' {$r=New-Report;$r.ObservedUtc=[DateTime]::UtcNow.AddDays(-1).ToString('o');Assert-Report $r $snapshot} -Reject
Check 'report rejects future observation' {$r=New-Report;$r.ObservedUtc=[DateTime]::UtcNow.AddDays(1).ToString('o');Assert-Report $r $snapshot} -Reject
[byte[]]$permit=New-Object byte[] 184
[BitConverter]::GetBytes([uint32]0x44434443).CopyTo($permit,0);[BitConverter]::GetBytes([uint32]2).CopyTo($permit,4)
[BitConverter]::GetBytes([uint64]100).CopyTo($permit,8);[BitConverter]::GetBytes([uint64]200).CopyTo($permit,16)
[BitConverter]::GetBytes([uint64]10800000).CopyTo($permit,24);$permit[40]=2;[Text.Encoding]::ASCII.GetBytes('b'*64).CopyTo($permit,56);[Text.Encoding]::ASCII.GetBytes('c'*64).CopyTo($permit,120)
Check 'consumed binary schema parses' {$p=Read-ConsumedPermit $permit;if($p.TrialId -cne ('02'+('00'*15))){throw 'Trial endian/offset mismatch'}}
Check 'consumed binary rejects truncation' {Read-ConsumedPermit $permit[0..182]} -Reject
foreach($offset in @(0,4,56,120)) {Check "consumed binary rejects corruption at $offset" {$p=$permit.Clone();$p[$offset]=0;Read-ConsumedPermit $p} -Reject}
Check 'consumed binary rejects zero trial' {$p=$permit.Clone();$p[40]=0;Read-ConsumedPermit $p} -Reject
Check 'consumed binary rejects old GUID-based schema' {$p=$permit.Clone();$p[4]=1;Read-ConsumedPermit $p} -Reject
Check 'next boot permit accepts zero expiration' {$p=$permit.Clone();$p[4]=3;[BitConverter]::GetBytes([uint64]0).CopyTo($p,16);$value=Read-ConsumedPermit $p;if($value.Schema -ne 3 -or $value.Expires -ne 0){throw 'Wrong lifetime'}}
Check 'next boot permit rejects timed expiration' {$p=$permit.Clone();$p[4]=3;Read-ConsumedPermit $p} -Reject
Check 'timed permit rejects zero expiration' {$p=$permit.Clone();[BitConverter]::GetBytes([uint64]0).CopyTo($p,16);Read-ConsumedPermit $p} -Reject
Check 'consumed binary rejects nonzero reserved bytes' {$p=$permit.Clone();$p[32]=1;Read-ConsumedPermit $p} -Reject
Check 'consumed binary rejects insufficient preparation uptime' {$p=$permit.Clone();[BitConverter]::GetBytes([uint64]600999).CopyTo($p,24);Read-ConsumedPermit $p} -Reject
Check 'consumed binary preserves full 64-bit uptime' {$p=$permit.Clone();[BitConverter]::GetBytes([uint64]4294968296).CopyTo($p,24);if((Read-ConsumedPermit $p).PreparedUptimeMs -ne 4294968296){throw 'Uptime truncated'}}
Check 'binding changes with root' {if((Get-ColdBinding 'C:\one' ('b'*64) ([byte[]]@(1,2))) -eq (Get-ColdBinding 'C:\two' ('b'*64) ([byte[]]@(1,2)))){throw 'Binding collision'}}
Check 'binding changes with receipt bytes' {if((Get-ColdBinding 'C:\one' ('b'*64) ([byte[]]@(1,2))) -eq (Get-ColdBinding 'C:\one' ('b'*64) ([byte[]]@(1,3)))){throw 'Binding collision'}}
$trialPermit=[pscustomobject]@{PreparedUptimeMs=10800000;Fingerprint='b'*64;Binding='c'*64;Created=[uint64]1000000000;Expires=[uint64]5000000000}
$trialService=[pscustomobject]@{ProcessId=123;CreationFileTimeUtc='3010000000'}
$trialState=@{'Service.State'='running-cold-diagnostic-unverified';'Service.DiagnosticState'='running-cold-diagnostic-unverified';'Service.StartMode'='cold-diagnostic';'Service.EngineStarted'='1';'Service.Error'='0';'Service.DiagnosticError'='0';'Service.ProcessId'='123';'Service.BootElapsedMs'='1000'}
Check 'valid live trial policy' {Assert-ColdTrial $trialPermit ('b'*64) ('c'*64) $trialService $trialState 10000000000 700000}
Check 'reject same boot startup checkpoint' {$s=$trialState.Clone();$s['Service.BootElapsedMs']='10800000';Assert-ColdTrial $trialPermit ('b'*64) ('c'*64) $trialService $s 10000000000 700000} -Reject
Check 'reject missing startup checkpoint' {$s=$trialState.Clone();$s.Remove('Service.BootElapsedMs');Assert-ColdTrial $trialPermit ('b'*64) ('c'*64) $trialService $s 10000000000 700000} -Reject
Check 'reject late cold startup' {$s=$trialState.Clone();$s['Service.BootElapsedMs']='600001';Assert-ColdTrial $trialPermit ('b'*64) ('c'*64) $trialService $s 10000000000 700000} -Reject
Check 'reject future startup checkpoint' {$s=$trialState.Clone();$s['Service.BootElapsedMs']='5000';Assert-ColdTrial $trialPermit ('b'*64) ('c'*64) $trialService $s 3001000000 1000} -Reject
Check 'long-running acceptance keeps original live trial' {Assert-ColdTrial $trialPermit ('b'*64) ('c'*64) $trialService $trialState 203000000000 20000000}
Check 'reject changed current fingerprint' {Assert-ColdTrial $trialPermit ('e'*64) ('c'*64) $trialService $trialState 10000000000 700000} -Reject
Check 'reject changed installation binding' {Assert-ColdTrial $trialPermit ('b'*64) ('e'*64) $trialService $trialState 10000000000 700000} -Reject
foreach($field in $trialState.Keys) {
    Check "reject invalid live state $field" {$s=$trialState.Clone();$s[$field]='session-only';Assert-ColdTrial $trialPermit ('b'*64) ('c'*64) $trialService $s 10000000000 700000} -Reject
}
Check 'reject previous boot process' {Assert-ColdTrial $trialPermit ('b'*64) ('c'*64) $trialService $trialState 20000000000 700000} -Reject
Check 'reject service started after permit expiry' {$s=[pscustomobject]@{ProcessId=123;CreationFileTimeUtc='5000000000'};Assert-ColdTrial $trialPermit ('b'*64) ('c'*64) $s $trialState 10000000000 700000} -Reject
Check 'next boot completion accepts a normal reboot days after preparation' {
    $p=$trialPermit | ConvertTo-Json | ConvertFrom-Json;$p | Add-Member Schema 3;$p.Expires=0
    $later=[uint64]7*86400*10000000
    $s=[pscustomobject]@{ProcessId=123;CreationFileTimeUtc=([uint64]3010000000+$later).ToString()}
    Assert-ColdTrial $p ('b'*64) ('c'*64) $s $trialState ([int64]10000000000+$later) 700000
}
# File publication tests use only the new output folder. Protection is mocked;
# these tests make no claim about Windows ACL/SCM integration acceptance.
function Assert-Protected([string]$Path) {if(-not (Test-Path -LiteralPath $Path)){throw 'Fixture path missing'}}
function Set-ReceiptAcl([string]$Path) {}
$receiptBytes=New-CompatibilityBytes $snapshot ('f'*64)
Check 'receipt publishes without overwrite' {Install-CompatibilityReceipt $receiptBytes}
Check 'identical receipt retry succeeds' {Install-CompatibilityReceipt $receiptBytes}
Check 'different receipt is retained and rejected' {Install-CompatibilityReceipt (New-CompatibilityBytes $snapshot ('e'*64))} -Reject
Check 'existing bytes remain unchanged' {if((Get-BytesHash ([IO.File]::ReadAllBytes((Join-Path $Installation 'shell-compatibility.ini')))) -ne (Get-BytesHash $receiptBytes)){throw 'Existing record overwritten'}}
Check 'native file helper compiles and reads isolated file' {Initialize-CompletionNative;$bytes=[ColdCompletionNative]::ReadSingleFile((Join-Path $Installation 'shell-compatibility.ini'),4096);if((Get-BytesHash $bytes) -ne (Get-BytesHash $receiptBytes)){throw 'Read helper changed bytes'}}
Check 'native file helper rejects oversized fixture' {[ColdCompletionNative]::ReadSingleFile((Join-Path $Installation 'shell-compatibility.ini'),1)} -Reject
# Transaction adapter mocks: no SCM/native invocations or OS setting changes.
function Reset-Transaction([string]$Failure='') {$script:mode='Disabled';$script:written=$false;$script:failure=$Failure;$script:reads=0;$script:changes=[Collections.Generic.List[string]]::new()}
function Get-CompletionSnapshot([string]$Mode='Disabled') {
    $script:reads++
    if($script:mode -ne $Mode){throw 'Unexpected service start mode'}
    $value=$snapshot | ConvertTo-Json | ConvertFrom-Json
    if($script:written){$value.Compatibility='compatible'}
    if($script:failure -eq 'verify' -and $script:written){$value.Compatibility='compatibility-unverified'}
    if($script:failure -eq 'drift' -and $script:reads -eq 2){$value.Fingerprint='e'*64}
    if($script:failure -eq 'after-enable' -and $Mode -eq 'Auto'){throw 'Simulated post-enable verification failure'}
    return $value
}
function Install-CompatibilityReceipt([byte[]]$Bytes) {if($script:failure -eq 'write'){throw 'Simulated write failure'};$script:written=$true}
function Set-Service([string]$Name,[string]$StartupType) {
    if($Name -ne 'ClassicDeskShell' -or $StartupType -ne 'Automatic'){throw 'Unexpected mutation'}
    $script:changes.Add($StartupType)
    if($script:failure -eq 'enable'){throw 'Simulated enable failure'}
    $script:mode='Auto'
}
function Restore-Disabled($Snapshot) {$script:changes.Add('Disabled');$script:mode='Disabled';if($script:failure -eq 'rollback'){throw 'Simulated rollback failure'}}
Check 'successful transaction writes before enabling without restart' {Reset-Transaction;$result=Invoke-CompletionTransaction $snapshot $receiptBytes;if(-not $script:written -or $script:mode -ne 'Auto' -or $script:changes.Count -ne 1 -or $result.ServiceStarted -or $result.ServiceStopped){throw 'Bad success sequence'}}
foreach($failure in @('write','verify','drift','enable','after-enable')) {
    Check "transaction rejects $failure" {Reset-Transaction $failure;Invoke-CompletionTransaction $snapshot $receiptBytes} -Reject
    Check "transaction leaves Disabled after $failure" {if($script:mode -ne 'Disabled'){throw 'Failure left Auto'}}
}
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'completion-tests.json') -Encoding UTF8
[pscustomobject]@{Passed=$results.Count;Failed=0;Scope='pure policy, isolated file publication, mocked transaction; no native acceptance';OutputDirectory=$OutputDirectory}
