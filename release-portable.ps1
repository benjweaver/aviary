param([switch]$SkipArchive,[switch]$Isolated)
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot '.build'
$env:NUGET_PACKAGES=Join-Path $PSScriptRoot '.packages'
$env:MSBuildEnableWorkloadResolver='false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
if($Isolated){
 $env:APPDATA=Join-Path $PSScriptRoot '.build\AppData'
 $env:LOCALAPPDATA=Join-Path $PSScriptRoot '.build\LocalAppData'
 $env:TEMP=Join-Path $PSScriptRoot '.build\Temp'
 $env:TMP=$env:TEMP
 New-Item -ItemType Directory -Force -Path $env:TEMP | Out-Null
}
$qemu=Join-Path $PSScriptRoot '.tools\qemu'
if(!(Test-Path "$qemu\qemu-system-x86_64.exe")){throw 'Run setup-deps.ps1 first to obtain QEMU.'}
$version='v'+([xml](Get-Content -LiteralPath "$PSScriptRoot\Directory.Build.props" -Raw)).Project.PropertyGroup.Version
$releaseRoot=Join-Path $PSScriptRoot ('.build\releases\'+$version+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$destination=Join-Path $releaseRoot 'Aviary'
$dotnet=Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
& $dotnet publish "$PSScriptRoot\src\Aviary.App\Aviary.App.csproj" -c Release -r win-x64 --self-contained true -o $destination -m:1 -nr:false "-p:RestoreConfigFile=$PSScriptRoot\NuGet.Config"
if($LASTEXITCODE -ne 0){throw 'Release build failed'}
foreach($required in 'Aviary.App.exe','Aviary.App.pri','App.xbf','coreclr.dll'){
 if(!(Test-Path (Join-Path $destination $required))){throw "Missing release file: $required"}
}
New-Item -ItemType Directory -Force -Path "$destination\runtime\qemu" | Out-Null
Copy-Item -Path "$qemu\*" -Destination "$destination\runtime\qemu" -Recurse
Copy-Item -LiteralPath "$PSScriptRoot\LICENSE" -Destination "$destination\LICENSE-Aviary.txt"
foreach($script in 'install.ps1','update.ps1','uninstall.ps1'){Copy-Item -LiteralPath "$PSScriptRoot\packaging\windows\$script" -Destination $destination}
Set-Content -LiteralPath "$destination\portable.txt" -Value 'Keep this file beside Aviary.App.exe to store settings and machines in Data.'
@'
Aviary portable for Windows x64

Extract the entire folder to a writable location and run Aviary.App.exe.
No .NET installation, Windows App SDK installation, admin install, or PATH changes are needed.
Settings and new machines are stored in Data beside the application.
To move machines between PCs, shut them down and copy the entire Aviary folder.
Place installer ISOs inside this folder too if you want their paths to move with it.
External disks and ISOs remain external and must be available on the destination PC.
Native Hyper-V machines remain registered with Windows; moving them requires
Hyper-V export/import even when Aviary is portable.
When updating, keep your Data folder. Release archives never contain user machines.

Requires x64 Windows 10 1809 or later; Windows 11 recommended.
Software emulation works without WHPX. Hardware acceleration requires Windows
Hypervisor Platform and firmware virtualization, which your IT team may need to enable.
Portable packaging does not bypass workplace application-control or security policies.

QEMU is a separate GPL-licensed engine; its bundled COPYING and COPYING.LIB files
are in runtime/qemu. Windows builds: https://qemu.weilnetz.de/w64/
Upstream source: https://www.qemu.org/download/#source
Bundled build: QEMU 11.1.0 (qemu-w64-setup-20260811 by Stefan Weil, git e470268ff4). Source: https://gitlab.com/qemu-project/qemu/-/tree/e470268ff4 and https://www.qemu.org/download/#source
Aviary is GPL-3.0-or-later (LICENSE-Aviary.txt); source: https://github.com/benjweaver/aviary
'@ | Set-Content -LiteralPath "$destination\READ-ME.txt"
if(!$SkipArchive){
 # The folder inside stays named Aviary in every release; the archive carries the version.
 $archive=Join-Path $releaseRoot "Aviary-$version-windows-x86_64.zip"
 $sevenZip=Join-Path $PSScriptRoot '.tools\7zip\7z.exe'
 if(Test-Path -LiteralPath $sevenZip){
  Push-Location $releaseRoot
  try {& $sevenZip a -tzip $archive 'Aviary' -mx=5 -bso0 -bsp0; if($LASTEXITCODE -ne 0){throw 'Archive creation failed'}} finally {Pop-Location}
 } else {Compress-Archive -LiteralPath $destination -DestinationPath $archive -CompressionLevel Optimal}
 $checksum=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
 Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS') -Value ($checksum+'  '+(Split-Path $archive -Leaf))
 Write-Output "Portable archive: $archive"
}
Write-Output "Portable app: $destination\Aviary.App.exe"
