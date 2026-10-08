# Aviary guest SSH setup for Windows. Run once from an elevated Windows PowerShell inside the guest.
# Installs OpenSSH Server, pins the host key Aviary generated, authorizes this PC's key for the current
# administrator account, and allows SSH only from the host. Reports the account name back to Aviary.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$reportUrl = '__REPORT_URL__'
function Send-Report([string]$kind, [string]$value) {
    if ($reportUrl.StartsWith('kvp:')) {
        # Hyper-V: the data exchange (KVP) integration service carries this value to the host. The token
        # identifies this run, so a report left by an earlier setup is never mistaken for this one.
        $kvp = 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest'
        if (Test-Path -LiteralPath $kvp) { Set-ItemProperty -LiteralPath $kvp -Name 'AviarySsh' -Value ($reportUrl.Substring(4) + '/' + $kind + '/' + $value) }
    } else {
        try { Invoke-WebRequest -UseBasicParsing ($reportUrl + '/' + $kind + '/' + [uri]::EscapeDataString($value)) | Out-Null } catch { }
    }
}
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Send-Report 'error' 'not-admin'; throw 'Run this from PowerShell opened as administrator.' }
    $user = $env:USERNAME

    if (!(Get-Service sshd -ErrorAction SilentlyContinue)) {
        Write-Host 'Installing OpenSSH Server (may take a few minutes)...'
        Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null
    }
    Set-Service sshd -StartupType Automatic
    Start-Service sshd   # first start creates %ProgramData%\ssh and its default configuration
    $ssh = Join-Path $env:ProgramData 'ssh'

    function Protect([string]$path) {
        & icacls.exe $path /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not protect $path." }
    }
    $hostKey = Join-Path $ssh 'ssh_host_ed25519_key'
    [IO.File]::WriteAllBytes($hostKey, [Convert]::FromBase64String('__HOST_KEY_B64__'))
    Set-Content -LiteralPath ($hostKey + '.pub') -Value '__HOST_PUBLIC_KEY__' -Encoding ascii
    Protect $hostKey

    # Administrators authenticate with administrators_authorized_keys in the default sshd_config.
    $authorized = Join-Path $ssh 'administrators_authorized_keys'
    $key = '__AUTHORIZED_KEY__'
    $existing = if (Test-Path -LiteralPath $authorized) { @(Get-Content -LiteralPath $authorized) } else { @() }
    if ($existing -notcontains $key) { Add-Content -LiteralPath $authorized -Value $key -Encoding ascii }
    Protect $authorized

    New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
    New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Value "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -PropertyType String -Force | Out-Null

    $remote = '__REMOTE_ADDRESS__'
    if ($remote -eq 'gateway') {
        # Hyper-V: the host reaches the guest from its own address on the switch, which is the guest's gateway.
        $remote = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop
    }
    Get-NetFirewallRule -Name 'Aviary-SSH' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -Name 'Aviary-SSH' -DisplayName 'Aviary SSH from host' -Direction Inbound -Protocol TCP -LocalPort 22 -RemoteAddress $remote -Action Allow | Out-Null
    # The capability's own rule allows SSH from anywhere; keep it off.
    Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Disable-NetFirewallRule

    & "$env:windir\System32\OpenSSH\sshd.exe" -t
    if ($LASTEXITCODE -ne 0) { throw 'sshd rejected its configuration.' }
    Restart-Service sshd
    Send-Report 'ok' $user
    Write-Host "Aviary SSH is ready for $user. Remove the Aviary-SSH firewall rule and this PC's key from $authorized to revoke it."
}
catch {
    Send-Report 'error' 'setup-failed'
    Write-Host ('Aviary SSH setup failed: ' + $_.Exception.Message) -ForegroundColor Red
}
finally {
    if ($PSCommandPath -and (Test-Path -LiteralPath $PSCommandPath)) { Remove-Item -LiteralPath $PSCommandPath -Force }
}
