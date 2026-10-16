# Run as administrator inside a Hyper-V Windows guest. No keys or credentials are stored here.
$ErrorActionPreference = 'Stop'
$directory = Join-Path $env:ProgramFiles 'AviarySshGateway'
if (Test-Path -LiteralPath $directory) {
    if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a redirected SSH gateway directory.' }
}
New-Item -ItemType Directory -Force -Path $directory | Out-Null
# A SYSTEM task must never execute a script that an ordinary guest user can replace.
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false)
foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
    $identity = New-Object Security.Principal.SecurityIdentifier($sid)
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))
}
$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
Set-Acl -LiteralPath $directory -AclObject $acl
$path = Join-Path $directory 'refresh.ps1'
if (Test-Path -LiteralPath $path) {
    if ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refusing a redirected SSH gateway script.' }
}
$refresh = @'
$ErrorActionPreference = 'Stop'
$rule = Get-NetFirewallRule -Name 'Aviary-SSH' -ErrorAction SilentlyContinue
if (!$rule) { return } # Removing the rule revokes access; never recreate it.
try {
    $route = Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
        Sort-Object @{Expression={ $_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric }} |
        Select-Object -First 1
    $gateway = $null
    if (!$route -or ![Net.IPAddress]::TryParse([string]$route.NextHop, [ref]$gateway) -or
        $gateway.Equals([Net.IPAddress]::Any) -or [Net.IPAddress]::IsLoopback($gateway) -or
        $gateway.GetAddressBytes()[0] -ge 224) {
        $rule | Disable-NetFirewallRule
        return
    }
    $filter = $rule | Get-NetFirewallAddressFilter
    if (@($filter.RemoteAddress).Count -ne 1 -or $filter.RemoteAddress -ne $gateway.ToString()) {
        $rule | Disable-NetFirewallRule
        $filter | Set-NetFirewallAddressFilter -RemoteAddress $gateway.ToString()
    }
    $rule | Enable-NetFirewallRule
} catch {
    $rule | Disable-NetFirewallRule
    throw
}
'@
Set-Content -LiteralPath $path -Value $refresh -Encoding UTF8
$fileAcl = New-Object Security.AccessControl.FileSecurity
$fileAcl.SetAccessRuleProtection($false,$false)
$fileAcl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
Set-Acl -LiteralPath $path -AclObject $fileAcl
$action = New-ScheduledTaskAction -Execute "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $path + '"')
$startup = New-ScheduledTaskTrigger -AtStartup
$periodic = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds 50) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName 'Aviary-SSH-Gateway' -Action $action -Trigger @($startup,$periodic) -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
& ([scriptblock]::Create($refresh))
