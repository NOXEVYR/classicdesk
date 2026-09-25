param([string]$OutputDirectory = '', [string]$ServiceBundle = '')
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
& dotnet run --project (Join-Path $repo 'tests/ServiceUI/ServiceUI.csproj') -c Release -- (Join-Path $OutputDirectory 'service-ui')
if ($LASTEXITCODE -ne 0) { throw 'Service UI checks failed.' }
& dotnet run --project (Join-Path $repo 'tests/AutoHide/AutoHide.csproj') -c Release -- (Join-Path $OutputDirectory 'auto-hide')
if ($LASTEXITCODE -ne 0) { throw 'Auto-hide isolated checks failed.' }
& dotnet run --project (Join-Path $repo 'tests/ColdUpgrade/ColdUpgrade.csproj') -c Release -- (Join-Path $OutputDirectory 'cold-upgrade.json')
if ($LASTEXITCODE -ne 0) { throw 'Cold upgrade checks failed.' }
if ($ServiceBundle) {
    & dotnet run --project (Join-Path $repo 'tests/ShellService/ShellService.csproj') -c Release -- ([IO.Path]::GetFullPath($ServiceBundle)) (Join-Path $OutputDirectory 'service')
    if ($LASTEXITCODE -ne 0) { throw 'Service package checks failed.' }
    # Windows PowerShell 5.1 uses legacy path limits; keep the nested installer fixture short.
    $installerOutput = Join-Path ([IO.Path]::GetTempPath()) ('CD15-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tests/ShellService/InstallerChecks.ps1') -ReviewedBundle ([IO.Path]::GetFullPath($ServiceBundle)) -OutputDirectory $installerOutput
    $installerExit = $LASTEXITCODE
    Copy-Item -LiteralPath (Join-Path $installerOutput 'report.json') -Destination (Join-Path $OutputDirectory 'installer.json')
    if ($installerExit -ne 0) { throw 'Isolated installer checks failed.' }
}
Write-Output "Checks completed. Local reports: $OutputDirectory"
