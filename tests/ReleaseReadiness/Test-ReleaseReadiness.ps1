$ErrorActionPreference = 'Stop'
$gatePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../scripts/Test-ReleaseReadiness.ps1'))
$tempRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('ClassicDesk-ReleaseReadiness-' + [Guid]::NewGuid().ToString('N'))))
$results = [Collections.Generic.List[object]]::new()

function Assert-Case {
    param([string]$Name, [bool]$Passed, [string]$Detail = '')
    $results.Add([ordered]@{ name = $Name; passed = $Passed; detail = $Detail })
    if (-not $Passed) { throw "Release readiness fixture failed: $Name. $Detail" }
}

function Write-JsonFile {
    param([string]$Path, [object]$Value)
    $json = $Value | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($Path, $json + "`n", [Text.UTF8Encoding]::new($false))
}

function Get-Hash {
    param([string]$Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-Gate {
    param(
        [string]$Root,
        [string]$Package,
        [string]$Commit,
        [string]$Profile = 'frontend-only',
        [string]$RuntimeBundle
    )
    $arguments = @{
        SourceRoot = $Root
        ReleaseVersion = '9.8.7-preview'
        SourceCommit = $Commit
        PackageDirectory = $Package
        Profile = $Profile
    }
    if ($RuntimeBundle) { $arguments.RuntimeBundleDirectory = $RuntimeBundle }
    $captured = [Collections.Generic.List[string]]::new()
    $invocationError = ''
    try {
        & $gatePath @arguments *>&1 | ForEach-Object { $captured.Add([string]$_) }
    } catch {
        $invocationError = $_.Exception.Message
    }
    try {
        $report = ($captured -join "`n") | ConvertFrom-Json
        return [pscustomobject]@{ Passed = [string]::IsNullOrEmpty($invocationError); Report = $report; Error = $invocationError }
    } catch {
        $parseError = $_.Exception.Message
        if ($invocationError) { $parseError = "$invocationError; report parse failed: $parseError" }
        return [pscustomobject]@{ Passed = $false; Report = $null; Error = $parseError }
    }
}

function Assert-FailureCheck {
    param([string]$Name, [object]$Result, [string]$CheckId)
    $matchingChecks = @()
    if ($Result.Report) {
        $matchingChecks = @($Result.Report.checks | Where-Object { $_.id -ceq $CheckId -and $_.status -ceq 'fail' })
    }
    $detail = if ($matchingChecks.Count -eq 1) { $matchingChecks[0].detail } else { $Result.Error }
    Assert-Case -Name $Name -Passed (-not $Result.Passed -and $matchingChecks.Count -eq 1) -Detail $detail
}

function New-Fixture {
    param([string]$Name, [switch]$OmitBuild, [switch]$IgnoreBuild)
    $root = Join-Path $tempRoot "$Name-source"
    $package = Join-Path $tempRoot "$Name-package"
    $runtimeBundle = Join-Path $tempRoot "$Name-runtime"
    New-Item -ItemType Directory -Path (Join-Path $root 'runtime') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'native') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $root 'src') -Force | Out-Null
    New-Item -ItemType Directory -Path $package -Force | Out-Null
    New-Item -ItemType Directory -Path $runtimeBundle -Force | Out-Null

    $sourcePath = Join-Path $root 'native\source.cpp'
    $buildPath = Join-Path $root 'native\build.ps1'
    $readinessPath = Join-Path $root 'native\readiness.h'
    $frontendSourcePath = Join-Path $root 'src\Product.cs'
    [IO.File]::WriteAllText($sourcePath, 'fixture native source', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($readinessPath, 'fixture readiness source', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($frontendSourcePath, 'namespace Fixture; internal sealed class Product {}', [Text.UTF8Encoding]::new($false))
    if (-not $OmitBuild) { [IO.File]::WriteAllText($buildPath, '# fixture build pointer', [Text.UTF8Encoding]::new($false)) }
    if ($IgnoreBuild) { [IO.File]::WriteAllText((Join-Path $root '.gitignore'), "native/build.ps1`n", [Text.UTF8Encoding]::new($false)) }
    $runtimeAssets = @()
    for ($index = 1; $index -le 18; $index++) {
        $assetName = 'asset-{0:d2}.bin' -f $index
        $runtimeAsset = Join-Path (Join-Path $root 'runtime') $assetName
        $bytes = [byte[]](1, 3, 5, $index)
        [IO.File]::WriteAllBytes($runtimeAsset, $bytes)
        $runtimeAssets += [ordered]@{
            path = $assetName
            bytes = (Get-Item -LiteralPath $runtimeAsset).Length
            sha256 = Get-Hash $runtimeAsset
        }
    }
    $runtimeManifest = [ordered]@{
        schemaVersion = 1
        engineVersion = '1.7.3'
        modules = @([ordered]@{
            id = 'fixture-module'
            sourcePath = 'native/source.cpp'
            sourceSha256 = Get-Hash $sourcePath
            readinessSourceSha256 = Get-Hash $readinessPath
            build = 'native/build.ps1'
        })
        assets = $runtimeAssets
    }
    $canonicalRuntimePath = Join-Path $root 'runtime\windows-x64-assets.json'
    Write-JsonFile -Path $canonicalRuntimePath -Value $runtimeManifest
    Copy-Item -LiteralPath $canonicalRuntimePath -Destination (Join-Path $runtimeBundle 'runtime-assets-manifest.json')
    foreach ($asset in $runtimeAssets) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $root 'runtime') $asset.path) -Destination (Join-Path $runtimeBundle $asset.path)
    }

    & git -C $root init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git init failed in an isolated fixture.' }
    & git -C $root config --local user.name 'Release readiness fixture'
    if ($LASTEXITCODE -ne 0) { throw 'git user.name setup failed in an isolated fixture.' }
    & git -C $root config --local user.email 'release-readiness@example.invalid'
    if ($LASTEXITCODE -ne 0) { throw 'git user.email setup failed in an isolated fixture.' }
    & git -C $root config --local core.autocrlf false
    if ($LASTEXITCODE -ne 0) { throw 'git core.autocrlf setup failed in an isolated fixture.' }
    & git -C $root add --all
    if ($LASTEXITCODE -ne 0) { throw 'git add failed in an isolated fixture.' }
    if ($IgnoreBuild) {
        & git -C $root add -f -- native/build.ps1
        if ($LASTEXITCODE -ne 0) { throw 'git force-add failed in an isolated fixture.' }
    }
    & git -C $root commit --quiet -m 'synthetic release readiness fixture'
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed in an isolated fixture.' }
    $commit = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'git rev-parse failed in an isolated fixture.' }

    $files = @()
    foreach ($name in @('ClassicDesk.exe', 'ClassicDesk.dll', 'ClassicDesk.deps.json', 'ClassicDesk.runtimeconfig.json')) {
        $assetName = "frontend-$name"
        $path = Join-Path $package $assetName
        [IO.File]::WriteAllText($path, "synthetic:$name", [Text.UTF8Encoding]::new($false))
        $files += [ordered]@{
            Name = $name
            AssetName = $assetName
            Size = (Get-Item -LiteralPath $path).Length
            Sha256 = Get-Hash $path
        }
    }
    Write-JsonFile -Path (Join-Path $package 'frontend-update.json') -Value ([ordered]@{
        Schema = 1
        Application = 'ClassicDesk'
        Version = '9.8.7-preview'
        Channel = 'preview'
        Platform = 'windows'
        Architecture = 'x64'
        Build = $commit
        Files = $files
    })
    return [pscustomobject]@{ Root = $root; Package = $package; Commit = $commit; RuntimeBundle = $runtimeBundle }
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    $good = New-Fixture -Name 'valid'
    $success = Invoke-Gate -Root $good.Root -Package $good.Package -Commit $good.Commit
    Assert-Case -Name 'clean frontend-only source and four generated assets pass' -Passed $success.Passed -Detail $success.Error
    Assert-Case -Name 'successful profile does not imply runtime bundle' -Passed ($success.Passed -and $success.Report.runtimeBundleClaimed -eq $false -and $success.Report.profile -eq 'frontend-only')
    $runtimeSuccess = Invoke-Gate -Root $good.Root -Package $good.Package -Commit $good.Commit -Profile 'Full-Runtime' -RuntimeBundle $good.RuntimeBundle
    Assert-Case -Name 'mixed-case full-runtime with evidence passes the runtime gate' -Passed ($runtimeSuccess.Passed -and $runtimeSuccess.Report.runtimeBundleClaimed -eq $true -and $runtimeSuccess.Report.runtimeBundleVerified -eq $true) -Detail $runtimeSuccess.Error

    $dirtySource = New-Fixture -Name 'dirty-source'
    [IO.File]::AppendAllText((Join-Path $dirtySource.Root 'native\source.cpp'), 'changed after commit')
    $dirtySourceResult = Invoke-Gate -Root $dirtySource.Root -Package $dirtySource.Package -Commit $dirtySource.Commit
    Assert-FailureCheck -Name 'modified tracked source fails the clean-source precondition' -Result $dirtySourceResult -CheckId 'clean-source'

    $assumeUnchanged = New-Fixture -Name 'assume-unchanged'
    & git -C $assumeUnchanged.Root update-index --assume-unchanged -- src/Product.cs
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the assume-unchanged flag in the isolated fixture.' }
    [IO.File]::AppendAllText((Join-Path $assumeUnchanged.Root 'src\Product.cs'), '// changed under assume-unchanged')
    $assumeStatus = @(& git -C $assumeUnchanged.Root status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the assume-unchanged fixture status.' }
    Assert-Case -Name 'fixture confirms git status hides assume-unchanged edit' -Passed ($assumeStatus.Count -eq 0)
    $assumeResult = Invoke-Gate -Root $assumeUnchanged.Root -Package $assumeUnchanged.Package -Commit $assumeUnchanged.Commit
    Assert-FailureCheck -Name 'assume-unchanged frontend source still fails clean-source' -Result $assumeResult -CheckId 'clean-source'

    $skipWorktree = New-Fixture -Name 'skip-worktree'
    & git -C $skipWorktree.Root update-index --skip-worktree -- src/Product.cs
    if ($LASTEXITCODE -ne 0) { throw 'Could not set the skip-worktree flag in the isolated fixture.' }
    [IO.File]::AppendAllText((Join-Path $skipWorktree.Root 'src\Product.cs'), '// changed under skip-worktree')
    $skipStatus = @(& git -C $skipWorktree.Root status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the skip-worktree fixture status.' }
    Assert-Case -Name 'fixture confirms git status hides skip-worktree edit' -Passed ($skipStatus.Count -eq 0)
    $skipResult = Invoke-Gate -Root $skipWorktree.Root -Package $skipWorktree.Package -Commit $skipWorktree.Commit
    Assert-FailureCheck -Name 'skip-worktree frontend source still fails clean-source' -Result $skipResult -CheckId 'clean-source'

    $extraRuntime = New-Fixture -Name 'extra-runtime'
    [IO.File]::WriteAllText((Join-Path $extraRuntime.RuntimeBundle 'local-settings.json'), '{"fixture":true}', [Text.UTF8Encoding]::new($false))
    $extraRuntimeResult = Invoke-Gate -Root $extraRuntime.Root -Package $extraRuntime.Package -Commit $extraRuntime.Commit -Profile 'full-runtime' -RuntimeBundle $extraRuntime.RuntimeBundle
    Assert-FailureCheck -Name 'runtime bundle rejects unmanifested files' -Result $extraRuntimeResult -CheckId 'runtime-bundle'

    $missingBuild = New-Fixture -Name 'missing-build' -OmitBuild
    $missingBuildResult = Invoke-Gate -Root $missingBuild.Root -Package $missingBuild.Package -Commit $missingBuild.Commit
    Assert-FailureCheck -Name 'missing declared local build input fails' -Result $missingBuildResult -CheckId 'local-build-input:fixture-module'

    $ignoredBuild = New-Fixture -Name 'ignored-build' -IgnoreBuild
    $ignoredBuildResult = Invoke-Gate -Root $ignoredBuild.Root -Package $ignoredBuild.Package -Commit $ignoredBuild.Commit
    Assert-FailureCheck -Name 'declared local build input matching an ignore rule fails' -Result $ignoredBuildResult -CheckId 'local-build-input:fixture-module'

    $sizeMismatch = New-Fixture -Name 'size-mismatch'
    [IO.File]::AppendAllText((Join-Path $sizeMismatch.Package 'frontend-ClassicDesk.dll'), 'changed after manifest')
    $sizeResult = Invoke-Gate -Root $sizeMismatch.Root -Package $sizeMismatch.Package -Commit $sizeMismatch.Commit
    Assert-FailureCheck -Name 'changed published asset fails its manifest size' -Result $sizeResult -CheckId 'frontend-package'

    $staleHash = New-Fixture -Name 'stale-hash'
    $changedBytes = [IO.File]::ReadAllBytes((Join-Path $staleHash.Package 'frontend-ClassicDesk.dll'))
    $changedBytes[0] = [byte]($changedBytes[0] -bxor 1)
    [IO.File]::WriteAllBytes((Join-Path $staleHash.Package 'frontend-ClassicDesk.dll'), $changedBytes)
    $staleResult = Invoke-Gate -Root $staleHash.Root -Package $staleHash.Package -Commit $staleHash.Commit
    Assert-FailureCheck -Name 'same-size published asset with stale digest fails' -Result $staleResult -CheckId 'frontend-package'

    $frontendClaim = New-Fixture -Name 'frontend-claim'
    $frontendClaimResult = Invoke-Gate -Root $frontendClaim.Root -Package $frontendClaim.Package -Commit $frontendClaim.Commit -Profile 'FULL-RUNTIME'
    Assert-FailureCheck -Name 'uppercase FULL-RUNTIME without evidence fails' -Result $frontendClaimResult -CheckId 'runtime-bundle'

    $lowerClaim = New-Fixture -Name 'lower-claim'
    $lowerClaimResult = Invoke-Gate -Root $lowerClaim.Root -Package $lowerClaim.Package -Commit $lowerClaim.Commit -Profile 'full-runtime'
    Assert-FailureCheck -Name 'lowercase full-runtime without evidence still fails' -Result $lowerClaimResult -CheckId 'runtime-bundle'

    $wrongCommit = New-Fixture -Name 'wrong-commit'
    $wrongCommitResult = Invoke-Gate -Root $wrongCommit.Root -Package $wrongCommit.Package -Commit ('0' * 40)
    Assert-FailureCheck -Name 'declared commit different from clean HEAD fails' -Result $wrongCommitResult -CheckId 'source-commit'

    $failed = @($results | Where-Object { -not $_.passed }).Count
    [ordered]@{
        passed = $results.Count - $failed
        failed = $failed
        mode = 'synthetic temporary Git repositories and non-executable fixture files only'
        writesOutsideTemporaryDirectory = 0
        checks = @($results)
    } | ConvertTo-Json -Depth 8
    if ($failed -gt 0) { throw "Release readiness synthetic tests failed: $failed." }
} finally {
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $resolvedTempRoot = [IO.Path]::GetFullPath($tempRoot)
    if ($resolvedTempRoot.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -and
        ((Split-Path -Leaf $resolvedTempRoot) -match '^ClassicDesk-ReleaseReadiness-[a-f0-9]{32}$') -and
        (Test-Path -LiteralPath $resolvedTempRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
