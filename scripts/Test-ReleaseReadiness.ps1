[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$ReleaseVersion,
    [Parameter(Mandatory = $true)][string]$SourceCommit,
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [ValidateSet('frontend-only', 'full-runtime')][string]$Profile = 'frontend-only',
    [string]$RuntimeBundleDirectory
)

# Read-only release evidence gate. It never builds, downloads, extracts,
# publishes, installs, signs, or changes a product/runtime setting.
$ErrorActionPreference = 'Stop'
$checks = [Collections.Generic.List[object]]::new()
$expectedFrontendNames = @(
    'ClassicDesk.exe',
    'ClassicDesk.dll',
    'ClassicDesk.deps.json',
    'ClassicDesk.runtimeconfig.json'
)

function Add-Check {
    param([string]$Id, [ValidateSet('pass', 'fail', 'info')][string]$Status, [string]$Detail)
    $checks.Add([ordered]@{ id = $Id; status = $Status; detail = $Detail })
}

function Get-Sha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-RegularPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer) { throw "Expected a file: $Path" }
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse path is not accepted: $Path"
        }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}

function Assert-RegularDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    $item = Get-Item -LiteralPath $Path -Force
    if (-not $item.PSIsContainer) { throw "Expected a directory: $Path" }
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Reparse path is not accepted: $Path"
        }
        $item = $item.Parent
    }
}

function Get-RepoFile {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Expected a repository-relative file path: $RelativePath"
    }
    $parts = $RelativePath -split '[\\/]'
    if ($parts | Where-Object { $_ -in @('', '.', '..') }) { throw "Unsafe repository path: $RelativePath" }
    if ($RelativePath -match '^[A-Za-z]:|:') { throw "Unsafe repository path: $RelativePath" }

    $normalized = $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar)
    $fullPath = [IO.Path]::GetFullPath((Join-Path $Root $normalized))
    $rootPrefix = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Repository path escapes its root: $RelativePath"
    }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Repository input is missing: $RelativePath" }
    Assert-RegularPath -Path $fullPath

    $trackedOutput = @(& git -C $Root ls-files --error-unmatch -- $RelativePath 2>$null)
    $trackedExitCode = $LASTEXITCODE
    if ($trackedExitCode -ne 0 -or $trackedOutput.Count -eq 0) { throw "Repository input is not tracked: $RelativePath" }

    & git -C $Root check-ignore -q --no-index -- $RelativePath 2>$null
    $ignoreExitCode = $LASTEXITCODE
    if ($ignoreExitCode -eq 0) { throw "Repository input matches an ignore rule: $RelativePath" }
    if ($ignoreExitCode -gt 1) { throw "Could not inspect ignore rules for: $RelativePath" }
    return $fullPath
}

function Find-TrackedNativeSourceByHash {
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$Hash)
    $nativePaths = @(& git -C $Root ls-files -- 'native/*.h' 'native/*.hpp' 'native/*.c' 'native/*.cpp')
    $nativeExitCode = $LASTEXITCODE
    if ($nativeExitCode -ne 0) { throw 'Could not list tracked native source files.' }
    $matches = [Collections.Generic.List[string]]::new()
    foreach ($relative in $nativePaths) {
        $path = Get-RepoFile -Root $Root -RelativePath ([string]$relative)
        if ((Get-Sha256 -Path $path) -ceq $Hash.ToLowerInvariant()) { $matches.Add([string]$relative) }
    }
    return @($matches)
}

function Get-ManifestProperty {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) { return $null }
    return $Object.PSObject.Properties[$Name].Value
}

$repo = $null
try {
    $repo = [IO.Path]::GetFullPath($SourceRoot)
    if (-not (Test-Path -LiteralPath $repo -PathType Container)) { throw "Source root is missing: $repo" }
    Assert-RegularDirectory -Path $repo

    $topLevel = @(& git -C $repo rev-parse --show-toplevel 2>$null)
    $gitExitCode = $LASTEXITCODE
    if ($gitExitCode -ne 0 -or $topLevel.Count -eq 0) { throw 'Source root is not a Git worktree.' }
    $gitRoot = [IO.Path]::GetFullPath(($topLevel -join "`n").Trim())
    if (-not [string]::Equals($repo.TrimEnd('\', '/'), $gitRoot.TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'SourceRoot must be the repository top level.'
    }
    Add-Check 'source-root' 'pass' 'SourceRoot is the top level of a regular Git worktree.'
} catch {
    Add-Check 'source-root' 'fail' $_.Exception.Message
}

$versionValid = $ReleaseVersion -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview(\.(0|[1-9][0-9]*))?)?\z'
$commitValid = $SourceCommit -match '^[a-fA-F0-9]{40}\z'
if (-not $versionValid) {
    Add-Check 'release-version' 'fail' 'Version must use the existing release generator semver format.'
} elseif (-not $commitValid) {
    Add-Check 'release-version' 'fail' 'SourceCommit must be a full 40-character Git commit.'
} else {
    $existingRelease = if ($repo) { Join-Path (Join-Path $repo 'releases') $ReleaseVersion } else { $null }
    if ($existingRelease -and (Test-Path -LiteralPath $existingRelease)) {
        Add-Check 'release-version' 'fail' "The version already has a releases/$ReleaseVersion path. Choose a new version."
    } else {
        Add-Check 'release-version' 'pass' 'Version uses the generator format and does not collide with a current release directory.'
    }
}

if ($repo) {
    try {
        $headOutput = @(& git -C $repo rev-parse --verify HEAD 2>$null)
        $headExitCode = $LASTEXITCODE
        $head = ($headOutput -join "`n").Trim().ToLowerInvariant()
        if ($headExitCode -ne 0 -or $head -notmatch '^[a-f0-9]{40}$') { throw 'Could not read the checked-out source commit.' }
        if (-not $commitValid -or $head -cne $SourceCommit.ToLowerInvariant()) {
            throw "Declared source commit $SourceCommit does not match checked-out HEAD $head."
        }
        Add-Check 'source-commit' 'pass' "Declared source commit matches HEAD ($head)."
    } catch {
        Add-Check 'source-commit' 'fail' $_.Exception.Message
    }

    try {
        $status = @(& git -C $repo status --porcelain=v1 --untracked-files=all 2>$null)
        $statusExitCode = $LASTEXITCODE
        if ($statusExitCode -ne 0) { throw 'Could not inspect the source worktree status.' }
        if ($status.Count -gt 0) {
            $preview = ($status | Select-Object -First 8) -join '; '
            throw "Source worktree is not clean (first entries: $preview)."
        }
        $indexOutput = @(& git -C $repo ls-files -v -z 2>$null)
        $indexExitCode = $LASTEXITCODE
        if ($indexExitCode -ne 0) { throw 'Could not inspect tracked-file index flags.' }
        $indexText = $indexOutput -join ''
        $indexRecords = $indexText.Split([char[]]@([char]0), [StringSplitOptions]::RemoveEmptyEntries)
        $hiddenIndexEntries = [Collections.Generic.List[string]]::new()
        foreach ($record in $indexRecords) {
            if ($record.Length -lt 3 -or $record[1] -ne ' ') { throw 'Git returned an invalid NUL-delimited tracked-file index record.' }
            $indexFlag = $record.Substring(0, 1)
            if ($indexFlag -cmatch '^[a-z]$' -or $indexFlag -in @('S', 's')) {
                $hiddenIndexEntries.Add($record.Substring(2))
            }
        }
        if ($hiddenIndexEntries.Count -gt 0) {
            $preview = ($hiddenIndexEntries | Select-Object -First 8) -join ', '
            throw "Tracked files have assume-unchanged or skip-worktree flags: $preview. Clear them deliberately and rerun the gate."
        }
        Add-Check 'clean-source' 'pass' 'No tracked modifications or untracked files are present, and no tracked paths have assume-unchanged or skip-worktree flags.'
    } catch {
        Add-Check 'clean-source' 'fail' $_.Exception.Message
    }
} else {
    Add-Check 'source-commit' 'fail' 'Source commit cannot be checked because SourceRoot is unavailable.'
    Add-Check 'clean-source' 'fail' 'Worktree cleanliness cannot be checked because SourceRoot is unavailable.'
}

$canonicalManifest = $null
if ($repo) {
    try {
        $canonicalPath = Get-RepoFile -Root $repo -RelativePath 'runtime/windows-x64-assets.json'
        $canonicalManifest = Get-Content -LiteralPath $canonicalPath -Raw | ConvertFrom-Json
        if ($canonicalManifest.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace([string]$canonicalManifest.engineVersion) -or
            $canonicalManifest.assets -isnot [array] -or $canonicalManifest.assets.Count -lt 18 -or $canonicalManifest.assets.Count -gt 128) {
            throw 'Canonical runtime manifest does not satisfy the existing schema, engine, and 18-to-128 asset contract.'
        }
        $canonicalAssetPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($asset in $canonicalManifest.assets) {
            $assetRelative = [string]$asset.path
            $assetBytes = 0L
            if ($assetRelative -notmatch '^[a-zA-Z0-9_+./$-]+$' -or $assetRelative.Contains('..') -or
                -not $canonicalAssetPaths.Add($assetRelative) -or
                $asset.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
                -not [long]::TryParse([string]$asset.bytes, [ref]$assetBytes) -or $assetBytes -lt 1 -or $assetBytes -gt 64MB) {
                throw "Canonical runtime asset entry is invalid or duplicated: $assetRelative"
            }
        }
        Add-Check 'canonical-runtime-manifest' 'pass' 'The tracked canonical runtime manifest is present and has a nonempty asset list.'
    } catch {
        Add-Check 'canonical-runtime-manifest' 'fail' $_.Exception.Message
    }
} else {
    Add-Check 'canonical-runtime-manifest' 'fail' 'Canonical manifest cannot be checked because SourceRoot is unavailable.'
}

if ($null -ne $canonicalManifest) {
    $localModules = @($canonicalManifest.modules | Where-Object {
        (Get-ManifestProperty $_ 'sourcePath') -or (Get-ManifestProperty $_ 'build')
    })
    if ($localModules.Count -eq 0) {
        Add-Check 'local-build-inputs' 'fail' 'Canonical runtime manifest declares no local source/build input pairs.'
    } else {
        foreach ($module in $localModules) {
            $moduleId = [string](Get-ManifestProperty $module 'id')
            if ([string]::IsNullOrWhiteSpace($moduleId)) { $moduleId = 'unnamed-module' }
            try {
                $sourceRelative = [string](Get-ManifestProperty $module 'sourcePath')
                $buildRelative = [string](Get-ManifestProperty $module 'build')
                if (-not $sourceRelative -or -not $buildRelative) {
                    throw 'A local module must declare both sourcePath and build.'
                }
                $sourcePath = Get-RepoFile -Root $repo -RelativePath $sourceRelative
                $buildPath = Get-RepoFile -Root $repo -RelativePath $buildRelative
                $expectedSourceHash = [string](Get-ManifestProperty $module 'sourceSha256')
                if ($expectedSourceHash -notmatch '^[a-fA-F0-9]{64}$') { throw 'Local module has no valid sourceSha256.' }
                $actualSourceHash = Get-Sha256 -Path $sourcePath
                if ($actualSourceHash -cne $expectedSourceHash.ToLowerInvariant()) {
                    throw "Source hash mismatch for $sourceRelative (expected $expectedSourceHash, found $actualSourceHash)."
                }
                foreach ($supplementalName in @('readinessSourceSha256', 'policySourceSha256')) {
                    $supplementalHash = [string](Get-ManifestProperty $module $supplementalName)
                    if (-not $supplementalHash) { continue }
                    if ($supplementalHash -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid $supplementalName for $moduleId." }
                    $sourceMatches = @(Find-TrackedNativeSourceByHash -Root $repo -Hash $supplementalHash)
                    if ($sourceMatches.Count -ne 1) {
                        throw "$supplementalName must match exactly one tracked native source file; found $($sourceMatches.Count)."
                    }
                }
                $actualBuildHash = Get-Sha256 -Path $buildPath
                Add-Check "local-build-input:$moduleId" 'pass' "Tracked source $sourceRelative matches sourceSha256 and any declared readiness/policy source hashes resolve to one tracked native source; tracked build pointer $buildRelative exists (SHA-256 $actualBuildHash)."
            } catch {
                Add-Check "local-build-input:$moduleId" 'fail' $_.Exception.Message
            }
        }
    }
} else {
    Add-Check 'local-build-inputs' 'fail' 'Local build inputs cannot be checked because the canonical runtime manifest is unavailable.'
}

$packagePass = $false
try {
    $packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) { throw "Package directory is missing: $packageRoot" }
    Assert-RegularDirectory -Path $packageRoot

    $expectedAssetNames = @($expectedFrontendNames | ForEach-Object { "frontend-$($_)" })
    $expectedTopLevel = @('frontend-update.json') + $expectedAssetNames
    $actualTopLevel = @(Get-ChildItem -LiteralPath $packageRoot -Force | ForEach-Object { $_.Name })
    $unexpected = @($actualTopLevel | Where-Object { $_ -notin $expectedTopLevel })
    $missing = @($expectedTopLevel | Where-Object { $_ -notin $actualTopLevel })
    if ($unexpected.Count -gt 0) { throw "Package directory has unmanifested entries: $($unexpected -join ', ')." }
    if ($missing.Count -gt 0) { throw "Package directory is missing required generator outputs: $($missing -join ', ')." }

    $packageManifestPath = Join-Path $packageRoot 'frontend-update.json'
    Assert-RegularPath -Path $packageManifestPath
    $packageManifest = Get-Content -LiteralPath $packageManifestPath -Raw | ConvertFrom-Json
    $expectedChannel = if ($ReleaseVersion.Contains('-preview')) { 'preview' } else { 'stable' }
    if ($packageManifest.Schema -ne 1 -or $packageManifest.Application -cne 'ClassicDesk' -or
        $packageManifest.Version -cne $ReleaseVersion -or $packageManifest.Channel -cne $expectedChannel -or
        $packageManifest.Platform -cne 'windows' -or $packageManifest.Architecture -cne 'x64' -or
        [string]$packageManifest.Build -cne $SourceCommit.ToLowerInvariant()) {
        throw 'frontend-update.json identity/version/build does not match the existing generator contract and declared source.'
    }
    if ($packageManifest.Files -isnot [array] -or $packageManifest.Files.Count -ne 4) {
        throw 'frontend-update.json must contain exactly the four required frontend assets.'
    }

    foreach ($expectedName in $expectedFrontendNames) {
        $entries = @($packageManifest.Files | Where-Object { $_.Name -ceq $expectedName })
        if ($entries.Count -ne 1) { throw "Manifest must contain exactly one entry for $expectedName." }
        $entry = $entries[0]
        $expectedAsset = "frontend-$expectedName"
        if ($entry.AssetName -cne $expectedAsset) { throw "Generator AssetName mismatch for $expectedName." }
        $assetPath = Join-Path $packageRoot $expectedAsset
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "Published frontend asset is missing: $expectedAsset" }
        Assert-RegularPath -Path $assetPath
        $assetInfo = Get-Item -LiteralPath $assetPath
        $declaredSize = 0L
        if (-not [long]::TryParse([string]$entry.Size, [ref]$declaredSize) -or $declaredSize -le 0 -or $assetInfo.Length -ne $declaredSize) {
            throw "Size mismatch or invalid size for $expectedAsset."
        }
        $declaredHash = [string]$entry.Sha256
        if ($declaredHash -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid SHA-256 in manifest for $expectedAsset." }
        $actualHash = Get-Sha256 -Path $assetPath
        if ($actualHash -cne $declaredHash.ToLowerInvariant()) { throw "SHA-256 mismatch for $expectedAsset." }
    }
    $packagePass = $true
    Add-Check 'frontend-package' 'pass' 'All four generated frontend assets match frontend-update.json size/SHA-256, and manifest version/build identity matches the declaration.'
} catch {
    Add-Check 'frontend-package' 'fail' $_.Exception.Message
}

$runtimeClaim = $Profile -eq 'full-runtime'
$runtimeBundlePass = $false
if (-not $runtimeClaim -and [string]::IsNullOrWhiteSpace($RuntimeBundleDirectory)) {
    Add-Check 'runtime-bundle' 'info' 'Frontend-only profile: no runtime bundle is claimed or implied.'
} elseif ($null -eq $canonicalManifest) {
    Add-Check 'runtime-bundle' 'fail' 'Runtime bundle evidence cannot be checked without the canonical runtime manifest.'
} elseif ([string]::IsNullOrWhiteSpace($RuntimeBundleDirectory)) {
    Add-Check 'runtime-bundle' 'fail' 'full-runtime was claimed but no RuntimeBundleDirectory evidence was supplied.'
} else {
    try {
        $bundleRoot = [IO.Path]::GetFullPath($RuntimeBundleDirectory)
        if (-not (Test-Path -LiteralPath $bundleRoot -PathType Container)) { throw "Runtime bundle directory is missing: $bundleRoot" }
        Assert-RegularDirectory -Path $bundleRoot
        $bundleManifestPath = Join-Path $bundleRoot 'runtime-assets-manifest.json'
        if (-not (Test-Path -LiteralPath $bundleManifestPath -PathType Leaf)) { throw 'Runtime bundle is missing runtime-assets-manifest.json at its root.' }
        Assert-RegularPath -Path $bundleManifestPath
        $canonicalPath = Join-Path $repo 'runtime/windows-x64-assets.json'
        if ((Get-Sha256 -Path $bundleManifestPath) -cne (Get-Sha256 -Path $canonicalPath)) {
            throw 'Runtime bundle manifest bytes do not match the canonical manifest copied by the current runtime preparation contract.'
        }
        $allowedRuntimeFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [void]$allowedRuntimeFiles.Add('runtime-assets-manifest.json')
        foreach ($asset in $canonicalManifest.assets) {
            $relative = [string]$asset.path
            if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)|:') {
                throw "Unsafe canonical runtime asset path: $relative"
            }
            $normalized = $relative.Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar)
            [void]$allowedRuntimeFiles.Add($relative.Replace('\', '/'))
            $assetPath = [IO.Path]::GetFullPath((Join-Path $bundleRoot $normalized))
            $bundlePrefix = $bundleRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
            if (-not $assetPath.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Runtime asset escapes bundle root: $relative" }
            if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "Runtime bundle asset is missing: $relative" }
            Assert-RegularPath -Path $assetPath
            $assetInfo = Get-Item -LiteralPath $assetPath
            if ([long]$asset.bytes -le 0 -or $assetInfo.Length -ne [long]$asset.bytes) { throw "Runtime asset size mismatch: $relative" }
            if ((Get-Sha256 -Path $assetPath) -cne ([string]$asset.sha256).ToLowerInvariant()) { throw "Runtime asset SHA-256 mismatch: $relative" }
        }
        foreach ($entry in Get-ChildItem -LiteralPath $bundleRoot -Force -Recurse) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Runtime bundle contains a reparse path: $($entry.FullName)" }
            if (-not $entry.PSIsContainer) {
                $relative = $entry.FullName.Substring($bundleRoot.TrimEnd('\', '/').Length + 1).Replace('\', '/')
                if (-not $allowedRuntimeFiles.Contains($relative)) { throw "Runtime bundle contains an unmanifested file: $relative" }
            }
        }
        $runtimeBundlePass = $true
        Add-Check 'runtime-bundle' 'pass' "All $($canonicalManifest.assets.Count) canonical runtime assets and the exact runtime manifest are present, match, and are the bundle's only files. This is asset evidence only, not installation or host acceptance."
    } catch {
        Add-Check 'runtime-bundle' 'fail' $_.Exception.Message
    }
}

$failures = @($checks | Where-Object status -eq 'fail').Count
$result = if ($failures -eq 0) { 'ready-for-declared-profile' } else { 'not-ready' }
$report = [ordered]@{
    schemaVersion = 1
    result = $result
    profile = $Profile
    declaredVersion = $ReleaseVersion
    declaredSourceCommit = $SourceCommit.ToLowerInvariant()
    frontendPackageVerified = $packagePass
    runtimeBundleClaimed = $runtimeClaim
    runtimeBundleVerified = $runtimeBundlePass
    checks = @($checks)
    scope = 'Read-only source/package preflight only. No publication, install, signature, license, native runtime execution, real Windows acceptance, or runtime redistribution approval is asserted.'
}
Write-Output ($report | ConvertTo-Json -Depth 8)
if ($failures -gt 0) { throw "Release readiness failed with $failures failing check(s)." }
