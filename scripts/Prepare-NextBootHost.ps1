[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installation,
    [Parameter(Mandatory)][string]$NewHost,
    [Parameter(Mandatory)][string]$OutputDirectory
)
# Offline preparation only. The original installation and startup state are read-only.
$ErrorActionPreference='Stop'
$oldPin='312445A8EACF90EC793A9B8C4640A2AF53CCA3CAE6C0EA5DE35365F17B1BC4A9'
$newPin='62EA4A48A3D1F44D09CBD62F2E9FD796B96DEFDBA87AC3346C40BD5696D8EEB3'
$runtimePin='D6011E31F21E582748DF06A9536A8E5798F7043F3B881845D46ECEE9990F6315'
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function NoLinks([string]$Path) {
    for($p=[IO.Path]::GetFullPath($Path);$p;$p=[IO.Path]::GetDirectoryName($p)) {
        if((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse path rejected'}
    }
}
function Within([string]$Root,[string]$Relative) {
    if([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or $Relative.Split([char[]]@('/','\')) -contains '..'){throw 'Invalid manifest path'}
    $path=[IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if(-not $path.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path escaped root'}
    NoLinks $path
    return $path
}
$Installation=[IO.Path]::GetFullPath($Installation).TrimEnd('\')
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
NoLinks $Installation;NoLinks $NewHost;NoLinks $OutputDirectory
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new output directory'}
$receiptPath=Join-Path $Installation 'install-record.json'
NoLinks $receiptPath
$receiptHash=Hash $receiptPath
$receipt=Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
if($receipt.HostSha256 -ne $oldPin -or (Hash (Join-Path $Installation 'ClassicDeskShell.exe')) -ne $oldPin -or
   (Hash $NewHost) -ne $newPin -or $receipt.RuntimeManifest -ne $runtimePin){throw 'Unreviewed host/runtime migration'}
if($receipt.OwnerSid -ne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value){throw 'Installation owner differs'}
$profile=if($receipt.TargetProfile){$receipt.TargetProfile}else{$receipt.Source.AppliedProfile}
if(-not $profile -or $receipt.Files.Count -lt 25 -or $receipt.Files.Count -gt 512){throw 'Invalid installed profile or file list'}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$files=[Collections.Generic.List[object]]::new()
$null=New-Item -ItemType Directory -Path $OutputDirectory
foreach($item in $receipt.Files) {
    if(-not $seen.Add($item.Path)){throw 'Duplicate manifest path'}
    $from=Within $Installation $item.Path;$to=Within $OutputDirectory $item.Path
    $cache=$item.Path -match '^Runtime/AppData/Engine/(ModsWritable|Symbols)/'
    if(-not $cache -and ((Hash $from) -ne $item.Sha256 -or (Get-Item -LiteralPath $from).Length -ne $item.Bytes)){throw ('Installed fixed asset changed: '+$item.Path)}
    if($item.Path -eq 'ClassicDeskShell.exe'){$from=$NewHost}
    $null=New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($to)) -Force
    Copy-Item -LiteralPath $from -Destination $to
    $files.Add([pscustomobject]@{Path=$item.Path;Bytes=(Get-Item -LiteralPath $to).Length;Sha256=Hash $to})
}
if((Hash $receiptPath) -ne $receiptHash){throw 'Installation changed while preparing'}
$scope='%SystemRoot%\System32\winlogon.exe|%SystemRoot%\System32\userinit.exe|%SystemRoot%\explorer.exe|%SystemRoot%\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe'
$plan=[ordered]@{SchemaVersion=1;State='prepared-not-installed';ServiceName='ClassicDeskShell';OwnerSid=$receipt.OwnerSid;
    Source=$receipt.Source;RuntimeManifest=$runtimePin;HostSha256=$newPin;EngineInclude=$scope;Files=$files;
    TargetProfile=$profile;LayoutSource=@{Installation=$Installation;ReceiptSha256=$receiptHash}}
$path=Join-Path $OutputDirectory 'service-package.json'
[IO.File]::WriteAllText($path,($plan | ConvertTo-Json -Depth 15),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{Bundle=$OutputDirectory;BundleSha256=Hash $path;Files=$files.Count;Installed=$false;StartupChanged=$false;EngineStarted=$false}
