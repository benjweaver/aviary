param([switch]$Test,[switch]$Run,[switch]$Isolated)
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
if(Test-Path "$PSScriptRoot\.tools\qemu\qemu-system-x86_64.exe"){$env:AVIARY_QEMU=Join-Path $PSScriptRoot '.tools\qemu'}
if(Test-Path "$PSScriptRoot\.tools\downloads\alpine.iso"){$env:AVIARY_TEST_ISO=Join-Path $PSScriptRoot '.tools\downloads\alpine.iso'}
$dotnet=Get-Command dotnet -ErrorAction SilentlyContinue
if($dotnet){$exe=$dotnet.Source}else{$exe=Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'}
& $exe build "$PSScriptRoot\Aviary.slnx" -m:1 -nr:false "-p:RestoreConfigFile=$PSScriptRoot\NuGet.Config"
if($LASTEXITCODE -ne 0){throw 'Build failed'}
if($Test){& $exe test "$PSScriptRoot\src\Aviary.Tests\Aviary.Tests.csproj" --no-build -m:1;if($LASTEXITCODE -ne 0){throw 'Tests failed'}}
if($Run){Start-Process -FilePath "$PSScriptRoot\src\Aviary.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Aviary.App.exe"}
