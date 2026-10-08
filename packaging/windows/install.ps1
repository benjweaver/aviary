# Installs the latest Aviary release for your user, with no admin rights.
# The app goes in %LOCALAPPDATA%\Programs\Aviary with a Start menu shortcut.
# PATH and autostart are left alone.
#
#   irm https://raw.githubusercontent.com/benjweaver/aviary/main/packaging/windows/install.ps1 | iex
#
# update.ps1 and uninstall.ps1 beside it run this with -Update or -Uninstall.
# Updating refuses when Aviary isn't installed and does nothing when the latest
# release is already installed. Uninstalling removes only the files this script
# installed: your VM library, disks and settings are always kept.
#
# -Archive installs a local release ZIP instead (checked against the
# SHA256SUMS beside it, or -ChecksumFile).
#
# Written for Windows PowerShell 5.1, which every Windows 10 and 11 has.

param([string]$Archive, [string]$ChecksumFile, [switch]$Update, [switch]$Uninstall,
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\Aviary'),
    [switch]$NoShortcut)

# Everything runs inside a script block so that `irm | iex` leaves nothing behind
# in your session, and a failure throws rather than closing your terminal.
& {
    param([string]$Archive, [string]$ChecksumFile, [bool]$Update, [bool]$Uninstall, [string]$InstallDirectory, [bool]$NoShortcut)
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue' # the progress bar slows downloads right down
    $repo = 'benjweaver/aviary'
    $target = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    if ($target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\') -or
        $target -eq $env:USERPROFILE -or $target -eq (Join-Path $env:LOCALAPPDATA 'Aviary')) { throw 'Choose a dedicated program folder.' }
    $manifestPath = Join-Path $target 'install-manifest.json'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Aviary.lnk'
    function OwnedPath([string]$relative) {
        $path = [IO.Path]::GetFullPath((Join-Path $target $relative))
        if (!$path.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe installation manifest path.' }
        return $path
    }
    function Read-Manifest { if (Test-Path -LiteralPath $manifestPath) { Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json } }

    if ($Uninstall) {
        $manifest = Read-Manifest
        if (!$manifest) { throw "Aviary isn't installed in $target." }
        if (Get-Process Aviary.App -ErrorAction SilentlyContinue) { throw 'Close Aviary and shut down its VMs before removing it.' }
        foreach ($path in @($manifest.Files | ForEach-Object { OwnedPath $_ })) { if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force } }
        if (!$NoShortcut -and (Test-Path -LiteralPath $shortcut)) {
            $shell = New-Object -ComObject WScript.Shell
            if ($shell.CreateShortcut($shortcut).TargetPath -eq (Join-Path $target 'Aviary.App.exe')) { Remove-Item -LiteralPath $shortcut -Force }
        }
        Remove-Item -LiteralPath $manifestPath -Force
        # Only remove empty directories. Unrecognized files and all VM data survive.
        Get-ChildItem -LiteralPath $target -Directory -Recurse | Sort-Object FullName -Descending | ForEach-Object {
            if (!(Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $_.FullName }
        }
        if (!(Get-ChildItem -LiteralPath $target -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $target }
        Write-Host "Aviary is removed. Your VMs and settings in $env:LOCALAPPDATA\Aviary are still there."
        return
    }
    $installed = Read-Manifest
    if ($Update -and !$installed) {
        throw "Aviary isn't installed in $target. Install it with: irm https://raw.githubusercontent.com/$repo/main/packaging/windows/install.ps1 | iex"
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) ('aviary-install-' + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $version = $null
        if ($Archive) {
            $Archive = (Resolve-Path -LiteralPath $Archive).Path
            if (!$ChecksumFile) { $ChecksumFile = Join-Path (Split-Path $Archive -Parent) 'SHA256SUMS' }
            $zipName = Split-Path $Archive -Leaf
            if ($zipName -match '^Aviary-(v[^-]+)-') { $version = $Matches[1] }
        } else {
            $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
            if ($arch -ne 'AMD64') { throw "Aviary has no build for $arch Windows." }
            # Windows PowerShell 5.1 doesn't offer TLS 1.2 on its own, and GitHub needs it.
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            $version = (Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -UseBasicParsing).tag_name
            if ($installed -and $installed.Version -eq $version) { Write-Host "Aviary $version is already the latest release."; return }
            if ($installed) { Write-Host "Updating Aviary $($installed.Version) to $version" }
            $zipName = "Aviary-$version-windows-x86_64.zip"
            $base = "https://github.com/$repo/releases/download/$version"
            Write-Host "Downloading Aviary $version"
            $Archive = Join-Path $work $zipName
            Invoke-WebRequest "$base/$zipName" -OutFile $Archive -UseBasicParsing
            # Saved and read back rather than taken from .Content, which PowerShell 5.1
            # hands over as bytes for a file served without a text type.
            $ChecksumFile = Join-Path $work 'SHA256SUMS'
            Invoke-WebRequest "$base/SHA256SUMS" -OutFile $ChecksumFile -UseBasicParsing
        }
        $line = Get-Content -LiteralPath $ChecksumFile | Where-Object { $_ -match ('^[a-fA-F0-9]{64}\s+\*?' + [regex]::Escape($zipName) + '\s*$') } | Select-Object -First 1
        $hash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash
        if (!$line -or $hash -ne ($line -split '\s+')[0]) { throw "$zipName doesn't match its checksum; nothing was installed." }
        if (Get-Process Aviary.App -ErrorAction SilentlyContinue) { throw 'Close Aviary and shut down its VMs before installing or updating it.' }

        # The zip holds a single folder named Aviary.
        $extracted = Join-Path $work 'extracted'
        Expand-Archive -LiteralPath $Archive -DestinationPath $extracted
        $payload = Join-Path $extracted 'Aviary'
        foreach ($required in 'Aviary.App.exe', 'Aviary.App.pri', 'App.xbf', 'runtime\qemu\qemu-system-x86_64.exe') {
            if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Incomplete release: $required" }
        }
        Get-ChildItem -LiteralPath $payload -File -Recurse | Unblock-File
        if (Test-Path -LiteralPath (Join-Path $target 'portable.txt')) { throw 'This is a portable copy. Choose a different installation folder.' }
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        $files = @()
        foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
            $relative = $file.FullName.Substring($payload.Length + 1)
            # Installed copies keep their library in %LOCALAPPDATA%\Aviary, never beside the app.
            if ($relative -eq 'portable.txt' -or $relative.StartsWith('Data\', [StringComparison]::OrdinalIgnoreCase)) { continue }
            $destination = OwnedPath $relative
            New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
            $files += $relative
        }
        if ($installed) {
            foreach ($relative in $installed.Files) { if ($relative -notin $files) { $stale = OwnedPath $relative; if (Test-Path -LiteralPath $stale -PathType Leaf) { Remove-Item -LiteralPath $stale -Force } } }
        }
        @{ Version = $version; Files = $files; ArchiveHash = $hash } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath
        if (!$NoShortcut) {
            $shell = New-Object -ComObject WScript.Shell; $link = $shell.CreateShortcut($shortcut)
            $link.TargetPath = Join-Path $target 'Aviary.App.exe'; $link.WorkingDirectory = $target; $link.Description = 'Aviary virtual machine manager'
            if (Test-Path -LiteralPath (Join-Path $target 'Assets\Aviary.ico')) { $link.IconLocation = (Join-Path $target 'Assets\Aviary.ico') + ',0' }
            $link.Save()
        }
        Write-Host "Installed Aviary $version in $target. Open it from the Start menu."
    } finally {
        $resolvedWork = [IO.Path]::GetFullPath($work)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolvedWork.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $resolvedWork -Leaf) -like 'aviary-install-*') { Remove-Item -LiteralPath $resolvedWork -Recurse -Force }
    }
} $Archive $ChecksumFile $Update.IsPresent $Uninstall.IsPresent $InstallDirectory $NoShortcut.IsPresent
