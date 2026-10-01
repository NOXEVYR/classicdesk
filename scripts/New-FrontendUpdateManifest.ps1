param(
    [Parameter(Mandatory=$true)][string]$AppDirectory,
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Build,
    [ValidateSet('preview','stable')][string]$Channel='preview'
)
$ErrorActionPreference='Stop'
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview(\.(0|[1-9][0-9]*))?)?\z' -or
    $Build -notmatch '^[a-fA-F0-9]{40}\z' -or (($Channel -eq 'preview') -ne $Version.Contains('-preview'))) { throw 'Invalid version, build or channel.' }
$appRoot=[IO.Path]::GetFullPath($AppDirectory)
$releaseRoot=[IO.Path]::GetFullPath($ReleaseDirectory)
if ((Test-Path -LiteralPath $releaseRoot) -or (Test-Path -LiteralPath (Join-Path $appRoot 'frontend-installation.json'))) { throw 'Use a fresh release directory and unregistered publish output.' }
foreach($root in @($appRoot,(Split-Path $releaseRoot -Parent))) {
    $directory=Get-Item -LiteralPath $root
    while($directory) {
        if(($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Reparse paths are not supported.'}
        $directory=$directory.Parent
    }
}
$names=@('ClassicDesk.exe','ClassicDesk.dll','ClassicDesk.deps.json','ClassicDesk.runtimeconfig.json')
$actualVersion=(Get-Item -LiteralPath (Join-Path $appRoot 'ClassicDesk.dll')).VersionInfo.ProductVersion.Split('+')[0]
if($actualVersion -cne $Version){throw 'Published DLL version differs from release version.'}
$exe=[IO.File]::ReadAllBytes((Join-Path $appRoot 'ClassicDesk.exe'))
if($exe.Length -lt 256 -or $exe[0] -ne 77 -or $exe[1] -ne 90){throw 'Expected a Windows apphost.'}
$pe=[BitConverter]::ToInt32($exe,60)
if($pe -lt 64 -or $pe -gt $exe.Length-6 -or [BitConverter]::ToUInt32($exe,$pe) -ne 17744 -or [BitConverter]::ToUInt16($exe,$pe+4) -ne 34404){throw 'Expected Windows x64 apphost.'}
$files=@(foreach($name in $names){
    $file=Get-Item -LiteralPath (Join-Path $appRoot $name)
    if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -le 0){throw 'Invalid frontend file.'}
    [ordered]@{Name=$name; AssetName="frontend-$name"; Size=$file.Length; Sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()}
})
$manifest=[ordered]@{Schema=1;Application='ClassicDesk';Version=$Version;Channel=$Channel;Platform='windows';Architecture='x64';Build=$Build.ToLowerInvariant();Files=$files}
$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($manifest|ConvertTo-Json -Depth 8)+"`n")
New-Item -ItemType Directory -Path $releaseRoot | Out-Null
foreach($file in $files){Copy-Item -LiteralPath (Join-Path $appRoot $file.Name) -Destination (Join-Path $releaseRoot $file.AssetName)}
[IO.File]::WriteAllBytes((Join-Path $releaseRoot 'frontend-update.json'),$bytes)
[IO.File]::WriteAllBytes((Join-Path $appRoot 'frontend-installation.json'),$bytes)
Write-Output 'Created frontend update assets and registration. Upload all five assets to the matching version release; keep GitHub SHA-256 digests. No software installed or published.'
