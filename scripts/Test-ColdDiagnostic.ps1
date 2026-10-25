param([Parameter(Mandatory)][string]$Toolchain,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new isolated output directory'}
New-Item -ItemType Directory -Path $output | Out-Null
$priorTemp=$env:TEMP;$priorTmp=$env:TMP
try {
    $env:TEMP=$output;$env:TMP=$output
    & (Join-Path $repo 'native/Build-Service.ps1') -Toolchain $Toolchain -OutputDirectory $output
    $compiler=Join-Path $Toolchain 'bin/clang++.exe'
    foreach($test in @('service-cold-diagnostic-tests','service-startup-trace-tests','service-start-arguments-tests','service-compatibility-tests','service-session-tests')) {
        $exe=Join-Path $output ($test+'.exe')
        & $compiler -target i686-w64-mingw32 -std=c++23 -O2 -static (Join-Path $repo ('native/'+$test+'.cpp')) -o $exe
        if($LASTEXITCODE -ne 0){throw "Build failed: $test"}
        & $exe | Tee-Object -FilePath (Join-Path $output ($test+'.log'))
        if($LASTEXITCODE -ne 0){throw "Test failed: $test"}
    }
    $io=Join-Path $output 'service-cold-diagnostic-io-tests.exe'
    & $compiler -target i686-w64-mingw32 -std=c++23 -O2 -static -municode -I $output (Join-Path $repo 'native/service-cold-diagnostic-io-tests.cpp') -ladvapi32 -lbcrypt -lwevtapi -o $io
    if($LASTEXITCODE -ne 0){throw 'IO test build failed'}
    & $io (Join-Path $output 'fixture') | Tee-Object -FilePath (Join-Path $output 'io.log')
    if($LASTEXITCODE -ne 0){throw 'IO tests failed'}
    & (Join-Path $output 'ClassicDeskShell.exe') --inspect-cold-diagnostic | Tee-Object -FilePath (Join-Path $output 'boot-inspection.json')
    if($LASTEXITCODE -ne 0){throw 'Read-only cold diagnostic inspection failed'}
    $tokens=$null;$errors=$null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Prepare-ColdDiagnostic.ps1'),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw ($errors | Out-String)}
    [pscustomobject]@{Passed=$true;HostBuilt=$true;PolicyAndIoTests=$true;ServiceStarted=$false;SettingsChanged=$false;ColdBootAccepted=$false} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'report.json') -Encoding UTF8
} finally {$env:TEMP=$priorTemp;$env:TMP=$priorTmp}
