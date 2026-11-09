<#
.SYNOPSIS
    Builds the DSPi Console launcher stub.

.DESCRIPTION
    Produces a standalone DSPiConsole.exe that starts app\DSPiConsole.exe. It is
    statically linked so the release root can hold this one file and nothing
    else: no C runtime DLLs, no .NET runtime, no companion config.

    Requires MinGW-w64 (msys2). gcc is located on PATH, falling back to the
    default msys64 install.

.EXAMPLE
    ./build.ps1 -Version 1.1.5
#>
[CmdletBinding()]
param(
    [string]$Version = '1.1.5',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolved here: Windows PowerShell 5.1 can leave $PSScriptRoot empty in a
# parameter default when the script runs with -File, which put the output in
# \bin at the drive root.
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'bin' }

# Locate the toolchain.
$gcc = (Get-Command gcc -ErrorAction SilentlyContinue).Source
if (-not $gcc) { $gcc = 'C:\msys64\mingw64\bin\gcc.exe' }
$windres = (Get-Command windres -ErrorAction SilentlyContinue).Source
if (-not $windres) { $windres = 'C:\msys64\mingw64\bin\windres.exe' }
foreach ($tool in @($gcc, $windres)) {
    if (-not (Test-Path $tool)) { throw "Required build tool not found: $tool" }
}

# VERSIONINFO wants a 4-part comma list; pad whatever we were given.
$parts = @($Version.Split('-')[0].Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$verNum = ($parts[0..3]) -join ','
$verStr = ($parts[0..3]) -join '.'

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

$rc = Join-Path $OutDir 'launcher.rc'
$res = Join-Path $OutDir 'launcher.res'
$exe = Join-Path $OutDir 'DSPiConsole.exe'

(Get-Content "$PSScriptRoot\launcher.rc.in" -Raw).
    Replace('@VERSION_NUM@', $verNum).
    Replace('@VERSION_STR@', $verStr) |
    Set-Content -Path $rc -Encoding utf8

Write-Host "Compiling resources ($verStr)..."
& $windres --include-dir $PSScriptRoot $rc -O coff -o $res
if ($LASTEXITCODE -ne 0) { throw "windres failed with exit code $LASTEXITCODE" }

Write-Host 'Linking launcher...'
# -municode  : wWinMain entry point
# -mwindows  : GUI subsystem, no console window
# -static -s : no runtime DLL dependencies, stripped
& $gcc -municode -mwindows -O2 -s -static `
    -o $exe "$PSScriptRoot\launcher.c" $res `
    -lkernel32 -luser32 -lversion
if ($LASTEXITCODE -ne 0) { throw "gcc failed with exit code $LASTEXITCODE" }

$size = (Get-Item $exe).Length
Write-Host "Built $exe ($size bytes)"
