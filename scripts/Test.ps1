param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ClassicDesk-tests-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& dotnet run --project (Join-Path $repo 'tests/Core/ActivationProbe.csproj') -c Release -- (Join-Path $OutputDirectory 'core.json')
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
& dotnet run --project (Join-Path $repo 'tests/Host/HostProbe.csproj') -c Release -- (Join-Path $OutputDirectory 'host.json')
if ($LASTEXITCODE -ne 0) { throw 'Host checks failed.' }
& dotnet run --project (Join-Path $repo 'tests/Frontend/ShellFrontendChecks.csproj') -c Release -- (Join-Path $OutputDirectory 'fixtures') (Join-Path $OutputDirectory 'frontend')
if ($LASTEXITCODE -ne 0) { throw 'Frontend checks failed.' }
& dotnet run --project (Join-Path $repo 'tests/ReleaseBoundary/ReleaseBoundary.csproj') -c Release -- (Join-Path $OutputDirectory 'release-boundary.json')
if ($LASTEXITCODE -ne 0) { throw 'Release boundary checks failed.' }
Write-Output "Checks completed. Local reports: $OutputDirectory"
