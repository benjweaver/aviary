param([switch]$IncludeTestIso)
$ErrorActionPreference='Stop'
$tools=Join-Path $PSScriptRoot '.tools'
$downloads=Join-Path $tools 'downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
function Fetch([string]$Url,[string]$Name,[string]$Algorithm,[string]$Hash) {
 $file=Join-Path $downloads $Name
 if(!(Test-Path -LiteralPath $file)){Invoke-WebRequest $Url -OutFile $file}
 if((Get-FileHash -LiteralPath $file -Algorithm $Algorithm).Hash -ne $Hash){throw "Checksum mismatch: $Name"}
 return $file
}
$qemu=Fetch 'https://qemu.weilnetz.de/w64/qemu-w64-setup-20260811.exe' 'qemu.exe' 'SHA512' '5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543'
$extractor=Fetch 'https://github.com/ip7z/7zip/releases/download/26.04/7zr.exe' '7zr.exe' 'SHA256' '256FECA8E274E5DA655E2A284FABAFD9F554365EB164862089DACD4E8276D282'
$archive=Fetch 'https://github.com/ip7z/7zip/releases/download/26.04/7z2604-x64.exe' '7zip.exe' 'SHA256' 'D54BF805F9F3704D1E8DB2FA3498AE7EF2DF0312B40B558E7C71C734430A665D'
if(!(Test-Path "$tools\7zip\7z.exe")){& $extractor x $archive "-o$tools\7zip" -y;if($LASTEXITCODE -ne 0){throw '7-Zip extraction failed'}}
if(!(Test-Path "$tools\qemu\qemu-system-x86_64.exe")){& "$tools\7zip\7z.exe" x $qemu "-o$tools\qemu" -y;if($LASTEXITCODE -ne 0){throw 'QEMU extraction failed'}}
if($IncludeTestIso){$null=Fetch 'https://dl-cdn.alpinelinux.org/alpine/v3.24/releases/x86_64/alpine-virt-3.24.2-x86_64.iso' 'alpine.iso' 'SHA256' '3AB424762AF704B2C2A9E57DF1DC37F982AF260071504D977F2FB96822E7130B'}
& "$tools\qemu\qemu-system-x86_64.exe" --version
if($LASTEXITCODE -ne 0){throw 'QEMU version check failed'}
Write-Host 'Dependencies ready. Run .\build.ps1 -Test, then .\start.ps1.'
