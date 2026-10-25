param(
    [Parameter(Mandatory)][string]$Toolchain,
    [Parameter(Mandatory)][string]$Sdk,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$compiler=Join-Path ([IO.Path]::GetFullPath($Toolchain)) 'bin/clang++.exe'
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Test output directory must be new.'}
New-Item -ItemType Directory -Path $output | Out-Null
$exe=Join-Path $output 'explorer-frame-entry-tests.exe'
$flags=@('-std=c++23','-O2','-static','-DUNICODE','-D_UNICODE',
    '-DWINVER=0x0A00','-D_WIN32_WINNT=0x0A00','-D_WIN32_IE=0x0A00',
    '-DNTDDI_VERSION=0x0A000008','-D__USE_MINGW_ANSI_STDIO=0',
    '-I',(Join-Path ([IO.Path]::GetFullPath($Sdk)) 'include'),
    '-I',(Join-Path ([IO.Path]::GetFullPath($Toolchain)) 'include'),
    '-target','x86_64-w64-mingw32',
    (Join-Path $PSScriptRoot 'explorer-frame-entry-tests.cpp'),
    '-lole32','-loleaut32','-lversion','-luuid','-o',$exe)
& $compiler @flags
if($LASTEXITCODE -ne 0){throw "Entry check build failed: $LASTEXITCODE"}
& $exe
if($LASTEXITCODE -ne 0){throw "Entry checks failed: $LASTEXITCODE"}
