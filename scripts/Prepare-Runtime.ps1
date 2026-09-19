param(
    [Parameter(Mandatory)][string]$ApplicationDll,
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$ExtractedInstaller,
    [Parameter(Mandatory)][string]$ModuleDirectory,
    [Parameter(Mandatory)][string]$Destination,
    [string]$DotNet = 'dotnet'
)
# Offline preparation only. The installer/engine is never executed. No service,
# registry, startup entry, active application or existing directory is changed.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$manifestPath = Join-Path $repo 'runtime/windows-x64-assets.json'
$manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
$manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json

function Assert-RegularPath([string]$Path) {
    $current = Get-Item -LiteralPath $Path -Force
    if ($current.PSIsContainer) { throw "Expected a file: $Path" }
    while ($null -ne $current) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse path rejected: $Path" }
        $current = if ($current -is [IO.DirectoryInfo]) { $current.Parent } else { $current.Directory }
    }
}
function Assert-Asset([string]$Path, $Asset) {
    Assert-RegularPath $Path
    if ((Get-Item -LiteralPath $Path).Length -ne $Asset.bytes -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Asset.sha256) {
        throw "Fixed asset verification failed: $Path"
    }
}
function Within([string]$Root, [string]$Relative) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (-not $candidate.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw 'Asset path escapes its root.' }
    return $candidate
}

$ApplicationDll = [IO.Path]::GetFullPath($ApplicationDll)
$Destination = [IO.Path]::GetFullPath($Destination)
Assert-RegularPath $ApplicationDll
if (Test-Path -LiteralPath $Destination) { throw 'Destination must be new; existing directories are never overwritten.' }
Assert-Asset ([IO.Path]::GetFullPath($Installer)) $manifest.installer
$sources = @{}
foreach ($asset in $manifest.assets) {
    $base = switch ($asset.source) { 'installer' { $ExtractedInstaller } 'module' { $ModuleDirectory } default { throw 'Unknown asset source.' } }
    $source = Within $base $asset.sourcePath
    Assert-Asset $source $asset
    $sources[$asset.path] = $source
}

# The product owns new-directory creation and writes every module Disabled=1,
# both safety modes on, with updates/toolkit/tray disabled.
& $DotNet $ApplicationDll --prepare-disabled-config $Destination
if ($LASTEXITCODE -ne 0) { throw 'Disabled configuration creation failed; any partial output is preserved.' }
foreach ($asset in $manifest.assets) {
    $target = Within $Destination $asset.path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    $payload = [IO.File]::ReadAllBytes($sources[$asset.path])
    $stream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($payload, 0, $payload.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    Assert-Asset $target $asset
}
$outputManifest = Within $Destination 'runtime-assets-manifest.json'
$stream = [IO.File]::Open($outputManifest, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($manifestBytes, 0, $manifestBytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
# Check against the application's compiled manifest pin, not just this JSON.
& $DotNet $ApplicationDll --check-runtime $Destination
if ($LASTEXITCODE -ne 0) { throw 'Product verification failed. Output preserved, engine not started.' }
Write-Output 'Verified local runtime prepared with all modules disabled. Nothing has been activated.'
