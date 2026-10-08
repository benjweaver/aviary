# Run in an elevated Windows PowerShell inside the guest.
$ErrorActionPreference='Stop'
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this script as administrator inside the guest.'}
$agentRoot=Join-Path $env:ProgramData 'Aviary\Agent'
$marker=Join-Path $agentRoot 'managed.txt'
$existing=Get-LocalUser -Name 'aviary-agent' -ErrorAction SilentlyContinue
if($existing -and !(Test-Path -LiteralPath $marker)){throw 'aviary-agent already exists and is not managed by Aviary. No account changes made.'}
$hadServer=$null -ne (Get-Service sshd -ErrorAction SilentlyContinue)
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null
New-Item -ItemType Directory -Force -Path $agentRoot | Out-Null
if(!$existing){
 $bytes=New-Object byte[] 32; $rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($bytes);$rng.Dispose()
 $password=ConvertTo-SecureString ([Convert]::ToBase64String($bytes)+'aA1!') -AsPlainText -Force
 $user=New-LocalUser -Name 'aviary-agent' -Password $password -Description 'Aviary SSH standard user' -PasswordNeverExpires
 $users=Get-LocalGroup -SID 'S-1-5-32-545';Add-LocalGroupMember -Group $users -Member $user
 Set-Content -LiteralPath $marker -Value 'Aviary SSH account'
}
$user=Get-LocalUser -Name 'aviary-agent'
& icacls.exe $agentRoot /inheritance:r /grant:r ('*'+$user.SID.Value+':(OI)(CI)RX') '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Could not protect SSH key folder.'}
$keyFile=Join-Path $agentRoot 'authorized_keys'
Set-Content -LiteralPath $keyFile -Value '__PUBLIC_KEY__' -Encoding ascii
$config=Join-Path $env:ProgramData 'ssh\sshd_config'
if(!(Test-Path -LiteralPath $config)){Copy-Item -LiteralPath "$env:windir\System32\OpenSSH\sshd_config_default" -Destination $config}
if(!$hadServer){
 $original=Get-Content -LiteralPath $config -Raw
 Set-Content -LiteralPath $config -Value ("AllowUsers aviary-agent`r`nPasswordAuthentication no`r`n"+$original)
 Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Disable-NetFirewallRule | Out-Null
}
if(!(Select-String -LiteralPath $config -SimpleMatch '# Aviary agent access' -Quiet)){
 Copy-Item -LiteralPath $config -Destination ($config+'.before-aviary')
 Add-Content -LiteralPath $config -Value "`r`n# Aviary agent access`r`nMatch User aviary-agent`r`n    AuthorizedKeysFile __PROGRAMDATA__/Aviary/Agent/authorized_keys`r`n    AuthenticationMethods publickey`r`n    PasswordAuthentication no"
}
& "$env:windir\System32\OpenSSH\sshd.exe" -t
if($LASTEXITCODE -ne 0){Copy-Item -LiteralPath ($config+'.before-aviary') -Destination $config -Force; throw 'OpenSSH configuration validation failed; previous configuration restored.'}
if(!(Get-NetFirewallRule -Name 'Aviary-SSH' -ErrorAction SilentlyContinue)){New-NetFirewallRule -Name 'Aviary-SSH' -DisplayName 'Aviary SSH guest access' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow -RemoteAddress LocalSubnet | Out-Null}
Set-Service sshd -StartupType Automatic;Restart-Service sshd
echo 'SSH configured for aviary-agent (standard user, public key only).'
