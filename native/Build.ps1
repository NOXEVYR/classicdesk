param(
    [Parameter(Mandatory)][string]$Toolchain,
    [Parameter(Mandatory)][string]$Sdk,
    [Parameter(Mandatory)][string]$EngineLibrary,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference='Stop'
$compiler=Join-Path ([IO.Path]::GetFullPath($Toolchain)) 'bin/clang++.exe'
$sdkRoot=[IO.Path]::GetFullPath($Sdk)
$argsList=@('-std=c++23','-O2','-shared','-DUNICODE','-D_UNICODE',
    '-DWINVER=0x0A00','-D_WIN32_WINNT=0x0A00','-D_WIN32_IE=0x0A00',
    '-DNTDDI_VERSION=0x0A000008','-D__USE_MINGW_ANSI_STDIO=0','-DWH_MOD',
    '-DWH_MOD_ID=L"taskbar-background-helper"','-DWH_MOD_VERSION=L"1.2-classicdesk.1"',
    '-include','windhawk_api.h','-I',(Join-Path $sdkRoot 'include'),
    '-I',(Join-Path ([IO.Path]::GetFullPath($Toolchain)) 'include'),
    '-L',(Join-Path $sdkRoot 'x86_64-w64-mingw32/lib'),
    '-target','x86_64-w64-mingw32','-Wl,--export-all-symbols',
    [IO.Path]::GetFullPath($EngineLibrary),(Join-Path $PSScriptRoot 'taskbar-background-helper.wh.cpp'),
    '-ldwmapi','-lgdi32','-lole32','-loleaut32','-lruntimeobject','-o',[IO.Path]::GetFullPath($Output))
& $compiler @argsList
if($LASTEXITCODE -ne 0){throw "Native build failed: $LASTEXITCODE"}
Get-FileHash -LiteralPath $Output -Algorithm SHA256
