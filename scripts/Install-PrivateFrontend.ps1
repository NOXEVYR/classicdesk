[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedManifestSha256,
    [string]$TargetParent = (Join-Path $env:LOCALAPPDATA 'Programs\ClassicDesk\PrivateCandidates'),
    [switch]$Install
)
$ErrorActionPreference='Stop'
if(-not ('ClassicDeskFileLabel' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class ClassicDeskFileLabel {
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, EntryPoint="GetNamedSecurityInfoW")]
    static extern uint GetNamedSecurityInfo(string name,int type,uint info,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr descriptor,uint revision,uint info,out IntPtr text,out uint length);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public static string Read(string path) {
        IntPtr descriptor,text=IntPtr.Zero; uint length;
        uint error=GetNamedSecurityInfo(path,1,16,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,out descriptor);
        if(error!=0) throw new Win32Exception((int)error);
        try {
            if(!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor,1,16,out text,out length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStringUni(text) ?? "";
        } finally {if(text!=IntPtr.Zero) LocalFree(text);LocalFree(descriptor);}
    }
}
'@
}
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function CheckAncestors([string]$Path){
    for($part=[IO.Path]::GetFullPath($Path);$part;$part=[IO.Path]::GetDirectoryName($part)){
        if(Test-Path -LiteralPath $part){
            if((Get-Item -LiteralPath $part -Force).Attributes-band [IO.FileAttributes]::ReparsePoint){throw 'Reparse path is not an installation target or source.'}
        }
    }
}
function CheckLabel([string]$Path){
    $label=[ClassicDeskFileLabel]::Read($Path)
    if($label-match ';;;(LW|UN|S-1-16-(0|4096))\)'){throw 'Low-integrity installation path; use the normal application directory. No security label was changed.'}
    $label
}
$Source=[IO.Path]::GetFullPath($Source).TrimEnd('\')
$TargetParent=[IO.Path]::GetFullPath($TargetParent).TrimEnd('\')
CheckAncestors $Source;CheckAncestors $Manifest;CheckAncestors $TargetParent
if((Hash $Manifest)-ne $ExpectedManifestSha256){throw 'Manifest identity mismatch.'}
$entries=Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8|ConvertFrom-Json
if($entries.Count-lt 1 -or $entries.Count-gt 2000){throw 'Invalid manifest size.'}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($file in $entries){
    $relative=[string]$file.Path
    if([IO.Path]::IsPathRooted($relative)-or $relative-match '(^|[\\/])\.\.?([\\/]|$)|:'-or -not $seen.Add($relative.Replace('/','\'))){throw 'Unsafe or duplicate manifest path.'}
    $path=[IO.Path]::GetFullPath((Join-Path $Source $relative))
    if(-not $path.StartsWith($Source+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source escape.'}
    CheckAncestors $path
    if((Get-Item -LiteralPath $path).Length-ne $file.Bytes -or (Hash $path)-ne $file.Sha256){throw 'Source hash or size mismatch.'}
}
if(-not $seen.Contains('ClassicDesk.exe')){throw 'Missing application executable.'}
$ancestor=$TargetParent
while(-not(Test-Path -LiteralPath $ancestor)){$ancestor=[IO.Path]::GetDirectoryName($ancestor);if(-not $ancestor){throw 'Missing target ancestor.'}}
$parentLabel=CheckLabel $ancestor
$requiredBytes=[long](($entries|Measure-Object -Property Bytes -Sum).Sum)+32MB
$availableBytes=([IO.DriveInfo]::new([IO.Path]::GetPathRoot($TargetParent))).AvailableFreeSpace
if($availableBytes-lt $requiredBytes){throw 'Insufficient destination disk space. No installation directory was created.'}
if(-not $Install){[pscustomobject]@{Validated=$true;Files=$entries.Count;TargetParent=$TargetParent;ExistingAncestorLabel=$parentLabel;RequiredBytes=$requiredBytes;AvailableBytes=$availableBytes;Installed=$false;ProcessesStarted=0;SecurityLabelsChanged=0};return}
New-Item -ItemType Directory -Path $TargetParent -Force|Out-Null
CheckAncestors $TargetParent;[void](CheckLabel $TargetParent)
$destination=Join-Path $TargetParent ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $destination|Out-Null
foreach($file in $entries){
    $target=Join-Path $destination $file.Path
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null
    CheckAncestors $target
    # Create new files with the destination's normal inherited security metadata.
    # Do not copy or rewrite the source directory's security descriptor.
    $inputStream=[IO.File]::OpenRead((Join-Path $Source $file.Path))
    try{$outputStream=[IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$inputStream.CopyTo($outputStream)}finally{$outputStream.Dispose()}}
    finally{$inputStream.Dispose()}
    if((Get-Item -LiteralPath $target).Length-ne $file.Bytes -or (Hash $target)-ne $file.Sha256){throw 'Installed file integrity mismatch; incomplete version retained for inspection.'}
}
$appLabel=CheckLabel (Join-Path $destination 'ClassicDesk.exe')
$receipt=[ordered]@{Installation=$destination;ManifestSha256=$ExpectedManifestSha256;ApplicationSha256=(Hash (Join-Path $destination 'ClassicDesk.exe'));Files=$entries.Count;ApplicationLabel=$appLabel;Installed=$true;ProcessesStarted=0;ServiceChanged=$false;StartupChanged=$false;SecurityLabelsChanged=0}
[IO.File]::WriteAllText((Join-Path $destination 'private-install-record.json'),($receipt|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
[pscustomobject]$receipt
