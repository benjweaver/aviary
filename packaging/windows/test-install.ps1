$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path "$PSScriptRoot\..\..").Path
$testRoot=Join-Path $projectRoot ('.build\packaging-test-'+[guid]::NewGuid())
$payload=Join-Path $testRoot 'Aviary'
$target=Join-Path $testRoot 'installed'
New-Item -ItemType Directory -Force -Path "$payload\runtime\qemu","$payload\Data" | Out-Null
foreach($file in 'Aviary.App.exe','Aviary.App.pri','App.xbf','runtime\qemu\qemu-system-x86_64.exe','portable.txt','Data\private.txt'){
 Set-Content -LiteralPath (Join-Path $payload $file) -Value 'fixture'
}
$archive=Join-Path $testRoot 'Aviary-v9.9.9-windows-x86_64.zip'
Compress-Archive -LiteralPath $payload -DestinationPath $archive
$sums=Join-Path $testRoot 'SHA256SUMS'
Set-Content -LiteralPath $sums -Value (('0'*64)+'  Aviary-v9.9.9-windows-x86_64.zip')
$rejected=$false
try {& "$PSScriptRoot\install.ps1" -Archive $archive -InstallDirectory $target -NoShortcut} catch {$rejected=$_.Exception.Message -like '*checksum*'}
if(!$rejected -or (Test-Path $target)){throw 'Bad checksum was not rejected before installation'}
Set-Content -LiteralPath $sums -Value ((Get-FileHash $archive).Hash+'  Aviary-v9.9.9-windows-x86_64.zip')
& "$PSScriptRoot\install.ps1" -Archive $archive -InstallDirectory $target -NoShortcut
if((Get-Content "$target\install-manifest.json" -Raw | ConvertFrom-Json).Version -ne 'v9.9.9'){throw 'Installed version was not recorded'}
if(!(Test-Path "$target\Aviary.App.exe") -or (Test-Path "$target\portable.txt") -or (Test-Path "$target\Data")){throw 'Installed/portable separation failed'}
Set-Content -LiteralPath "$target\keep.txt" -Value 'user-owned'
& "$PSScriptRoot\install.ps1" -Update -Archive $archive -InstallDirectory $target -NoShortcut
& "$PSScriptRoot\install.ps1" -Uninstall -InstallDirectory $target -NoShortcut
if((Test-Path "$target\Aviary.App.exe") -or !(Test-Path "$target\keep.txt")){throw 'Uninstall did not preserve untracked files'}
Write-Output 'PASS: checksum rejection, install, update, portable-data exclusion, and safe uninstall.'
