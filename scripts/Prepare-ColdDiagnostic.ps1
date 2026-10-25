[CmdletBinding()]
param(
    [ValidateSet('Inspect','Arm')][string]$Action='Inspect',
    [Parameter(Mandatory)][string]$Installation,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedHostSha256,
    [ValidatePattern('^[0-9a-f]{64}$')][string]$ExpectedFingerprint,
    [ValidateRange(1,360)][int]$ExpiresInMinutes=60,
    [ValidateSet('NextBoot','Timed')][string]$Lifetime='NextBoot'
)
# This is a separate diagnostic operation. It never grants compatibility,
# starts a service, stops Explorer or requests a reboot. Arm enables exactly the
# protected native host's one-shot path for an operator-scheduled later boot.
$ErrorActionPreference='Stop'
$Installation=[IO.Path]::GetFullPath($Installation).TrimEnd('\')
$hostPath=Join-Path $Installation 'ClassicDeskShell.exe'
for($path=$hostPath;$path;$path=[IO.Path]::GetDirectoryName($path)) {
    if((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse installation path rejected'}
}
if((Get-FileHash -LiteralPath $hostPath -Algorithm SHA256).Hash -ne $ExpectedHostSha256){throw 'Host differs from the reviewed candidate; no command executed'}
function Invoke-Native([string[]]$Arguments,[switch]$Mutation) {
    $info=[Diagnostics.ProcessStartInfo]::new()
    $info.FileName=$hostPath;$info.Arguments=$Arguments -join ' '
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true
    $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($info)
    try {
        # Never kill an arm operation mid-transaction. Its result can be unknown;
        # inspect SCM and permit records before retrying. Read-only probes are bounded.
        if(-not $process.WaitForExit(30000)) {
            if($Mutation){throw 'Arm result unknown: native operation still running. Inspect SCM and permit records before retrying; it was not killed.'}
            $process.Kill();throw 'Read-only inspection timed out'
        }
        $output=$process.StandardOutput.ReadToEnd()
        $errorText=$process.StandardError.ReadToEnd()
        if($process.ExitCode -ne 0){throw "Native operation failed: $output $errorText"}
        return ($output | ConvertFrom-Json)
    } finally {$process.Dispose()}
}
$context=Invoke-Native -Arguments @('--inspect-cold-diagnostic')
if($context.schema -ne 2 -or $context.bootEvidence -cne 'monotonic-uptime-reset-v2' -or $context.engineStarted -ne $false -or $context.acceptanceWritten -ne $false){throw 'Unsupported diagnostic host'}
$verified=Invoke-Native -Arguments @('--inspect-protected')
if($verified.assetsVerified -ne $true -or $verified.engineStarted -ne $false){throw 'Protected installation verification failed'}
$compatibility=Invoke-Native -Arguments @('--inspect-compatibility')
if($compatibility.fingerprint -cnotmatch '^[0-9a-f]{64}$' -or $compatibility.acceptanceWritten -ne $false){throw 'Fingerprint unavailable'}
if($Action -eq 'Inspect') {
    [pscustomobject]@{State='inspection-only';Fingerprint=$compatibility.fingerprint;Compatibility=$compatibility.state;
        Diagnostic=$context;ServiceStarted=$false;StartupChanged=$false;AcceptanceWritten=$false}
    return
}
if(-not $ExpectedFingerprint -or $ExpectedFingerprint -cne $compatibility.fingerprint){throw 'Arm requires the exact independently reviewed fingerprint'}
if($Lifetime -eq 'NextBoot' -and $PSBoundParameters.ContainsKey('ExpiresInMinutes')){throw 'ExpiresInMinutes requires explicit Lifetime Timed; no startup setting changed'}
if($Lifetime -eq 'NextBoot' -and $context.nextBootArmSupported -ne $true){throw 'Installed host does not support next-boot preparation. Stage the new candidate; do not reboot or silently fall back to a timed permit.'}
$currentService=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
if($currentService.State -eq 'Running' -and $context.runningSessionArmSupported -ne $true){throw 'Installed host cannot prepare a cold trial while this session is running. Stage the reviewed newer host during a coordinated test; do not reboot merely to stop this session.'}
. (Join-Path $PSScriptRoot 'Explorer-StartupPrerequisite.ps1')
Assert-ExplorerStartupPrerequisite (Read-ExplorerInstallationReceipt (Join-Path $Installation 'install-record.json'))
if($context.canArm -ne $true){throw 'Cold diagnostic preparation requires at least 601 seconds of current kernel uptime; no startup setting changed'}
$arguments=if($Lifetime -eq 'NextBoot'){@('--arm-next-boot',$ExpectedFingerprint)}else{@('--arm-cold-diagnostic',$ExpectedFingerprint,$ExpiresInMinutes.ToString([Globalization.CultureInfo]::InvariantCulture))}
$result=Invoke-Native -Arguments $arguments -Mutation
if($result.state -ne 'cold-diagnostic-armed-unverified' -or $result.engineStarted -ne $false -or $result.acceptanceWritten -ne $false){throw 'Unexpected arm result; inspect SCM and permit before any retry'}
$result
