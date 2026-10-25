param([Parameter(Mandatory=$true)][string]$ReportDirectory,
      [Parameter(Mandatory=$true)][string]$NuGetConfig)
$ErrorActionPreference = 'Stop'
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetExecutable = if ($dotnetCommand -and $dotnetCommand.CommandType -eq 'Application') { $dotnetCommand.Source } else { Join-Path $env:USERPROFILE '.dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetExecutable -PathType Leaf)) { throw 'The .NET SDK was not found on PATH or in the current user .dotnet directory.' }
# Only unique synthetic local fixtures; no real Start Menu, settings or application launch.
$reportPath = [System.IO.Path]::GetFullPath($ReportDirectory)
if ($reportPath.StartsWith('\\') -or $reportPath.StartsWith('//')) { throw 'Use a local report directory.' }
$fixturePath = Join-Path $reportPath ('junction-fixtures-' + [guid]::NewGuid().ToString('N'))
$targetPath = Join-Path $fixturePath 'target'
$scanPath = Join-Path $fixturePath 'scan'
New-Item -ItemType Directory -Path $targetPath,$scanPath -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $targetPath 'Outside.lnk'), 'Synthetic shortcut; never parsed or launched')
New-Item -ItemType Junction -Path (Join-Path $scanPath 'linked') -Target $targetPath | Out-Null
New-Item -ItemType Junction -Path (Join-Path $scanPath 'spare') -Target $targetPath | Out-Null
$projectPath = Join-Path $PSScriptRoot 'StartCore.csproj'
& $dotnetExecutable restore $projectPath --configfile $NuGetConfig
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnetExecutable run --project $projectPath -c Release --no-restore -- $reportPath $fixturePath
exit $LASTEXITCODE
