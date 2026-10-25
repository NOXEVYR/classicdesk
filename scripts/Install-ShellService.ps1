[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('Validate','Inspect','InstallDisabled','StageUpdate','StageLayout','StageHost','EnableNextBoot','DisableNextBoot','RemoveStopped','StartCurrentSession')][string]$Action,
    [string]$Bundle,
    [string]$Installation,
    [string]$ExpectedBundleHash,
    [uint32]$ExpectedExplorerPid,
    [uint32]$ExpectedSessionId,
    [string]$ExpectedExplorerCreationTime,
    [string]$ExpectedFingerprint,
    [switch]$ValidateOnly
)
# Do not inherit incompatible PowerShell 7 modules when launched by a .NET process.
$env:PSModulePath=$PSHOME+'\Modules'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$ErrorActionPreference='Stop'
if($ValidateOnly -and $Action -ne 'StageHost'){throw 'ValidateOnly applies only to StageHost'}
$serviceName='ClassicDeskShell'
$expectedHost='62EA4A48A3D1F44D09CBD62F2E9FD796B96DEFDBA87AC3346C40BD5696D8EEB3'
$nextBootUpgradeSourceHost='312445A8EACF90EC793A9B8C4640A2AF53CCA3CAE6C0EA5DE35365F17B1BC4A9'
$previousGuardHost='637A6A3617B268AD8CF1EEED69838CC8609880623958778EC03D19E2BC7892A2'
$expectedRuntime='D6011E31F21E582748DF06A9536A8E5798F7043F3B881845D46ECEE9990F6315'
$startupRepairHost='D08EE9E4B2D9E3F92A704FEEE20E63BAC57DEAAEE6E2A6FB3351427C46BA9650'
$startupRepairRuntime='12CA9CD52EA5D64FED8136253CEB0BDB9C7FEC8D51D95388C83F408F4BD41F9F'
$expectedScope='%SystemRoot%\System32\winlogon.exe|%SystemRoot%\System32\userinit.exe|%SystemRoot%\explorer.exe|%SystemRoot%\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe'
$adminSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$systemSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$usersSid=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
$base=Join-Path $env:ProgramW6432 'ClassicDesk'
$serviceBase=Join-Path $base 'ShellService'

function Assert-CompatibilityResult($Result) {
    if($Result.state -ne 'compatible' -or $Result.engineStarted -ne $false -or $Result.acceptanceWritten -ne $false) {
        throw ('Enhanced shell compatibility has not been accepted for this Windows build and layout: '+$Result.state+'. Startup remains disabled.')
    }
}
function Assert-ExplorerStartupReady($Receipt) {
    . (Join-Path $PSScriptRoot 'Explorer-StartupPrerequisite.ps1')
    Assert-ExplorerStartupPrerequisite $Receipt
}
function Inspect-Native([string]$Root,[switch]$Protected,[switch]$Compatibility,[switch]$AllowUnverified) {
    Write-Verbose "Inspect native assets: $Root"
    $argument=if($Compatibility){'--inspect-compatibility'}elseif($Protected){'--inspect-protected'}else{'--inspect'}
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
        if($Compatibility) {
            $result=$output | ConvertFrom-Json
            if($AllowUnverified) {
                if($result.state -notin @('compatible','compatibility-unverified','compatibility-changed','compatibility-unavailable') -or
                   $result.engineStarted -ne $false -or $result.acceptanceWritten -ne $false -or
                   $result.fingerprint -cnotmatch '^[0-9a-f]{64}$') {throw 'Native compatibility fingerprint is invalid; current-session start refused'}
                return $result
            }
            Assert-CompatibilityResult $result
            return $result
        }
    } finally {$proc.Dispose()}
}
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
    if($Action -eq 'StageHost') {
        $profile=if($previous.TargetProfile){$previous.TargetProfile}else{$previous.Source.AppliedProfile}
        if(($profile | ConvertTo-Json -Compress -Depth 10) -cne ($Plan.TargetProfile | ConvertTo-Json -Compress -Depth 10)){throw 'Host update must preserve the exact saved profile'}
    }
    foreach($item in $Plan.Files) {
        $original=@($previous.Files | Where-Object Path -eq $item.Path)
        if($original.Count -ne 1){throw 'Unknown layout update file'}
        $oldFile=Inside $Plan.LayoutSource.Installation $item.Path
        $newFile=Inside $Bundle $item.Path
        if($item.Path -match '^Runtime/AppData/Engine/(ModsWritable|Symbols)/'){continue}
        if((Get-FileHash -LiteralPath $oldFile).Hash -ne $original[0].Sha256){throw 'Installed fixed file changed'}
        if($Action -eq 'StageHost') {
            if($item.Path -eq 'ClassicDeskShell.exe') {
                if($original[0].Sha256 -ne $nextBootUpgradeSourceHost -or $item.Sha256 -ne $expectedHost){throw 'Unreviewed host upgrade'}
            } elseif($item.Sha256 -ne $original[0].Sha256 -or $item.Bytes -ne $original[0].Bytes){throw 'Host update changed saved configuration or runtime'}
        } elseif($item.Path -match '^Runtime/AppData/Engine/Mods/[^/]+\.ini$') {
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
    if($Action -in @('StageLayout','StageHost')) {$null=Assert-LayoutSource $Plan;return}
    # Enable validates the currently installed receipt and all its files below;
    # its preserved LayoutSource describes history, not a new staging request.
    if(($Plan.LayoutSource -or $Plan.TargetProfile) -and $Action -ne 'EnableNextBoot'){throw 'Use StageLayout for a layout update'}
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
    # Read-only diagnostics and the independent recovery action recognize the
    # pinned startup repair. Old layout staging must still reject newer code.
    if(-not $Previous -and $Action -in @('Inspect','DisableNextBoot') -and
       $receipt.HostSha256 -in @($startupRepairHost,$previousGuardHost,$nextBootUpgradeSourceHost) -and $receipt.RuntimeManifest -eq $expectedRuntime) {$hostPin=$receipt.HostSha256}
    if(-not $Previous -and $Action -eq 'StageHost' -and $receipt.HostSha256 -eq $nextBootUpgradeSourceHost -and $receipt.RuntimeManifest -eq $expectedRuntime){$hostPin=$receipt.HostSha256}
    if($receipt.SchemaVersion -ne 1 -or $receipt.ServiceName -ne $serviceName -or $receipt.ImagePath -ne $image -or $receipt.HostSha256 -ne $hostPin) {throw 'Installation ownership mismatch'}
    $key=Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\'+$serviceName)
    if($key.ImagePath -ne $image -or $key.ObjectName -ne 'LocalSystem') {throw 'Service identity changed; no action taken'}
    if((Get-FileHash -LiteralPath (Join-Path $Root 'ClassicDeskShell.exe')).Hash -ne $hostPin) {throw 'Service executable changed'}
    return $receipt
}

function Initialize-CurrentSessionNative {
    if('ClassicDeskCurrentSessionNative' -as [type]) {return}
    $definition=@'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ClassicDeskCurrentSessionNative {
    [StructLayout(LayoutKind.Sequential)] public struct FileTime { public uint Low; public uint High; }
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder image, ref uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] public struct ServiceStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [StructLayout(LayoutKind.Sequential)] public struct ServiceStatusProcess { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);
    public static IntPtr OpenLocalManager(uint access) { return OpenSCManagerW(null, null, access); }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfigW(IntPtr service, uint type, uint startType, uint errorControl, IntPtr binaryPath, IntPtr loadGroup, IntPtr tagId, IntPtr dependencies, IntPtr account, IntPtr password, IntPtr displayName);
    public static bool ChangeStartType(IntPtr service, uint startType) { return ChangeServiceConfigW(service, UInt32.MaxValue, startType, UInt32.MaxValue, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); }
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool StartServiceW(IntPtr service, uint count, [MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.LPWStr)] string[] args);
    [DllImport("advapi32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess status, uint bufferSize, out uint bytesNeeded);
    [DllImport("advapi32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool CloseServiceHandle(IntPtr handle);
}
'@
    Add-Type -TypeDefinition $definition -Language CSharp -ErrorAction Stop
}

function Get-CurrentExplorerIdentity {
    Initialize-CurrentSessionNative
    [uint32]$ownSession=0
    if(-not [ClassicDeskCurrentSessionNative]::ProcessIdToSessionId([uint32][Diagnostics.Process]::GetCurrentProcess().Id,[ref]$ownSession)) {
        throw ('Cannot identify this interactive session: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())
    }
    $window=[ClassicDeskCurrentSessionNative]::GetShellWindow()
    if($window -eq [IntPtr]::Zero -or -not [ClassicDeskCurrentSessionNative]::IsWindow($window)) {throw 'The current session has no valid GetShellWindow'}
    [uint32]$explorerProcessId=0
    $thread=[ClassicDeskCurrentSessionNative]::GetWindowThreadProcessId($window,[ref]$explorerProcessId)
    [uint32]$session=0
    if($thread -eq 0 -or $explorerProcessId -eq 0 -or -not [ClassicDeskCurrentSessionNative]::ProcessIdToSessionId($explorerProcessId,[ref]$session)) {throw 'Cannot identify the current GetShellWindow process'}
    $process=[ClassicDeskCurrentSessionNative]::OpenProcess(0x1000,$false,$explorerProcessId)
    if($process -eq [IntPtr]::Zero) {throw ('Cannot open the current Explorer identity: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
    try {
        $created=New-Object ClassicDeskCurrentSessionNative+FileTime
        $exited=New-Object ClassicDeskCurrentSessionNative+FileTime
        $kernel=New-Object ClassicDeskCurrentSessionNative+FileTime
        $user=New-Object ClassicDeskCurrentSessionNative+FileTime
        if(-not [ClassicDeskCurrentSessionNative]::GetProcessTimes($process,[ref]$created,[ref]$exited,[ref]$kernel,[ref]$user)) {throw ('Cannot read Explorer process creation time: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
        [uint64]$creation=(([uint64]$created.High -shl 32) -bor [uint64]$created.Low)
        $image=New-Object Text.StringBuilder 32768
        [uint32]$capacity=32768
        if(-not [ClassicDeskCurrentSessionNative]::QueryFullProcessImageNameW($process,0,$image,[ref]$capacity)) {throw ('Cannot verify Explorer image path: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
        return [pscustomobject]@{Window=$window;ThreadId=$thread;ProcessId=$explorerProcessId;SessionId=$session;CurrentSessionId=$ownSession;CreationFileTime=$creation.ToString([Globalization.CultureInfo]::InvariantCulture);ImagePath=$image.ToString()}
    } finally {[void][ClassicDeskCurrentSessionNative]::CloseHandle($process)}
}

function Assert-ExpectedExplorerIdentity($Observed,[uint32]$ExplorerPid,[uint32]$SessionId,[string]$CreationFileTime) {
    [uint64]$expectedCreation=0
    if($CreationFileTime -notmatch '^[0-9]{1,20}$' -or -not [uint64]::TryParse($CreationFileTime,[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$expectedCreation) -or $expectedCreation -eq 0) {throw 'Expected Explorer creation FILETIME is invalid'}
    $expectedPath=[IO.Path]::GetFullPath((Join-Path $env:SystemRoot 'explorer.exe'))
    if($Observed.ProcessId -ne $ExplorerPid -or $Observed.SessionId -ne $SessionId -or $Observed.CurrentSessionId -ne $SessionId -or
       [uint64]$Observed.CreationFileTime -ne $expectedCreation -or
       -not [string]::Equals([IO.Path]::GetFullPath([string]$Observed.ImagePath),$expectedPath,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'GetShellWindow no longer matches the healthy Explorer PID, session, creation time and image path supplied by ClassicDesk'
    }
}

function Assert-InstalledReceiptFiles($Receipt,[string]$Root) {
    if(-not $Receipt.Files -or $Receipt.Files.Count -lt 25 -or $Receipt.Files.Count -gt 512) {throw 'Installed service receipt has no reviewed file list'}
    foreach($item in $Receipt.Files) {
        if($item.Path -match '^Runtime/AppData/Engine/(ModsWritable|Symbols)/') {continue}
        if($item.Bytes -lt 0 -or $item.Sha256 -notmatch '^[0-9A-F]{64}$') {throw 'Installed file receipt entry is invalid'}
        $path=Inside $Root $item.Path
        if((Get-Item -LiteralPath $path).Length -ne $item.Bytes -or (Get-FileHash -LiteralPath $path).Hash -ne $item.Sha256) {throw ('Installed fixed asset changed: '+$item.Path)}
    }
}

function Get-ClassicDeskServiceSnapshot {
    $service=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
    if(-not $service) {throw 'ClassicDeskShell service is not registered'}
    return [pscustomobject]@{State=[string]$service.State;StartMode=[string]$service.StartMode;ProcessId=[uint32]$service.ProcessId;Image=[string]$service.PathName}
}

function Invoke-ClassicDeskScm([ValidateSet('Manual','Disabled','Start','Stop')][string]$Operation,[string[]]$Arguments=@()) {
    Initialize-CurrentSessionNative
    $manager=[ClassicDeskCurrentSessionNative]::OpenLocalManager(0x0001)
    if($manager -eq [IntPtr]::Zero) {throw ('OpenSCManager failed: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
    $access=0x0004
    if($Operation -in @('Manual','Disabled')) {$access=$access -bor 0x0002}
    if($Operation -eq 'Start') {$access=$access -bor 0x0010}
    if($Operation -eq 'Stop') {$access=$access -bor 0x0020}
    $service=[IntPtr]::Zero
    try {
        $service=[ClassicDeskCurrentSessionNative]::OpenServiceW($manager,$serviceName,[uint32]$access)
        if($service -eq [IntPtr]::Zero) {throw ('OpenService failed: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
        $ok=$false
        switch($Operation) {
            'Manual' {$ok=[ClassicDeskCurrentSessionNative]::ChangeStartType($service,3)}
            'Disabled' {$ok=[ClassicDeskCurrentSessionNative]::ChangeStartType($service,4)}
            'Start' {$ok=[ClassicDeskCurrentSessionNative]::StartServiceW($service,[uint32]$Arguments.Count,$Arguments)}
            'Stop' {$null=$null;$status=New-Object ClassicDeskCurrentSessionNative+ServiceStatus;$ok=[ClassicDeskCurrentSessionNative]::ControlService($service,1,[ref]$status)}
        }
        if(-not $ok) {throw ($Operation+' service operation failed: '+[Runtime.InteropServices.Marshal]::GetLastWin32Error())}
    } finally {
        if($service -ne [IntPtr]::Zero) {[void][ClassicDeskCurrentSessionNative]::CloseServiceHandle($service)}
        [void][ClassicDeskCurrentSessionNative]::CloseServiceHandle($manager)
    }
}

function Wait-ClassicDeskServiceState([string]$Wanted,[int]$TimeoutSeconds=45) {
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $snapshot=Get-ClassicDeskServiceSnapshot
        if($snapshot.State -eq $Wanted) {return $snapshot}
        if($Wanted -eq 'Running' -and $snapshot.State -eq 'Stopped') {throw 'ClassicDeskShell stopped during current-session startup'}
        Start-Sleep -Milliseconds 250
    } while([DateTime]::UtcNow -lt $deadline)
    throw ('Timed out waiting for ClassicDeskShell to become '+$Wanted)
}

function Read-ClassicDeskServiceState([string]$Root) {
    $path=Join-Path $Root 'service-state.ini'
    Assert-Protected $path
    $text=[IO.File]::ReadAllText($path)
    $section=[regex]::Match($text,'(?ms)^\[Service\]\s*\r?\n(?<body>.*?)(?=^\[|\z)')
    if(-not $section.Success) {throw 'ClassicDesk service-state.ini has no Service section'}
    $values=@{}
    foreach($line in ($section.Groups['body'].Value -split '\r?\n')) {
        if($line -match '^([A-Za-z][A-Za-z0-9]*)=(.*)$') {$values[$matches[1]]=$matches[2]}
    }
    return [pscustomobject]@{State=[string]$values['State'];ProcessId=[uint32]$values['ProcessId'];Error=[uint32]$values['Error'];LastWriteTimeUtc=(Get-Item -LiteralPath $path).LastWriteTimeUtc}
}

function Stop-ClassicDeskServiceBounded([int]$TimeoutSeconds=20) {
    $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $snapshot=Get-ClassicDeskServiceSnapshot
    while($snapshot.State -in @('StartPending','StopPending')) {
        if([DateTime]::UtcNow -ge $deadline) {throw 'ClassicDeskShell remained pending and did not stop'}
        Start-Sleep -Milliseconds 250;$snapshot=Get-ClassicDeskServiceSnapshot
    }
    if($snapshot.State -eq 'Stopped') {return}
    if($snapshot.State -ne 'Running') {throw ('Unexpected service state while stopping: '+$snapshot.State)}
    Invoke-ClassicDeskScm Stop
    $null=Wait-ClassicDeskServiceState Stopped $TimeoutSeconds
}

function Invoke-StartCurrentSession([string]$Root,[uint32]$ExpectedExplorerPid,[uint32]$ExpectedSessionId,[string]$ExpectedExplorerCreationTime,[string]$ExpectedFingerprint) {
    if($ExpectedExplorerPid -eq 0 -or $ExpectedSessionId -eq 0 -or $ExpectedFingerprint -cnotmatch '^[0-9a-f]{64}$') {throw 'Current-session start arguments are invalid'}
    $receipt=Read-OwnedInstall $Root
    $currentSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if($receipt.OwnerSid -ne $currentSid) {throw 'Elevated user SID differs from the owner of this ClassicDesk installation'}
    $expectedImage='"'+(Join-Path $Root 'ClassicDeskShell.exe')+'" --service'
    $before=Get-ClassicDeskServiceSnapshot
    if($before.Image -ne $expectedImage -or $before.State -ne 'Stopped' -or $before.StartMode -ne 'Disabled' -or $before.ProcessId -ne 0) {throw 'Current-session start requires this owned service to be Stopped and Disabled at the same installation root'}
    Assert-InstalledReceiptFiles $receipt $Root
    Inspect-Native $Root -Protected
    $compatibility=Inspect-Native $Root -Compatibility -AllowUnverified
    if(-not [string]::Equals([string]$compatibility.fingerprint,$ExpectedFingerprint,[StringComparison]::Ordinal)) {throw 'Current Windows/layout fingerprint differs from the one checked by ClassicDesk'}
    $explorer=Get-CurrentExplorerIdentity
    Assert-ExpectedExplorerIdentity $explorer $ExpectedExplorerPid $ExpectedSessionId $ExpectedExplorerCreationTime
    $statePath=Join-Path $Root 'service-state.ini'
    Assert-NoLinks $statePath
    $previousStateWriteTime=$null
    if(Test-Path -LiteralPath $statePath) {Assert-Protected $statePath;$previousStateWriteTime=(Get-Item -LiteralPath $statePath).LastWriteTimeUtc}
    $startAttempted=$false;$failure=$null;$disableFailure=$null;$stopFailure=$null
    try {
        Invoke-ClassicDeskScm Manual
        $manual=Get-ClassicDeskServiceSnapshot
        if($manual.Image -ne $expectedImage -or $manual.State -ne 'Stopped' -or $manual.StartMode -ne 'Manual' -or $manual.ProcessId -ne 0) {throw 'ClassicDeskShell changed while preparing its one-session start'}
        $arguments=@('session-only',$ExpectedExplorerPid.ToString([Globalization.CultureInfo]::InvariantCulture),$ExpectedSessionId.ToString([Globalization.CultureInfo]::InvariantCulture),$ExpectedExplorerCreationTime,$ExpectedFingerprint)
        $startAttempted=$true
        Invoke-ClassicDeskScm Start $arguments
        $running=Wait-ClassicDeskServiceState Running 45
        if($running.Image -ne $expectedImage -or $running.StartMode -notin @('Manual','Disabled') -or $running.ProcessId -eq 0) {throw 'SCM did not confirm the expected current-session service identity'}
        $state=Read-ClassicDeskServiceState $Root
        if($state.State -ne 'running-current-session-unverified' -or $state.ProcessId -ne $running.ProcessId -or $state.Error -ne 0) {throw 'ClassicDesk native host did not confirm the requested current-session state'}
        if($previousStateWriteTime -and $state.LastWriteTimeUtc -le $previousStateWriteTime) {throw 'Native service state is stale; a new current-session start was not confirmed'}
    } catch {$failure=$_}
    finally {
        if($failure -and $startAttempted) {try {Stop-ClassicDeskServiceBounded 20} catch {$stopFailure=$_}}
        try {Invoke-ClassicDeskScm Disabled} catch {$disableFailure=$_}
    }
    if($disableFailure -and $startAttempted) {
        if(-not $stopFailure) {try {Stop-ClassicDeskServiceBounded 20} catch {$stopFailure=$_}}
        try {Invoke-ClassicDeskScm Disabled;$disableFailure=$null} catch {$disableFailure=$_}
    }
    if($failure -or $disableFailure -or $stopFailure) {
        $details=[Collections.Generic.List[string]]::new()
        foreach($problem in @($failure,$stopFailure,$disableFailure)) {if($problem -and $problem.Exception){$details.Add($problem.Exception.Message)}}
        try {$failedSnapshot=Get-ClassicDeskServiceSnapshot;if($failedSnapshot.StartMode -ne 'Disabled'){$details.Add('next-boot startup is not confirmed Disabled')}} catch {$details.Add('could not confirm Disabled startup mode: '+$_.Exception.Message)}
        throw ('Current-session ClassicDesk start failed; stop and Disabled recovery were attempted: '+($details -join '; '))
    }
    $postFailure=$null
    try {
        $after=Get-ClassicDeskServiceSnapshot
        if($after.Image -ne $expectedImage -or $after.State -ne 'Running' -or $after.StartMode -ne 'Disabled' -or $after.ProcessId -ne $running.ProcessId) {throw 'ClassicDesk current-session service was not left Running with next-boot startup Disabled'}
    } catch {$postFailure=$_}
    if($postFailure) {
        $cleanup=[Collections.Generic.List[string]]::new()
        try {Stop-ClassicDeskServiceBounded 20} catch {$cleanup.Add('stop: '+$_.Exception.Message)}
        try {Invoke-ClassicDeskScm Disabled} catch {$cleanup.Add('disable: '+$_.Exception.Message)}
        throw ($postFailure.Exception.Message+'; cleanup attempted: '+($cleanup -join '; '))
    }
    $result=[ordered]@{State='running-current-session-unverified';CurrentSessionStarted=$true;StartMode='Disabled';ExplorerPid=$ExpectedExplorerPid;SessionId=$ExpectedSessionId;StartupAcceptanceWritten=$false;Fingerprint=$ExpectedFingerprint;ServiceProcessId=$running.ProcessId}
    return ($result | ConvertTo-Json -Compress)
}

if($Action -in @('Validate','InstallDisabled','StageUpdate','StageLayout','StageHost')) {
    if(-not $Bundle){throw 'Bundle is required'}
    $Bundle=[IO.Path]::GetFullPath($Bundle).TrimEnd('\')
    $plan=Read-Bundle $Bundle
    Assert-SourceCurrent $plan
    if($Action -eq 'StageHost' -and $ValidateOnly) {
        [pscustomobject]@{Verified=$true;Migration='host-only';ServiceCreated=$false;EngineStarted=$false;StartupChanged=$false};return
    }
    if($Action -eq 'Validate') {
        [pscustomobject]@{Verified=$true;Files=$plan.Files.Count;Bytes=($plan.Files | Measure-Object Bytes -Sum).Sum;ServiceCreated=$false;EngineStarted=$false;UnifiedMachineLayout=$true};return
    }
}
if($Action -eq 'Inspect') {
    if(-not $Installation){throw 'An explicit owned Installation is required'}
    $receipt=Read-OwnedInstall $Installation
    Inspect-Native $Installation -Protected
    $inspection=[pscustomobject]@{ExitCode=0}
    if($inspection.ExitCode -ne 0){throw 'Protected installation inspection failed'}
    $service=Get-Service -Name $serviceName
    [pscustomobject]@{Installation=$Installation;ServiceState=$service.Status.ToString();StartType=$service.StartType.ToString();AssetsVerified=$true;HostSettingsChanged=$false;EngineStarted=$false};return
}
$identity=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {throw 'Windows administrator confirmation is required; no changes made'}

if($Action -eq 'StartCurrentSession') {
    if(-not $Installation -or $ExpectedExplorerPid -eq 0 -or $ExpectedSessionId -eq 0 -or
       $ExpectedExplorerCreationTime -notmatch '^[0-9]{1,20}$' -or $ExpectedFingerprint -cnotmatch '^[0-9a-f]{64}$') {
        throw 'StartCurrentSession requires Installation and the checked Explorer PID, session, creation FILETIME and lowercase fingerprint'
    }
    $Installation=[IO.Path]::GetFullPath($Installation).TrimEnd('\')
    if($PSCmdlet.ShouldProcess($serviceName,'Start ClassicDesk only in the verified current Explorer session; leave next-boot startup disabled')) {
        Write-Output (Invoke-StartCurrentSession $Installation $ExpectedExplorerPid $ExpectedSessionId $ExpectedExplorerCreationTime $ExpectedFingerprint)
    }
    return
}

if($Action -in @('InstallDisabled','StageUpdate','StageLayout','StageHost')) {
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
    } elseif($Action -in @('StageLayout','StageHost')) {
        if(-not $Installation -or [IO.Path]::GetFullPath($Installation).TrimEnd('\') -ne $plan.LayoutSource.Installation){throw 'Layout source installation mismatch'}
        $previousRoot=$plan.LayoutSource.Installation
        $previousReceipt=Assert-LayoutSource $plan
        $beforeService=Get-CimInstance Win32_Service -Filter "Name='ClassicDeskShell'"
        if($Action -eq 'StageHost' -and ($beforeService.State -ne 'Stopped' -or $beforeService.StartMode -ne 'Disabled')){throw 'Host upgrade requires stopped disabled service; no stop or reboot is performed'}
        if(-not (($beforeService.State -eq 'Running' -and $beforeService.StartMode -eq 'Auto') -or
            ($beforeService.State -eq 'Stopped' -and $beforeService.StartMode -eq 'Disabled'))) {throw 'Layout staging requires a stable running or disabled service'}
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
    Inspect-Native $Installation -Protected
    $inspection=[pscustomobject]@{ExitCode=0}
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
        if($current.ProcessId -ne $beforeService.ProcessId -or $current.State -ne $beforeService.State -or $current.StartMode -ne $beforeService.StartMode){throw 'Service changed during staging; startup path unchanged'}
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
        Inspect-Native $Installation -Protected
        Inspect-Native $Installation -Compatibility
        Assert-ExplorerStartupReady $receipt
    $inspection=[pscustomobject]@{ExitCode=0}
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
