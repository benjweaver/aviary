using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Aviary.HyperV;

public interface IHyperVCommands
{
    Task<string> RunAsync(string script, object arguments, CancellationToken token = default);
    // The secret reaches the script on stdin as $secret, never on a command line other processes can read.
    Task<string> RunWithSecretAsync(string script, object arguments, string secret, CancellationToken token = default) => throw new NotSupportedException();
}

public sealed class HyperVCommands : IHyperVCommands
{
    public static ProcessStartInfo Build(string script, object arguments, bool readSecret = false)
    {
        var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(arguments));
        var wrapped = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "$p=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json; " + (readSecret ? "$secret=[Console]::In.ReadLine(); " : "") + "try { " + script +
            " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = readSecret, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)) }) info.ArgumentList.Add(arg);
        return info;
    }
    public Task<string> RunAsync(string script, object arguments, CancellationToken token = default) => RunCoreAsync(script, arguments, null, token);
    public Task<string> RunWithSecretAsync(string script, object arguments, string secret, CancellationToken token = default) => RunCoreAsync(script, arguments, secret, token);
    async Task<string> RunCoreAsync(string script, object arguments, string? secret, CancellationToken token)
    {
        using var process = Process.Start(Build(script, arguments, secret is not null)) ?? throw new IOException("Windows PowerShell could not start.");
        if (secret is not null) { await process.StandardInput.WriteLineAsync(secret); process.StandardInput.Close(); }
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var stderr = await error; var stdout = await output;
        if (process.ExitCode != 0) throw new IOException("Hyper-V: " + stderr.Trim());
        return stdout.Trim();
    }
}

public static class HyperVScripts
{
    public const string Probe = """
        if (!(Get-Module -ListAvailable Hyper-V)) { throw 'Enable Hyper-V Platform and Hyper-V Management Tools in Windows Features, then restart. Requires a supported Windows edition.' }
        Import-Module Hyper-V
        if ((Get-Service vmms).Status -ne 'Running') { throw 'The Hyper-V Virtual Machine Management service is not running.' }
        Get-VMHost | Out-Null
        if (!(Test-Path -LiteralPath "$env:windir\System32\vmconnect.exe")) { throw 'Install Hyper-V Management Tools to open the guest console.' }
        [pscustomobject]@{ Switches = @(Get-VMSwitch | ForEach-Object { $_.Name }) } | ConvertTo-Json -Compress
        """;
    public const string Require = """
        Import-Module Hyper-V
        $vm = Get-VM -Id ([guid]$p.HyperVId)
        if ($vm.Notes -ne ('Aviary:' + $p.Id)) { throw 'The Hyper-V machine does not match this Aviary library entry. No changes were made.' }
        """;
    public const string Create = """
        Import-Module Hyper-V
        if ($p.NetworkEnabled) { Get-VMSwitch -Name $p.Switch | Out-Null }
        $vm = $null
        try {
            $vm = New-VM -Name $p.Name -Generation 2 -MemoryStartupBytes ([long]$p.MemoryMB * 1MB) -NewVHDPath $p.DiskPath -NewVHDSizeBytes ([long]$p.DiskGB * 1GB) -Path $p.Directory
            Set-VM -VM $vm -Notes ('Aviary:' + $p.Id) -AutomaticStartAction Nothing -AutomaticStopAction Save -AutomaticCheckpointsEnabled $false
            Set-VMProcessor -VM $vm -Count $p.CpuCores
            Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
            if ($p.NetworkEnabled) { Get-VMNetworkAdapter -VM $vm | Connect-VMNetworkAdapter -SwitchName $p.Switch }
            if ($p.Windows) {
                Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
                Set-VMKeyProtector -VM $vm -NewLocalKeyProtector
                Enable-VMTPM -VM $vm
            } else { Set-VMFirmware -VM $vm -EnableSecureBoot Off }
            $dvd = Add-VMDvdDrive -VM $vm -Path $p.IsoPath -Passthru
            if ($p.SetupIsoPath) {
                & icacls.exe (Split-Path -Parent $p.SetupIsoPath) /grant ('NT VIRTUAL MACHINE\'+$vm.Id+':RX') | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not grant this VM access to setup media.' }
                & icacls.exe $p.SetupIsoPath /grant ('NT VIRTUAL MACHINE\'+$vm.Id+':R') | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not grant this VM read access to setup media.' }
                Add-VMDvdDrive -VM $vm -ControllerNumber 0 -ControllerLocation 2 -Path $p.SetupIsoPath | Out-Null }
            Set-VMFirmware -VM $vm -FirstBootDevice $dvd
            [pscustomobject]@{ Id = $vm.Id.ToString() } | ConvertTo-Json -Compress
        } catch {
            if ($null -ne $vm) { Remove-VM -VM $vm -Force -ErrorAction SilentlyContinue }
            throw
        }
        """;
    public const string Start = Require + """

        if ($vm.State -eq 'Off') {
            $attached = @(Get-VMHardDiskDrive -VM $vm)
            if ($attached.Count -ne 1 -or $attached[0].Path -ne $p.DiskPath) { throw 'The registered Hyper-V disk differs from this library entry. Review it in Hyper-V Manager.' }
            Rename-VM -VM $vm -NewName $p.Name
            $setup = Get-VMDvdDrive -VM $vm | Where-Object { $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq 2 }
            if ($p.SetupIsoPath -and $setup.Path -and $setup.Path -ne $p.ManagedSetupIsoPath) { throw 'The setup slot contains other media. Review it in Hyper-V Manager.' }
            if ($p.SetupIsoPath) {
                & icacls.exe (Split-Path -Parent $p.SetupIsoPath) /grant ('NT VIRTUAL MACHINE\'+$vm.Id+':RX') | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not grant this VM access to setup media.' }
                & icacls.exe $p.SetupIsoPath /grant ('NT VIRTUAL MACHINE\'+$vm.Id+':R') | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Could not grant this VM read access to setup media.' }
                if ($setup) { $setup | Set-VMDvdDrive -Path $p.SetupIsoPath } else { Add-VMDvdDrive -VM $vm -ControllerNumber 0 -ControllerLocation 2 -Path $p.SetupIsoPath | Out-Null }
            } elseif ($setup -and $setup.Path -eq $p.ManagedSetupIsoPath) { $setup | Set-VMDvdDrive -Path $null }
            $dvd = Get-VMDvdDrive -VM $vm | Where-Object { $_.ControllerLocation -ne 2 } | Select-Object -First 1
            if ($null -eq $dvd) { throw 'The Hyper-V DVD drive is missing. Review it in Hyper-V Manager.' }
            if ($p.IsoPath) { $dvd | Set-VMDvdDrive -Path $p.IsoPath } else { $dvd | Set-VMDvdDrive -Path $null }
            if ($p.IsoPath) { Set-VMFirmware -VM $vm -FirstBootDevice $dvd } else { Set-VMFirmware -VM $vm -FirstBootDevice $attached[0] }
        }
        if ($vm.State -eq 'Paused') { Resume-VM -VM $vm } elseif ($vm.State -ne 'Running') { Start-VM -VM $vm }
        """;
    public const string Shutdown = Require + "\nStop-VM -VM $vm -Confirm:$false";
    public const string ForceOff = Require + "\nStop-VM -VM $vm -TurnOff -Confirm:$false";
    public const string Pause = Require + "\nSuspend-VM -VM $vm -Confirm:$false";
    public const string Resume = Require + "\nif ($vm.State -eq 'Saved') { Start-VM -VM $vm } else { Resume-VM -VM $vm -Confirm:$false }";
    public const string Reset = Require + "\nRestart-VM -VM $vm -Force -Confirm:$false";
    public const string RemoveRegistration = Require + "\nRemove-VM -VM $vm -Force";
    public const string Status = """
        Import-Module Hyper-V
        $result = @(foreach ($entry in $p.Machines) {
            try {
                $vm = Get-VM -Id ([guid]$entry.HyperVId)
                if ($vm.Notes -ne ('Aviary:' + $entry.Id)) { throw 'Hyper-V ownership marker does not match.' }
                [pscustomobject]@{ Id=$entry.Id; State=$vm.State.ToString(); Error=$null }
            } catch { [pscustomobject]@{ Id=$entry.Id; State='Error'; Error=$_.Exception.Message } }
        })
        ConvertTo-Json -InputObject $result -Compress
        """;
    // Guest automation. The WMI objects for this VM: Msvm_ComputerSystem is keyed by the VM's GUID.
    const string Wmi = Require + """

        $cs = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter ("Name='" + $vm.Id + "'")
        """;
    // Pushes a host file into the guest over VMBus; needs the Guest Service Interface integration service.
    public const string CopyIntoGuest = Require + """

        $gsi = Get-VMIntegrationService -VM $vm | Where-Object { $_.Id -like '*6C09BB55-D683-4DA0-8931-C9BF705F6480' }
        if ($gsi -and !$gsi.Enabled) { Enable-VMIntegrationService -VMIntegrationService $gsi; Start-Sleep -Seconds 3 }
        Copy-VMFile -VM $vm -SourcePath $p.Source -DestinationPath $p.Destination -CreateFullPath -FileSource Host -Force
        """;
    // IPv4 addresses (via the data exchange integration service) and the latest AviarySsh report from the guest.
    public const string GuestReport = Wmi + """

        $ips = @((Get-VMNetworkAdapter -VM $vm).IPAddresses | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' -and $_ -notlike '169.254.*' })
        $report = $null
        $kvp = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_KvpExchangeComponent
        foreach ($item in @($kvp.GuestExchangeItems)) {
            if (!$item) { continue }
            $x = [xml]$item
            $name = ($x.INSTANCE.PROPERTY | Where-Object { $_.NAME -eq 'Name' }).VALUE
            if ($name -eq 'AviarySsh') { $report = ($x.INSTANCE.PROPERTY | Where-Object { $_.NAME -eq 'Data' }).VALUE }
        }
        [pscustomobject]@{ Ips = $ips; Report = $report } | ConvertTo-Json -Compress
        """;
    // Text uses TypeText (ASCII); each chord is a list of Windows virtual-key codes pressed together.
    public const string Keyboard = Wmi + """

        $kb = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_Keyboard
        function Check($r) { if ($r.ReturnValue -ne 0) { throw ('Hyper-V keyboard returned ' + $r.ReturnValue) } }
        foreach ($step in @($p.Steps)) {
            if ($step.Text) { Check (Invoke-CimMethod -InputObject $kb -MethodName TypeText -Arguments @{ asciiText = [string]$step.Text }) }
            else {
                $keys = @($step.Keys)
                foreach ($k in $keys) { Check (Invoke-CimMethod -InputObject $kb -MethodName PressKey -Arguments @{ keyCode = [uint32]$k }) }
                [array]::Reverse($keys)
                foreach ($k in $keys) { Check (Invoke-CimMethod -InputObject $kb -MethodName ReleaseKey -Arguments @{ keyCode = [uint32]$k }) }
            }
            Start-Sleep -Milliseconds 30
        }
        """;
    // Automatic sign-in for a Windows guest, applied over PowerShell Direct with the account's own credentials
    // ($secret is the password, read from stdin). Like Sysinternals Autologon, the password is kept as the LSA
    // secret DefaultPassword rather than in the registry. A wrong password fails at New-PSSession, before any change.
    public const string AutoSignIn = Require + """

        $credential = New-Object System.Management.Automation.PSCredential($p.User, (ConvertTo-SecureString $secret -AsPlainText -Force))
        $session = New-PSSession -VMId $vm.Id -Credential $credential
        try {
            Invoke-Command -Session $session -ArgumentList $p.User, $secret, $p.Enable -ScriptBlock {
                param($user, $password, $enable)
                $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
                if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "$user isn't an administrator in the guest." }
                Add-Type -TypeDefinition @'
        using System; using System.ComponentModel; using System.Runtime.InteropServices;
        public static class AviaryLsaSecret {
            [StructLayout(LayoutKind.Sequential)] struct Text { public ushort Length, MaximumLength; public IntPtr Buffer; }
            [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Length; public IntPtr RootDirectory, ObjectName; public uint Flags; public IntPtr SecurityDescriptor, SecurityQualityOfService; }
            [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref Attributes attributes, uint access, out IntPtr policy);
            [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr policy, ref Text key, IntPtr data);
            [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr policy);
            [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);
            static Text Make(string s) { return new Text { Buffer = Marshal.StringToHGlobalUni(s), Length = (ushort)(s.Length * 2), MaximumLength = (ushort)(s.Length * 2 + 2) }; }
            public static void Store(string name, string value) {
                var attributes = new Attributes(); IntPtr policy;
                uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x000F0FFF, out policy);
                if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
                var key = Make(name); var data = value == null ? (Text?)null : Make(value); IntPtr pointer = IntPtr.Zero;
                try {
                    if (data.HasValue) { pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Text))); Marshal.StructureToPtr(data.Value, pointer, false); }
                    status = LsaStorePrivateData(policy, ref key, pointer);
                    if (status != 0 && !(value == null && status == 0xC0000034)) throw new Win32Exception(LsaNtStatusToWinError(status));
                } finally {
                    Marshal.FreeHGlobal(key.Buffer); if (data.HasValue) Marshal.FreeHGlobal(data.Value.Buffer); if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer); LsaClose(policy);
                }
            }
        }
        '@
                $winlogon = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
                Remove-ItemProperty -Path $winlogon -Name DefaultPassword, AutoLogonCount -ErrorAction SilentlyContinue
                if ($enable) {
                    [AviaryLsaSecret]::Store('DefaultPassword', $password)
                    Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value '1'
                    Set-ItemProperty -Path $winlogon -Name DefaultUserName -Value $user
                    Set-ItemProperty -Path $winlogon -Name DefaultDomainName -Value $env:COMPUTERNAME
                    # Windows 11 ignores automatic sign-in while "only allow Windows Hello sign-in" is on.
                    $passwordless = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device'
                    if (Test-Path $passwordless) { Set-ItemProperty -Path $passwordless -Name DevicePasswordLessBuildVersion -Value 0 }
                } else {
                    [AviaryLsaSecret]::Store('DefaultPassword', $null)
                    Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value '0'
                }
            }
        } finally { Remove-PSSession $session }
        """;
    // Raw RGB565 frame of the guest display, base64.
    public const string Thumbnail = Wmi + """

        $service = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_VirtualSystemManagementService
        $settings = Get-CimAssociatedInstance -InputObject $cs -ResultClassName Msvm_VirtualSystemSettingData | Where-Object { $_.VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized' }
        $r = Invoke-CimMethod -InputObject $service -MethodName GetVirtualSystemThumbnailImage -Arguments @{ TargetSystem = $settings; WidthPixels = [uint16]$p.Width; HeightPixels = [uint16]$p.Height }
        if ($r.ReturnValue -ne 0 -or !$r.ImageData) { throw 'Hyper-V did not return a screen image. Is the machine running?' }
        [Convert]::ToBase64String([byte[]]$r.ImageData)
        """;
}
