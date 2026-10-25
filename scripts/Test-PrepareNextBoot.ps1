param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new isolated directory'}
$null=New-Item -ItemType Directory -Path $OutputDirectory
$root=Join-Path $OutputDirectory 'installation';$null=New-Item -ItemType Directory -Path $root
[IO.File]::WriteAllText((Join-Path $root 'ClassicDeskShell.exe'),'non-executable fixture')
[IO.File]::WriteAllText((Join-Path $root 'install-record.json'),'{}')
$pin=(Get-FileHash -LiteralPath (Join-Path $root 'ClassicDeskShell.exe')).Hash
$source=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Prepare-ColdDiagnostic.ps1') -Raw -Encoding UTF8
# Replace only host adapters in a private harness, execute the production routing.
$mocks=@'
function Invoke-Native([string[]]$Arguments,[switch]$Mutation) {
    if($Mutation) {
        $commands.Add(($Arguments -join ' '))
        return [pscustomobject]@{state='cold-diagnostic-armed-unverified';engineStarted=$false;acceptanceWritten=$false}
    }
    switch($Arguments[0]) {
        '--inspect-cold-diagnostic' {return [pscustomobject]@{schema=2;bootEvidence='monotonic-uptime-reset-v2';runningSessionArmSupported=$true;nextBootArmSupported=$nextBootSupported;canArm=$true;engineStarted=$false;acceptanceWritten=$false}}
        '--inspect-protected' {return [pscustomobject]@{assetsVerified=$true;engineStarted=$false}}
        '--inspect-compatibility' {return [pscustomobject]@{fingerprint=('a'*64);acceptanceWritten=$false}}
        default {throw 'Unexpected native inspection'}
    }
}
function Get-CimInstance {return [pscustomobject]@{State='Running'}}
function Assert-ExplorerStartupPrerequisite($Receipt) {}
function Read-ExplorerInstallationReceipt($Path) {return [pscustomobject]@{}}
'@
$source=$source.Replace('$context=Invoke-Native',($mocks+"`r`n"+'$context=Invoke-Native'))
$source=$source.Replace(". (Join-Path `$PSScriptRoot 'Explorer-StartupPrerequisite.ps1')",'# Fixture: prerequisites isolated above')
$harness=Join-Path $OutputDirectory 'harness.ps1';[IO.File]::WriteAllText($harness,$source,[Text.UTF8Encoding]::new($true))
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[hashtable]$Options,[bool]$Reject=$false,[bool]$Supported=$true,[string]$Command='--arm-next-boot') {
    $script:commands=[Collections.Generic.List[string]]::new();$script:nextBootSupported=$Supported
    $rejected=$false
    try {& $harness -Action Arm -Installation $root -ExpectedHostSha256 $pin -ExpectedFingerprint ('a'*64) @Options | Out-Null}
    catch {$rejected=$true;if(-not $Reject){throw}}
    if($rejected -ne $Reject -or ($Reject -and $script:commands.Count -ne 0) -or
       (-not $Reject -and ($script:commands.Count -ne 1 -or -not $script:commands[0].StartsWith($Command+' ')))){throw "Failed: $Name"}
    $results.Add([pscustomobject]@{Name=$Name;Passed=$true;MockArmCalls=$script:commands.Count})
}
Check 'default prepares next boot without deadline' @{}
Check 'explicit next boot routing' @{Lifetime='NextBoot'}
Check 'legacy timed trial requires explicit selection' @{Lifetime='Timed';ExpiresInMinutes=360} $false $true '--arm-cold-diagnostic'
Check 'old host never silently falls back to timed trial' @{} $true $false
Check 'ambiguous expiration rejected before mutation' @{ExpiresInMinutes=360} $true
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'report.json') -Encoding UTF8
[pscustomobject]@{Passed=$results.Count;RealServiceWrites=0;RealProcessStarts=0}
