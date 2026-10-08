#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$VmId,
    [Parameter(Mandatory)][guid]$AviaryId,
    [ValidateSet('Setup','Remove')][string]$Mode = 'Setup'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$script:GpuLog = $null
function Write-GpuStatus([string]$Message) {
    if ($script:GpuLog) { Add-Content -LiteralPath $script:GpuLog -Value ((Get-Date -Format o) + ' ' + $Message) -Encoding UTF8 }
    Write-Host $Message
}

function Get-OwnedVm {
    $machine = Get-VM -Id $VmId
    if ($machine.Notes -ne ('Aviary:' + $AviaryId)) { throw 'This VM does not belong to the selected Aviary entry.' }
    return $machine
}
function Restore-Settings($Machine, $Saved) {
    Set-VM -VM $Machine -GuestControlledCacheTypes ([bool]$Saved.Cache) -LowMemoryMappedIoSpace ([uint64]$Saved.Low) -HighMemoryMappedIoSpace ([uint64]$Saved.High)
}
function Stop-Gracefully {
    $machine = Get-OwnedVm
    if ($machine.State -eq 'Running') {
        Stop-VM -VM $machine -Confirm:$false
        $deadline = (Get-Date).AddMinutes(2)
        do {
            $machine = Get-OwnedVm
            if ($machine.State -eq 'Off') { return $machine }
            Start-Sleep -Seconds 2
        } while ((Get-Date) -lt $deadline)
    }
    if ($machine.State -ne 'Off') { throw 'Shut down Windows normally inside this guest, then retry. Aviary will not force power off.' }
    return $machine
}
function Copy-GraphicsFiles($Session, $Gpu) {
    $deviceId = $Gpu.Name.Substring(4).Split('{')[0].TrimEnd('#').Replace('#','\')
    $drivers = @(Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceID -eq $deviceId })
    if ($drivers.Count -ne 1) { throw 'Could not identify the exact signed driver for this GPU.' }
    $driver = $drivers[0]
    $metadata = Get-WindowsDriver -Online -Driver $driver.InfName
    $repository = [IO.Path]::GetFullPath((Join-Path $env:windir 'System32\DriverStore\FileRepository')) + '\'
    $package = Split-Path -Parent $metadata.OriginalFileName
    if (![IO.Path]::GetFullPath($package).StartsWith($repository, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected GPU driver package path.' }
    $guestWindows = Invoke-Command -Session $Session -ScriptBlock { $env:windir }
    $targetRepository = Join-Path $guestWindows 'System32\HostDriverStore\FileRepository'
    Invoke-Command -Session $Session -ScriptBlock { param($Path) New-Item -ItemType Directory -Force -Path $Path | Out-Null } -ArgumentList $targetRepository
    $packages = @{}; $packages[$package] = $true
    $files = @(Get-CimAssociatedInstance -InputObject $driver -Association Win32_PnPSignedDriverCIMDataFile)
    $windowsRoot = [IO.Path]::GetFullPath($env:windir).TrimEnd('\') + '\'
    foreach ($file in $files) {
        $source = [IO.Path]::GetFullPath($file.Name)
        if ($source.StartsWith($repository, [StringComparison]::OrdinalIgnoreCase)) {
            $name = $source.Substring($repository.Length).Split('\')[0]
            $packages[(Join-Path $repository $name)] = $true
        } elseif ($source.StartsWith($windowsRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $destination = Join-Path $guestWindows $source.Substring($windowsRoot.Length)
            Invoke-Command -Session $Session -ScriptBlock { param($Path) New-Item -ItemType Directory -Force -Path $Path | Out-Null } -ArgumentList (Split-Path -Parent $destination)
            Copy-Item -LiteralPath $source -Destination $destination -ToSession $Session -Force
        } else { throw "Unexpected driver file outside Windows: $source" }
    }
    foreach ($folder in $packages.Keys) {
        Copy-Item -LiteralPath $folder -Destination $targetRepository -ToSession $Session -Recurse -Force
    }
    Write-Host ('Copied matching host driver: ' + $driver.DeviceName + ' ' + $driver.DriverVersion)
}
function Set-GpuHardware($machine, $gpu, $stateFile) {
    $saved = [ordered]@{ VmId = $VmId.ToString(); AviaryId = $AviaryId.ToString(); InstancePath = $gpu.Name; Cache = [bool]$machine.GuestControlledCacheTypes; Low = [uint64]$machine.LowMemoryMappedIoSpace; High = [uint64]$machine.HighMemoryMappedIoSpace }
    # Persist recovery before changing any VM hardware; never overwrite an existing recovery record.
    $json = $saved | ConvertTo-Json
    $stream = [IO.File]::Open($stateFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes($json); $stream.Write($bytes,0,$bytes.Length) } finally { $stream.Dispose() }
    try {
        Set-VM -VM $machine -GuestControlledCacheTypes $true -LowMemoryMappedIoSpace 1GB -HighMemoryMappedIoSpace 32GB
        Add-VMGpuPartitionAdapter -VM $machine -InstancePath $gpu.Name
        Start-VM -VM $machine | Out-Null
    } catch {
        $failure = $_
        $machine = Get-OwnedVm
        if ($machine.State -eq 'Off') {
            Get-VMGpuPartitionAdapter -VM $machine | Where-Object { $_.InstancePath -eq $gpu.Name } | Remove-VMGpuPartitionAdapter
            Restore-Settings $machine $saved
            Remove-Item -LiteralPath $stateFile
        }
        throw $failure
    }
}
function Invoke-GpuSetup {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this helper as administrator.' }
    Write-GpuStatus 'Checking Hyper-V and VM ownership.'
    Import-Module Hyper-V
    $machine = Get-OwnedVm
    # Recovery data belongs to this host, even when Aviary itself is portable.
    $stateRoot = Join-Path $env:ProgramData 'Aviary\GpuRecovery'
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    & "$env:windir\System32\icacls.exe" $stateRoot /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect GPU recovery data.' }
    $stateFile = Join-Path $stateRoot ($VmId.ToString() + '.json')
    if ($Mode -eq 'Remove') {
        if (!(Test-Path -LiteralPath $stateFile)) { throw 'No Aviary GPU recovery record exists for this VM.' }
        $saved = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
        if ($saved.VmId -ne $VmId.ToString() -or $saved.AviaryId -ne $AviaryId.ToString()) { throw 'GPU recovery identity does not match.' }
        $adapters = @(Get-VMGpuPartitionAdapter -VM $machine)
        if (@($adapters | Where-Object { $_.InstancePath -ne $saved.InstancePath }).Count) { throw 'GPU configuration changed outside Aviary; undo cancelled.' }
        Write-Host 'Undo removes the GPU assignment and restores VM memory settings. Copied guest driver files remain.'
        if ((Read-Host 'Save your guest work. Type REMOVE to shut it down and undo') -cne 'REMOVE') { return }
        $machine = Stop-Gracefully
        foreach ($adapter in $adapters) { Remove-VMGpuPartitionAdapter -VMGpuPartitionAdapter $adapter }
        Restore-Settings $machine $saved
        Remove-Item -LiteralPath $stateFile
        Write-Host 'GPU assignment removed. The guest is shut down; start it from Aviary.'
        return
    }
    if (Test-Path -LiteralPath $stateFile) { throw 'A GPU setup/recovery record already exists. Use Undo GPU setup before configuring again.' }
    if (@(Get-VMGpuPartitionAdapter -VM $machine).Count) { throw 'This VM already has a GPU partition. Aviary will not replace it.' }
    if ($machine.Generation -ne 2 -or $machine.State -ne 'Running') { throw 'Start this Generation 2 VM and finish installing Windows before GPU setup.' }
    $gpus = @(Get-VMHostPartitionableGpu)
    if (!$gpus.Count) { throw 'Windows does not expose a partitionable GPU. Check the host GPU driver.' }
    $selected = 0
    if ($gpus.Count -gt 1) {
        for ($i=0; $i -lt $gpus.Count; $i++) { Write-Host "$i : $($gpus[$i].Name)" }
        $choice = Read-Host 'GPU number'
        if (![int]::TryParse($choice, [ref]$selected) -or $selected -lt 0 -or $selected -ge $gpus.Count) { throw 'Invalid GPU selection.' }
    }
    $gpu = $gpus[$selected]
    Write-Host 'Experimental GPU-P for Windows guests. Desktop Windows / consumer GPUs are not a Microsoft-supported GPU-P configuration.'
    Write-Host 'This copies the matching host GPU driver, shuts down the guest normally, assigns a partition and starts it again.'
    Write-Host 'VMConnect remains available, but accelerated desktop streaming may require separate guest display/streaming software.'
    if ((Read-Host 'Save your guest work. Type SETUP to continue') -cne 'SETUP') { return }
    Write-GpuStatus 'Waiting for guest administrator credentials. Credentials are not logged.'
    $session = $null
    $credential = $null
    try {
        # PowerShell Direct needs the guest's local account (e.g. .\Dev) and its password; a Windows Hello PIN is rejected.
        for ($attempt = 1; !$session; $attempt++) {
            $credential = Get-Credential -Message 'Guest administrator, e.g. .\YourGuestUser, with its password (not a PIN or host login)'
            if (!$credential) { return }
            Write-GpuStatus 'Connecting to the guest through PowerShell Direct.'
            try { $session = New-PSSession -VMId $VmId -Credential $credential -ErrorAction Stop }
            catch {
                if ($attempt -ge 3 -or $_.Exception.Message -notlike '*credential*') { throw }
                Write-GpuStatus 'The guest rejected those credentials. Use the guest account name and its password, not a PIN.'
            }
        }
        $guest = Invoke-Command -Session $session -ScriptBlock {
            $admin = (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            [pscustomobject]@{ Build = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber; Admin = $admin; Is64 = [Environment]::Is64BitOperatingSystem }
        }
        $hostBuild = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
        if (!$guest.Admin -or !$guest.Is64) { throw 'A 64-bit Windows guest administrator is required.' }
        if ($guest.Build -ne $hostBuild) { throw "Host build $hostBuild and guest build $($guest.Build) differ. Use matching Windows builds before this experimental setup." }
        Write-GpuStatus 'Guest access and Windows build checks passed. Copying matching GPU drivers.'
        Copy-GraphicsFiles $session $gpu
    } finally { if ($session) { Remove-PSSession $session }; $credential = $null }
    Write-GpuStatus 'Driver copy completed. Requesting normal guest shutdown.'
    $machine = Stop-Gracefully
    Write-GpuStatus 'Guest shut down. Assigning GPU partition and restarting.'
    Set-GpuHardware $machine $gpu $stateFile
    Write-GpuStatus 'GPU partition assigned. Graphics are NOT yet verified.'
    Write-Host 'Inside the guest, check Device Manager for your GPU and run dxdiag to inspect the driver and Direct3D support.'
    Write-Host 'Then test an actual 3D application. VMConnect alone does not prove GPU acceleration.'
    Write-Host 'After host driver updates, undo and repeat setup to refresh the guest driver files.'
}
# Dot-sourcing exposes functions for isolated tests without touching Hyper-V.
if ($MyInvocation.InvocationName -ne '.') {
    $script:GpuLog = Join-Path $PSScriptRoot ('gpu-setup-' + $VmId.ToString() + '.log')
    Set-Content -LiteralPath $script:GpuLog -Value ('GPU setup started: ' + (Get-Date -Format o)) -Encoding UTF8
    try { Invoke-GpuSetup } catch {
        Write-GpuStatus ('ERROR: ' + $_.Exception.Message)
        Add-Content -LiteralPath $script:GpuLog -Value ('Location: ' + $_.InvocationInfo.PositionMessage) -Encoding UTF8
    }
    Write-Host ('Diagnostic log: ' + $script:GpuLog)
    Read-Host 'Press Enter to close' | Out-Null
}
