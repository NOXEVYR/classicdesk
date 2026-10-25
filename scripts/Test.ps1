param([string]$OutputDirectory = '', [string]$ServiceBundle = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExecutable = if ($dotnetCommand -and $dotnetCommand.CommandType -eq 'Application') { $dotnetCommand.Source } else { Join-Path $env:USERPROFILE '.dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetExecutable -PathType Leaf)) { throw 'The .NET SDK dotnet executable was not found on PATH or in the current user .dotnet directory.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('ClassicDesk-tests-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& $dotnetExecutable run --project (Join-Path $repo 'tests/Core/ActivationProbe.csproj') -c Release -- (Join-Path $OutputDirectory 'core.json')
if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/Host/HostProbe.csproj') -c Release -- (Join-Path $OutputDirectory 'host.json')
if ($LASTEXITCODE -ne 0) { throw 'Host checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/Frontend/ShellFrontendChecks.csproj') -c Release -- (Join-Path $OutputDirectory 'fixtures') (Join-Path $OutputDirectory 'frontend')
if ($LASTEXITCODE -ne 0) { throw 'Frontend checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/VisualPreview/VisualPreview.csproj') -c Release -- (Join-Path $OutputDirectory 'visual-preview')
if ($LASTEXITCODE -ne 0) { throw 'Profile visual preview checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/FocusMenu/FocusMenu.csproj') -c Release -- (Join-Path $OutputDirectory 'focus-menu')
if ($LASTEXITCODE -ne 0) { throw 'Focus and menu offscreen checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/ReleaseBoundary/ReleaseBoundary.csproj') -c Release -- (Join-Path $OutputDirectory 'release-boundary.json')
if ($LASTEXITCODE -ne 0) { throw 'Release boundary checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/ServiceUI/ServiceUI.csproj') -c Release -- (Join-Path $OutputDirectory 'service-ui')
if ($LASTEXITCODE -ne 0) { throw 'Service UI checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/TaskbarDiagnostics/TaskbarDiagnostics.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Taskbar diagnostics checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/ExplorerDiagnostics/ExplorerDiagnostics.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Explorer diagnostics checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/ExplorerRebuild/ExplorerRebuild.csproj') -c Release -- (Join-Path $OutputDirectory 'explorer-rebuild')
if ($LASTEXITCODE -ne 0) { throw 'Explorer rebuild checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/ExplorerRebuildUI/ExplorerRebuildUI.csproj') -c Release -- (Join-Path $OutputDirectory 'explorer-rebuild-ui')
if ($LASTEXITCODE -ne 0) { throw 'Explorer rebuild UI checks failed.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts/Test-ExplorerStartupPrerequisite.ps1') -OutputDirectory (Join-Path $OutputDirectory 'explorer-startup-prerequisite')
if ($LASTEXITCODE -ne 0) { throw 'Explorer startup prerequisite checks failed.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts/Test-PrepareNextBoot.ps1') -OutputDirectory (Join-Path $OutputDirectory 'nextboot-preparation')
if ($LASTEXITCODE -ne 0) { throw 'Next boot preparation checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/ExplorerUI/ExplorerUI.csproj') -c Release -- (Join-Path $OutputDirectory 'explorer-ui')
if ($LASTEXITCODE -ne 0) { throw 'Explorer UI checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/ExplorerPrelaunch/ExplorerPrelaunch.csproj') -c Release -- (Join-Path $OutputDirectory 'explorer-prelaunch')
if ($LASTEXITCODE -ne 0) { throw 'Explorer prelaunch ownership checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/ServiceSession/ServiceSession.csproj') -c Release -- (Join-Path $OutputDirectory 'service-session')
if ($LASTEXITCODE -ne 0) { throw 'Current-session service checks failed.' }
$sessionInstallerOutput = Join-Path ([IO.Path]::GetTempPath()) ('CDSS-' + [guid]::NewGuid().ToString('N').Substring(0,8))
& "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tests/ShellService/CurrentSessionInstallerChecks.ps1') -OutputDirectory $sessionInstallerOutput
$sessionInstallerExit = $LASTEXITCODE
Copy-Item -LiteralPath (Join-Path $sessionInstallerOutput 'current-session-installer.json') -Destination (Join-Path $OutputDirectory 'current-session-installer.json')
if ($sessionInstallerExit -ne 0) { throw 'Current-session installer checks failed.' }
$manifestChecks = Join-Path $repo 'tests/ShellService/BuildServiceManifestChecks.ps1'
$manifestPath = Join-Path $repo 'runtime/windows-x64-assets.json'
& "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $manifestChecks -ManifestPath $manifestPath |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-service-manifest.json') -Encoding UTF8
if ($LASTEXITCODE -ne 0) { throw 'Build service manifest checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/AutoHide/AutoHide.csproj') -c Release -- (Join-Path $OutputDirectory 'auto-hide')
if ($LASTEXITCODE -ne 0) { throw 'Auto-hide isolated checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/ColdUpgrade/ColdUpgrade.csproj') -c Release -- (Join-Path $OutputDirectory 'cold-upgrade.json')
if ($LASTEXITCODE -ne 0) { throw 'Cold upgrade checks failed.' }
if ($ServiceBundle) {
    & $dotnetExecutable run --project (Join-Path $repo 'tests/ShellService/ShellService.csproj') -c Release -- ([IO.Path]::GetFullPath($ServiceBundle)) (Join-Path $OutputDirectory 'service')
    if ($LASTEXITCODE -ne 0) { throw 'Service package checks failed.' }
    # Windows PowerShell 5.1 uses legacy path limits; keep the nested installer fixture short.
    $installerOutput = Join-Path ([IO.Path]::GetTempPath()) ('CD15-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    & "$env:SystemRoot/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tests/ShellService/InstallerChecks.ps1') -ReviewedBundle ([IO.Path]::GetFullPath($ServiceBundle)) -OutputDirectory $installerOutput
    $installerExit = $LASTEXITCODE
    Copy-Item -LiteralPath (Join-Path $installerOutput 'report.json') -Destination (Join-Path $OutputDirectory 'installer.json')
    if ($installerExit -ne 0) { throw 'Isolated installer checks failed.' }
}
& $dotnetExecutable run --project (Join-Path $repo 'tests/UpdatesFeed/UpdatesFeed.csproj') -c Release -- (Join-Path $OutputDirectory 'updates-feed.json')
if ($LASTEXITCODE -ne 0) { throw 'Frontend update feed checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/UpdatesInstall/UpdatesInstall.csproj') -c Release -- (Join-Path $OutputDirectory 'updates-install')
if ($LASTEXITCODE -ne 0) { throw 'Frontend update installer checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/UpdatesUI/UpdatesUI.csproj') -c Release -- (Join-Path $OutputDirectory 'updates-ui')
if ($LASTEXITCODE -ne 0) { throw 'Frontend update UI checks failed.' }
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tests/StartCore/Test.ps1') -ReportDirectory (Join-Path $OutputDirectory 'start-core') -NuGetConfig (Join-Path $repo 'tests/StartCore/NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'ClassicStart isolated core checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/StartUI/StartUI.csproj') -c Release -- (Join-Path $OutputDirectory 'start-ui')
if ($LASTEXITCODE -ne 0) { throw 'ClassicStart offscreen UI checks failed.' }
& $dotnetExecutable run --project (Join-Path $repo 'tests/LayoutDesign/LayoutDesign.csproj') -c Release -- (Join-Path $OutputDirectory 'layout-design')
if ($LASTEXITCODE -ne 0) { throw 'Layout design offscreen checks failed.' }
Write-Output "Checks completed. Local reports: $OutputDirectory"
