param(
    [Parameter(Mandatory)][string]$Toolchain,
    [Parameter(Mandatory)][string]$Sdk,
    [Parameter(Mandatory)][string]$EngineLibrary,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference='Stop'
$compiler=Join-Path ([IO.Path]::GetFullPath($Toolchain)) 'bin/clang++.exe'
$argsList=@('-std=c++23','-O2','-shared','-DUNICODE','-D_UNICODE',
    '-DWINVER=0x0A00','-D_WIN32_WINNT=0x0A00','-D_WIN32_IE=0x0A00',
    '-DNTDDI_VERSION=0x0A000008','-D__USE_MINGW_ANSI_STDIO=0','-DWH_MOD',
    '-DWH_MOD_ID=L"taskbar-start-button-position"','-DWH_MOD_VERSION=L"1.3.2-classicdesk.1"',
    '-include','windhawk_api.h','-I',(Join-Path ([IO.Path]::GetFullPath($Sdk)) 'include'),
    '-I',(Join-Path ([IO.Path]::GetFullPath($Toolchain)) 'include'),
    '-L',(Join-Path ([IO.Path]::GetFullPath($Sdk)) 'x86_64-w64-mingw32/lib'),
    '-target','x86_64-w64-mingw32','-Wl,--export-all-symbols','-Wl,--no-insert-timestamp',
    [IO.Path]::GetFullPath($EngineLibrary),(Join-Path $PSScriptRoot 'taskbar-start-button-position.wh.cpp'),
    '-ldwmapi','-lgdi32','-lole32','-loleaut32','-lruntimeobject','-o',[IO.Path]::GetFullPath($Output))
& $compiler @argsList
if($LASTEXITCODE -ne 0){throw "Start position build failed: $LASTEXITCODE"}
Get-FileHash -LiteralPath $Output -Algorithm SHA256
