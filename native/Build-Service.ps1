param(
    [Parameter(Mandatory)][string]$Toolchain,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Manifest
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$manifestPath=if($Manifest){[IO.Path]::GetFullPath($Manifest)}else{Join-Path $repo 'runtime/windows-x64-assets.json'}
$manifestData=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if($manifestData.engineVersion -ne '1.7.3' -or $manifestData.assets -isnot [array] -or $manifestData.assets.Count -lt 18 -or $manifestData.assets.Count -gt 128){throw 'Invalid or empty fixed runtime asset manifest'}
$assetPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $manifestData.assets){
    if($entry.path -notmatch '^[a-zA-Z0-9_+./$-]+$' -or $entry.path.Contains('..') -or $entry.sha256 -notmatch '^[0-9a-f]{64}$' -or $entry.bytes -isnot [ValueType] -or [long]$entry.bytes -lt 1 -or [long]$entry.bytes -gt 64MB -or -not $assetPaths.Add([string]$entry.path)){throw 'Invalid or duplicate fixed runtime asset'}
}
foreach($required in @('Engine/1.7.3/32/windhawk.dll','Engine/1.7.3/64/windhawk.dll','AppData/Engine/Mods/64/taskbar-start-button-position_1.3.2-classicdesk.1.dll','AppData/Engine/Mods/64/windows-11-taskbar-styler_1.9.dll')){if(-not $assetPaths.Contains($required)){throw ('Missing required fixed runtime asset: '+$required)}}
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$scope='%SystemRoot%\System32\winlogon.exe|%SystemRoot%\System32\userinit.exe|%SystemRoot%\explorer.exe|%SystemRoot%\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe'
$header=[Collections.Generic.List[string]]::new()
$header.Add('#pragma once')
$header.Add('struct Asset { const wchar_t* path; unsigned long long bytes; const char* sha256; };')
$header.Add('constexpr Asset Assets[]={')
foreach($asset in $manifestData.assets) {
    if($asset.path -notmatch '^[a-zA-Z0-9_+./$-]+$' -or $asset.path.Contains('..') -or $asset.sha256 -notmatch '^[0-9a-f]{64}$') { throw 'Invalid fixed asset' }
    $path=$asset.path.Replace('/','\').Replace('\','\\')
    $header.Add(' {L"'+$path+'",'+$asset.bytes+'ULL,"'+$asset.sha256+'"},')
}
$header.Add('};')
$header.Add('constexpr wchar_t EngineInclude[]=L"'+$scope.Replace('\','\\')+'";')
[IO.File]::WriteAllLines((Join-Path $output 'service-assets.generated.h'),$header,[Text.UTF8Encoding]::new($false))
$compiler=Join-Path ([IO.Path]::GetFullPath($Toolchain)) 'bin/clang++.exe'
& $compiler -target i686-w64-mingw32 -std=c++23 -O2 -static -municode '-Wl,--subsystem,windows' '-Wl,--no-insert-timestamp' -I $output (Join-Path $PSScriptRoot 'shell-service.cpp') -ladvapi32 -lbcrypt -lwevtapi -o (Join-Path $output 'ClassicDeskShell.exe')
if($LASTEXITCODE -ne 0){throw 'Shell service build failed'}
Get-FileHash -LiteralPath (Join-Path $output 'ClassicDeskShell.exe') -Algorithm SHA256
Write-Output "Engine scope: $scope"
