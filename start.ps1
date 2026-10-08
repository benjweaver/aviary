$ErrorActionPreference='Stop'
$env:AVIARY_QEMU=Join-Path $PSScriptRoot '.tools\qemu'
$exe=Join-Path $PSScriptRoot 'src\Aviary.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Aviary.App.exe'
if(!(Test-Path -LiteralPath $exe)){ & "$PSScriptRoot\build.ps1" }
& $exe
