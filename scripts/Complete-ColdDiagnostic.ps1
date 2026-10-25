[CmdletBinding()]
param(
    [ValidateSet('Inspect','Complete')][string]$Action='Inspect',
    [Parameter(Mandatory)][string]$Installation,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedHostSha256,
    [string]$EvidenceReport,
    [ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedReportSha256
)
# Deliberately separate from the installer. No start/stop/reboot operation exists.
$ErrorActionPreference='Stop'
function Get-Hex([byte[]]$Bytes) { return ([BitConverter]::ToString($Bytes)).Replace('-','').ToLowerInvariant() }
function Get-BytesHash([byte[]]$Bytes) {
    $hash=[Security.Cryptography.SHA256]::Create()
    try { Get-Hex ($hash.ComputeHash($Bytes)) } finally {$hash.Dispose()}
}
function Assert-NoLinks([string]$Path) {
    for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)) {
        if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse path rejected'}
    }
}
function Assert-Protected([string]$Path) {
    Assert-NoLinks $Path
    $base=Join-Path $env:ProgramW6432 'ClassicDesk'
    for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)) {
        $acl=Get-Acl -LiteralPath $part
        $trusted=@('S-1-5-18','S-1-5-32-544')
        if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw "Unprotected owner: $part"}
        $write=[int][Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership'
        foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
            if($rule.AccessControlType -eq 'Allow' -and -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
               ([int]$rule.FileSystemRights -band $write) -and $rule.IdentityReference.Value -notin $trusted){throw "Unprotected write access: $part"}
        }
        if($part -ieq $base){return}
    }
    throw 'Protected path is outside ClassicDesk'
}
function Initialize-CompletionNative {
    if('ColdCompletionNative' -as [type]){return}
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ColdCompletionNative {
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
    [StructLayout(LayoutKind.Sequential)] struct FileInfo { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out FileInfo i);
    public static byte[] ReadSingleFile(string path, int maximum) {
        using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            FileInfo i;
            if(!GetFileInformationByHandle(f.SafeFileHandle,out i) || (i.Attributes & 0x410)!=0 || i.Links!=1 || f.Length>maximum) throw new InvalidOperationException("Invalid single protected file");
            var bytes=new byte[(int)f.Length]; int at=0, count;
            while(at<bytes.Length && (count=f.Read(bytes,at,bytes.Length-at))>0) at+=count;
            if(at!=bytes.Length) throw new IOException("Short protected file read");
            return bytes;
        }
    }
}
'@
}
function Read-StrictIni([string]$Text) {
    $result=@{};$section=''
    foreach($line in ($Text -split '\r?\n')) {
        if($line -match '^\s*$' -or $line -match '^\s*;'){continue}
        if($line -match '^\[([A-Za-z]+)\]$'){$section=$Matches[1];continue}
        if($line -notmatch '^([A-Za-z]+)=([^\r\n]*)$' -or -not $section){throw 'Malformed state INI'}
        $key=$section+'.'+$Matches[1]
        if($result.ContainsKey($key)){throw 'Duplicate state key'}
        $result[$key]=$Matches[2]
    }
    return $result
}
function Get-ColdBinding([string]$Root,[string]$Fingerprint,[byte[]]$InstallBytes) {
    $stream=[IO.MemoryStream]::new();$writer=[IO.BinaryWriter]::new($stream)
    try {
        foreach($field in @('ClassicDesk-cold-diagnostic-v2',$Root,$Fingerprint,'install-record')) {
            $bytes=[Text.Encoding]::Unicode.GetBytes($field);$writer.Write([uint64]$bytes.Length);$writer.Write($bytes)
        }
        $writer.Write([uint64]$InstallBytes.Length);$writer.Write($InstallBytes);$writer.Flush()
        Get-BytesHash $stream.ToArray()
    } finally {$writer.Dispose();$stream.Dispose()}
}
function Read-ConsumedPermit([byte[]]$Bytes) {
    if($Bytes.Length -ne 184 -or [BitConverter]::ToUInt32($Bytes,0) -ne 0x44434443 -or [BitConverter]::ToUInt32($Bytes,4) -notin @(2,3) -or [BitConverter]::ToUInt64($Bytes,32) -ne 0){throw 'Malformed consumed permit'}
    $value=[pscustomobject]@{
        Schema=[BitConverter]::ToUInt32($Bytes,4)
        Created=[BitConverter]::ToUInt64($Bytes,8);Expires=[BitConverter]::ToUInt64($Bytes,16)
        PreparedUptimeMs=[BitConverter]::ToUInt64($Bytes,24);TrialId=Get-Hex $Bytes[40..55]
        Fingerprint=[Text.Encoding]::ASCII.GetString($Bytes,56,64);Binding=[Text.Encoding]::ASCII.GetString($Bytes,120,64)
    }
    if($value.PreparedUptimeMs -lt 601000 -or $value.TrialId -eq ('0'*32) -or $value.Fingerprint -cnotmatch '^[0-9a-f]{64}$' -or
       $value.Binding -cnotmatch '^[0-9a-f]{64}$' -or $value.Created -eq 0){throw 'Invalid consumed permit fields'}
    if(($value.Schema -eq 3 -and $value.Expires -ne 0) -or ($value.Schema -eq 2 -and
       ($value.Expires -le $value.Created -or ($value.Expires-$value.Created) -gt 216000000000))){throw 'Invalid consumed permit lifetime'}
    return $value
}
function Invoke-Inspector([string]$Argument) {
    if((Get-FileHash -LiteralPath $script:hostPath).Hash -ne $ExpectedHostSha256){throw 'Candidate host changed'}
    $info=[Diagnostics.ProcessStartInfo]::new();$info.FileName=$script:hostPath;$info.Arguments=$Argument
    $info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($info)
    try {
        if(-not $process.WaitForExit(30000)){$process.Kill();throw 'Read-only native inspection timed out'}
        $output=$process.StandardOutput.ReadToEnd();$errorText=$process.StandardError.ReadToEnd()
        if($process.ExitCode -ne 0){throw "Native inspection failed: $errorText"}
        $result=$output | ConvertFrom-Json
        if($result.engineStarted -ne $false){throw 'Unexpected inspector response'}
        return $result
    } finally {$process.Dispose()}
}
function Get-OwnedService([string]$Mode) {
    $service=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
    $image='"'+$script:hostPath+'" --service'
    $key=Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\ClassicDeskShell'
    if(-not $service -or $service.State -ne 'Running' -or $service.StartMode -ne $Mode -or $service.ProcessId -eq 0 -or
       $service.PathName -cne $image -or $service.StartName -ne 'LocalSystem' -or $key.ImagePath -cne $image -or
       $key.ObjectName -ne 'LocalSystem' -or $key.Type -ne 16 -or ($key.PSObject.Properties.Name -contains 'DelayedAutoStart' -and $key.DelayedAutoStart -ne 0)){throw 'Expected owned Running LocalSystem non-delayed service unavailable'}
    $process=[Diagnostics.Process]::GetProcessById([int]$service.ProcessId)
    try {
        if($process.HasExited -or $process.MainModule.FileName -ine $script:hostPath){throw 'Running service executable differs'}
        return [pscustomobject]@{ProcessId=[uint32]$service.ProcessId;CreationFileTimeUtc=$process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString()}
    } finally {$process.Dispose()}
}
function Assert-ColdTrial($Permit,[string]$Fingerprint,[string]$Binding,$Service,$State,[int64]$Now,[uint64]$Uptime) {
    if($Permit.PreparedUptimeMs -lt 601000 -or $Permit.Fingerprint -cne $Fingerprint -or $Permit.Binding -cne $Binding){throw 'Consumed permit no longer binds this installation'}
    if($State['Service.State'] -cne 'running-cold-diagnostic-unverified' -or $State['Service.DiagnosticState'] -cne 'running-cold-diagnostic-unverified' -or
       $State['Service.StartMode'] -cne 'cold-diagnostic' -or $State['Service.EngineStarted'] -ne '1' -or
       $State['Service.Error'] -ne '0' -or $State['Service.DiagnosticError'] -ne '0' -or $State['Service.ProcessId'] -ne [string]$Service.ProcessId){throw 'Live cold-trial state is not successful'}
    # The protected host records the uptime at engine startup. Compare that
    # checkpoint, not the current uptime: real interactive acceptance may take
    # longer than the preparation session. Live PID/creation binding below
    # prevents replay of a previous service/boot's state.
    [uint64]$startedUptime=0
    if($State['Service.BootElapsedMs'] -cnotmatch '^[0-9]+$' -or
       -not [uint64]::TryParse($State['Service.BootElapsedMs'],[ref]$startedUptime) -or
       $startedUptime -gt 600000 -or $startedUptime -ge $Permit.PreparedUptimeMs -or $startedUptime -gt $Uptime){throw 'Cold startup uptime reset is unproven'}
    $created=[uint64]$Service.CreationFileTimeUtc
    $bootStart=$Now-([int64]$Uptime*10000)
    if($created -lt $Permit.Created -or ($Permit.Schema -ne 3 -and $created -ge $Permit.Expires) -or $created -lt ($bootStart-20000000) -or $created -gt ($bootStart+6000000000)){throw 'Service creation is outside the permitted cold boot'}
}
function Get-CompletionSnapshot([string]$Mode='Disabled') {
    Assert-Protected $Installation
    foreach($name in @('ClassicDeskShell.exe','install-record.json','cold-diagnostic.consumed.bin','service-state.ini')){Assert-Protected (Join-Path $Installation $name)}
    if(Test-Path -LiteralPath (Join-Path $Installation 'cold-diagnostic.permit.bin')){throw 'Active cold permit still exists'}
    $installBytes=[ColdCompletionNative]::ReadSingleFile((Join-Path $Installation 'install-record.json'),2MB)
    if($installBytes.Length -gt 2MB){throw 'Oversized install record'}
    $install=[Text.Encoding]::UTF8.GetString($installBytes).TrimStart([char]0xfeff) | ConvertFrom-Json
    if($install.SchemaVersion -ne 1 -or $install.ServiceName -ne 'ClassicDeskShell' -or $install.HostSha256 -ne $ExpectedHostSha256 -or $install.ImagePath -cne ('"'+$script:hostPath+'" --service')){throw 'Install record ownership mismatch'}
    . (Join-Path $PSScriptRoot 'Explorer-StartupPrerequisite.ps1')
    Assert-ExplorerStartupPrerequisite $install
    $verified=Invoke-Inspector '--inspect-protected'
    if($verified.assetsVerified -ne $true){throw 'Current protected assets failed verification'}
    $compat=Invoke-Inspector '--inspect-compatibility'
    if($compat.acceptanceWritten -ne $false -or $compat.fingerprint -cnotmatch '^[0-9a-f]{64}$'){throw 'Current fingerprint unavailable'}
    $consumed=[ColdCompletionNative]::ReadSingleFile((Join-Path $Installation 'cold-diagnostic.consumed.bin'),184)
    $permit=Read-ConsumedPermit $consumed
    $service=Get-OwnedService $Mode
    $state=Read-StrictIni ([IO.File]::ReadAllText((Join-Path $Installation 'service-state.ini')))
    $now=[DateTime]::UtcNow.ToFileTimeUtc();$uptime=[ColdCompletionNative]::GetTickCount64()
    Assert-ColdTrial $permit $compat.fingerprint (Get-ColdBinding $Installation $compat.fingerprint $installBytes) $service $state $now $uptime
    [pscustomobject]@{Installation=$Installation;HostSha256=$ExpectedHostSha256.ToLowerInvariant();Fingerprint=$compat.fingerprint
        BootEvidence='monotonic-uptime-reset-v2';PreparedUptimeMs=$permit.PreparedUptimeMs;TrialId=$permit.TrialId;ConsumedSha256=Get-BytesHash $consumed;InstallRecordSha256=Get-BytesHash $installBytes
        ServiceProcessId=$service.ProcessId;ServiceCreationFileTimeUtc=$service.CreationFileTimeUtc;Compatibility=$compat.state}
}
function Assert-SameSnapshot($Before,$After) {
    foreach($field in @('Installation','HostSha256','Fingerprint','BootEvidence','PreparedUptimeMs','TrialId','ConsumedSha256','InstallRecordSha256','ServiceProcessId','ServiceCreationFileTimeUtc')) {
        if([string]$Before.$field -cne [string]$After.$field){throw "Trial identity changed: $field"}
    }
}
function Assert-Report($Report,$Snapshot) {
    if($Report.Schema -ne 1 -or $Report.Decision -cne 'accepted' -or [string]::IsNullOrWhiteSpace($Report.Operator)){throw 'Explicit operator acceptance required'}
    Assert-SameSnapshot $Snapshot $Report
    $observed=[DateTimeOffset]::Parse($Report.ObservedUtc,[Globalization.CultureInfo]::InvariantCulture)
    if($observed.Offset -ne [TimeSpan]::Zero -or $observed.UtcDateTime.ToFileTimeUtc() -lt [int64]$Snapshot.ServiceCreationFileTimeUtc -or $observed.UtcDateTime -gt [DateTime]::UtcNow){throw 'Acceptance observation is not from this running trial'}
    $paths=@($Report.Evidence | ForEach-Object {$_.Path})
    if($paths.Count -eq 0 -or $paths.Count -gt 64 -or @($paths | Sort-Object -Unique).Count -ne $paths.Count){throw 'Evidence manifest missing or duplicated'}
    foreach($check in @('ColdBoot','Taskbar','Win10Explorer','Drag','TopClick')) {
        $item=$Report.Checks.$check
        if($item.Result -cne 'pass' -or -not $item.Evidence -or [string]::IsNullOrWhiteSpace($item.Observation)){throw "Operator check incomplete: $check"}
        foreach($path in $item.Evidence){if($path -cnotin $paths){throw "Unbound evidence: $check"}}
    }
}
function New-CompatibilityBytes($Snapshot,[string]$ReportHash) {
    $text="[Compatibility]`r`nSchema=1`r`nEnhancedShellAccepted=1`r`nFingerprint=$($Snapshot.Fingerprint)`r`nEvidenceReportSha256=$ReportHash`r`nTrialId=$($Snapshot.TrialId)`r`nBootEvidence=$($Snapshot.BootEvidence)`r`nPreparedUptimeMs=$($Snapshot.PreparedUptimeMs)`r`nServiceProcessId=$($Snapshot.ServiceProcessId)`r`nServiceCreationFileTimeUtc=$($Snapshot.ServiceCreationFileTimeUtc)`r`n"
    return ,([Text.Encoding]::ASCII.GetBytes($text))
}
function Set-ReceiptAcl([string]$Path) {
    $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))}
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Install-CompatibilityReceipt([byte[]]$Bytes) {
    $path=Join-Path $Installation 'shell-compatibility.ini'
    if(Test-Path -LiteralPath $path) {
        Assert-Protected $path
        if((Get-BytesHash ([IO.File]::ReadAllBytes($path))) -cne (Get-BytesHash $Bytes)){throw 'Existing compatibility record is different; never overwritten'}
        return # Exact transaction retry only, not arbitrary valid receipt reuse.
    }
    $temporary=Join-Path $Installation ('compatibility-'+[guid]::NewGuid().ToString('N')+'.tmp')
    $file=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {$file.Write($Bytes,0,$Bytes.Length);$file.Flush($true)} finally {$file.Dispose()}
    # Failures retain the uniquely named temp for diagnosis; no unknown file is deleted.
    Set-ReceiptAcl $temporary;Assert-Protected $temporary
    [IO.File]::Move($temporary,$path) # Same-directory atomic, no overwrite.
    Assert-Protected $path
}
function Invoke-CompletionTransaction($Snapshot,[byte[]]$ReceiptBytes) {
    $attemptedEnable=$false
    try {
        Assert-SameSnapshot $Snapshot (Get-CompletionSnapshot)
        Install-CompatibilityReceipt $ReceiptBytes
        $current=Get-CompletionSnapshot
        Assert-SameSnapshot $Snapshot $current
        if($current.Compatibility -cne 'compatible'){throw 'Native host did not accept protected receipt'}
        $attemptedEnable=$true
        Set-Service -Name ClassicDeskShell -StartupType Automatic
        $current=Get-CompletionSnapshot 'Auto'
        Assert-SameSnapshot $Snapshot $current
        if($current.Compatibility -cne 'compatible'){throw 'Final native compatibility check failed'}
        [pscustomobject]@{State='accepted-auto-next-boot';ServiceProcessId=$Snapshot.ServiceProcessId;ServiceStarted=$false;ServiceStopped=$false;AcceptanceWritten=$true;StartupChanged=$true}
    } catch {
        $failure=$_
        if($attemptedEnable) {
            try {
                # Refuse to alter a service re-registered to a different process/image.
                Restore-Disabled $Snapshot
            } catch {throw "Completion failed; rollback could not be verified. Inspect SCM before retry. Original: $failure; rollback: $_"}
        }
        throw "Completion refused; retained receipt may be retried with the identical report after inspection. $failure"
    }
}
function Restore-Disabled($Snapshot) {
    # A failing engine may already have stopped. Disable the still-owned service
    # even then, but do not touch a different registration or running process.
    $service=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
    $key=Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\ClassicDeskShell'
    $image='"'+$script:hostPath+'" --service'
    if(-not $service -or $service.PathName -cne $image -or $service.StartName -ne 'LocalSystem' -or
       $key.ImagePath -cne $image -or $key.ObjectName -ne 'LocalSystem' -or $key.Type -ne 16){throw 'Rollback service ownership changed'}
    if($service.ProcessId -ne 0) {
        $process=[Diagnostics.Process]::GetProcessById([int]$service.ProcessId)
        try {
            if($service.ProcessId -ne $Snapshot.ServiceProcessId -or $process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString() -cne $Snapshot.ServiceCreationFileTimeUtc -or $process.MainModule.FileName -ine $script:hostPath){throw 'Rollback process changed'}
        } finally {$process.Dispose()}
    }
    Set-Service -Name ClassicDeskShell -StartupType Disabled
    $after=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
    if($after.StartMode -ne 'Disabled' -or $after.PathName -cne $image -or $after.StartName -ne 'LocalSystem'){throw 'Disabled rollback not confirmed'}
}
# Dot sourcing is for isolated function tests. It never invokes native/SCM operations.
if($MyInvocation.InvocationName -eq '.'){return}
$Installation=[IO.Path]::GetFullPath($Installation).TrimEnd('\')
$serviceBase=Join-Path $env:ProgramW6432 'ClassicDesk\ShellService'
if(-not $Installation.StartsWith($serviceBase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Not a ClassicDesk service installation'}
$script:hostPath=Join-Path $Installation 'ClassicDeskShell.exe'
Initialize-CompletionNative
$snapshot=Get-CompletionSnapshot
if($Action -eq 'Inspect') {
    [pscustomobject]@{State='inspection-only';Identity=$snapshot;AcceptanceWritten=$false;StartupChanged=$false;ServiceStarted=$false;ServiceStopped=$false}
    return
}
if(-not $EvidenceReport -or -not $ExpectedReportSha256){throw 'Complete requires an explicit evidence report and independently reviewed SHA-256'}
$EvidenceReport=[IO.Path]::GetFullPath($EvidenceReport);Assert-NoLinks $EvidenceReport
$locks=[Collections.Generic.List[IDisposable]]::new()
try {
    # Keep report and all evidence read-locked throughout receipt creation/enable.
    $reportStream=[IO.File]::Open($EvidenceReport,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$locks.Add($reportStream)
    if($reportStream.Length -gt 1MB){throw 'Oversized evidence report'}
    $buffer=[IO.MemoryStream]::new();try {$reportStream.CopyTo($buffer);$reportBytes=$buffer.ToArray()}finally{$buffer.Dispose()}
    $reportHash=Get-BytesHash $reportBytes
    if($reportHash -ine $ExpectedReportSha256){throw 'Evidence report changed since review'}
    $report=[Text.Encoding]::UTF8.GetString($reportBytes).TrimStart([char]0xfeff) | ConvertFrom-Json
    Assert-Report $report $snapshot
    foreach($item in $report.Evidence) {
        if($item.Sha256 -cnotmatch '^[0-9a-f]{64}$' -or [IO.Path]::IsPathRooted($item.Path) -or
           ($item.Path -split '[/\\]' | Where-Object {$_ -in @('','..','.') -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0})){throw 'Invalid local evidence path/hash'}
        $path=Join-Path ([IO.Path]::GetDirectoryName($EvidenceReport)) $item.Path;Assert-NoLinks $path
        $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$locks.Add($stream)
        $hash=[Security.Cryptography.SHA256]::Create()
        try {if((Get-Hex ($hash.ComputeHash($stream))) -cne $item.Sha256){throw 'Evidence file changed'}}finally{$hash.Dispose()}
    }
    # Protected lock serializes completion operations. It grants no permission.
    $lockPath=Join-Path $Installation 'cold-completion.lock'
    $lock=[IO.FileStream]::new($lockPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::DeleteOnClose)
    $locks.Add($lock)
    Invoke-CompletionTransaction $snapshot (New-CompatibilityBytes $snapshot $reportHash)
} finally {foreach($entry in $locks){$entry.Dispose()}}
