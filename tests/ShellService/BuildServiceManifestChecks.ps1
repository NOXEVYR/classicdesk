param(
    [Parameter(Mandatory)][string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
$manifestPathFull = [IO.Path]::GetFullPath($ManifestPath)
$buildScriptPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\native\Build-Service.ps1'))
if (-not (Test-Path -LiteralPath $manifestPathFull -PathType Leaf)) {
    throw "Runtime manifest not found: $manifestPathFull"
}
if (-not (Test-Path -LiteralPath $buildScriptPath -PathType Leaf)) {
    throw "Build script not found: $buildScriptPath"
}

$buildSource = [IO.File]::ReadAllText($buildScriptPath)
$parserTokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput($buildSource, [ref]$parserTokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Build-Service.ps1 has PowerShell parse errors: $($parseErrors[0].Message)"
}

# Evaluate the exact manifest-validation/header-generation prefix, stopping before
# compiler discovery or invocation. This exercises production logic without building or running a host.
$prefixStart = $buildSource.IndexOf('$manifestPath=', [StringComparison]::Ordinal)
$compilerStart = $buildSource.IndexOf('$compiler=Join-Path', [StringComparison]::Ordinal)
$requiredCheck = $buildSource.IndexOf('Missing required fixed runtime asset', [StringComparison]::Ordinal)
if ($prefixStart -lt 0 -or $compilerStart -le $prefixStart -or $requiredCheck -lt $prefixStart -or $compilerStart -lt $requiredCheck) {
    throw 'Could not establish that manifest validation and header generation precede compiler setup.'
}
$buildPrefix = $buildSource.Substring($prefixStart, $compilerStart - $prefixStart)

$actualManifest = Get-Content -LiteralPath $manifestPathFull -Raw | ConvertFrom-Json
if ($actualManifest.engineVersion -ne '1.7.3' -or $actualManifest.assets -isnot [array] -or $actualManifest.assets.Count -ne 20) {
    throw "Expected the supplied actual runtime manifest to contain 20 assets; found $(@($actualManifest.assets).Count)."
}

$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$temporaryRoot = Join-Path $temporaryParent ('ClassicDesk-BuildServiceManifestChecks-' + [Guid]::NewGuid().ToString('N'))
if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($temporaryRoot)) -ne $temporaryParent) {
    throw 'Temporary test directory escaped the system temporary directory.'
}
[void][IO.Directory]::CreateDirectory($temporaryRoot)
$script:caseResults = [Collections.Generic.List[object]]::new()

function Invoke-BuildPrefix {
    param(
        [Parameter(Mandatory)][string]$FixturePath,
        [Parameter(Mandatory)][string]$OutputPath,
        [Parameter(Mandatory)][string]$PrefixText,
        [Parameter(Mandatory)][string]$BuildScriptRoot
    )

    # Match the variables normally supplied by Build-Service.ps1's param and preamble.
    $Manifest = [IO.Path]::GetFullPath($FixturePath)
    $OutputDirectory = [IO.Path]::GetFullPath($OutputPath)
    $repo = Split-Path -Parent $BuildScriptRoot
    Invoke-Expression $PrefixText | Out-Null
}

function Invoke-TestCase {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )

    try {
        & $Body
        $script:caseResults.Add([pscustomobject]@{ Name = $Name; Passed = $true; Detail = 'passed' })
    }
    catch {
        $script:caseResults.Add([pscustomobject]@{ Name = $Name; Passed = $false; Detail = $_.Exception.Message })
    }
}

function Copy-ManifestObject {
    param([Parameter(Mandatory)]$ManifestObject)
    return ($ManifestObject | ConvertTo-Json -Depth 32 | ConvertFrom-Json)
}

function Save-ManifestObject {
    param(
        [Parameter(Mandatory)]$ManifestObject,
        [Parameter(Mandatory)][string]$Path
    )
    $json = $ManifestObject | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

try {
    Invoke-TestCase -Name 'legacy typed Manifest collision is detected' -Body {
        function Invoke-LegacyCollision {
            param([Parameter(Mandatory)][string]$Manifest)
            $manifest = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
            @($manifest.assets).Count
        }

        $legacyFailed = $false
        try {
            $legacyCount = Invoke-LegacyCollision -Manifest $manifestPathFull
            $legacyFailed = ([int]$legacyCount -ne 20)
        }
        catch {
            $legacyFailed = $true
        }
        if (-not $legacyFailed) {
            throw 'The old case-insensitive Manifest/$manifest collision unexpectedly retained all 20 assets.'
        }
    }

    Invoke-TestCase -Name 'current build prefix emits all 20 actual assets without compiling' -Body {
        $outputPath = Join-Path $temporaryRoot 'valid-output'
        Invoke-BuildPrefix -FixturePath $manifestPathFull -OutputPath $outputPath -PrefixText $buildPrefix -BuildScriptRoot (Split-Path -Parent $buildScriptPath)
        $headerPath = Join-Path $outputPath 'service-assets.generated.h'
        if (-not (Test-Path -LiteralPath $headerPath -PathType Leaf)) {
            throw 'Generated asset header is missing.'
        }
        $header = [IO.File]::ReadAllText($headerPath)
        $assetEntryCount = [regex]::Matches($header, '(?m)^\s*\{L"').Count
        if ($assetEntryCount -ne 20) {
            throw "Expected 20 generated asset entries; found $assetEntryCount."
        }
        if (Test-Path -LiteralPath (Join-Path $outputPath 'ClassicDeskShell.exe')) {
            throw 'The test unexpectedly produced a host executable.'
        }
    }

    $invalidCases = @(
        [pscustomobject]@{ Name = 'empty manifest rejected before output/compiler'; Kind = 'empty'; Expected = 'Invalid or empty fixed runtime asset manifest' },
        [pscustomobject]@{ Name = 'duplicate asset rejected before output/compiler'; Kind = 'duplicate'; Expected = 'Invalid or duplicate fixed runtime asset' },
        [pscustomobject]@{ Name = 'invalid byte count rejected before output/compiler'; Kind = 'badbytes'; Expected = 'Invalid or duplicate fixed runtime asset' },
        [pscustomobject]@{ Name = 'missing required asset rejected before output/compiler'; Kind = 'missing'; Expected = 'Missing required fixed runtime asset:' }
    )

    foreach ($invalidCase in $invalidCases) {
        $case = $invalidCase
        Invoke-TestCase -Name $case.Name -Body {
            $fixture = Copy-ManifestObject -ManifestObject $actualManifest
            switch ($case.Kind) {
                'empty' { $fixture.assets = @() }
                'duplicate' { $fixture.assets = @($fixture.assets) + @($fixture.assets[0]) }
                'badbytes' { $fixture.assets[0].bytes = 0 }
                'missing' {
                    $requiredPath = 'AppData/Engine/Mods/64/windows-11-taskbar-styler_1.9.dll'
                    if (@($fixture.assets | Where-Object { $_.path -eq $requiredPath }).Count -ne 1) {
                        throw "The supplied actual manifest does not contain required fixture asset $requiredPath."
                    }
                    $fixture.assets = @($fixture.assets | Where-Object { $_.path -ne $requiredPath })
                }
                default { throw "Unknown fixture kind: $($case.Kind)" }
            }

            $fixturePath = Join-Path $temporaryRoot ($case.Kind + '.json')
            $outputPath = Join-Path $temporaryRoot ($case.Kind + '-output')
            Save-ManifestObject -ManifestObject $fixture -Path $fixturePath
            $rejected = $false
            $errorMessage = ''
            try {
                Invoke-BuildPrefix -FixturePath $fixturePath -OutputPath $outputPath -PrefixText $buildPrefix -BuildScriptRoot (Split-Path -Parent $buildScriptPath)
            }
            catch {
                $rejected = $true
                $errorMessage = $_.Exception.Message
            }
            if (-not $rejected) {
                throw 'Invalid manifest was accepted.'
            }
            if ($case.Kind -eq 'missing') {
                if (-not $errorMessage.StartsWith($case.Expected, [StringComparison]::Ordinal)) {
                    throw "Rejected with the wrong validation error: $errorMessage"
                }
            }
            elseif ($errorMessage -ne $case.Expected) {
                throw "Rejected with the wrong validation error: $errorMessage"
            }
            if (Test-Path -LiteralPath $outputPath) {
                throw 'Output directory was created before invalid manifest rejection.'
            }
        }
    }
}
finally {
    if ([IO.Directory]::Exists($temporaryRoot)) {
        $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
        if ([IO.Path]::GetDirectoryName($resolvedRoot) -ne $temporaryParent -or [IO.Path]::GetFileName($resolvedRoot) -notlike 'ClassicDesk-BuildServiceManifestChecks-*') {
            throw "Refusing to remove unexpected temporary test path: $resolvedRoot"
        }
        [IO.Directory]::Delete($resolvedRoot, $true)
    }
}

$failedCases = @($script:caseResults | Where-Object { -not $_.Passed })
[pscustomobject]@{
    Passed = $script:caseResults.Count - $failedCases.Count
    Failed = $failedCases.Count
    Cases = @($script:caseResults)
} | ConvertTo-Json -Depth 5

if ($failedCases.Count -gt 0) {
    exit 1
}
