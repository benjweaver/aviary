using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aviary.Core;

namespace Aviary.Infrastructure;

public static class GuestProvisioning
{
    public static int AvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; } finally { listener.Stop(); }
    }
    public static string AccessDirectory(string machineDirectory) => Path.Combine(machineDirectory, "access");
    public static string ConfigPath(string machineDirectory) => Path.Combine(AccessDirectory(machineDirectory), "ssh_config");
    public static string SshCommand(string machineDirectory) => "ssh -F '" + ConfigPath(machineDirectory).Replace("'", "''") + "' guest";
    // The guest-tools install runs at first sign-in, finding the driver CD by its installer since drive letters vary.
    public static string VirtioToolsCommand => $"cmd /c for %d in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do @if exist %d:\\{VirtioDrivers.GuestToolsInstaller} start /wait msiexec /i %d:\\{VirtioDrivers.GuestToolsInstaller} /qn /norestart";
    public static string WindowsAnswerFile(string username, string password, bool installVirtioTools = false)
    {
        if (!Regex.IsMatch(username, "^[a-zA-Z][a-zA-Z0-9_-]{0,19}$") || password.Length < 12) throw new InvalidDataException("Choose a local username and a password of at least 12 characters.");
        XNamespace ns = "urn:schemas-microsoft-com:unattend", wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        return new XDocument(new XElement(ns + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", wcm),
            new XElement(ns + "settings", new XAttribute("pass", "oobeSystem"),
                new XElement(ns + "component", new XAttribute("name", "Microsoft-Windows-Shell-Setup"), new XAttribute("processorArchitecture", "amd64"), new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"),
                    installVirtioTools ? new XElement(ns + "FirstLogonCommands", new XElement(ns + "SynchronousCommand", new XAttribute(wcm + "action", "add"),
                        new XElement(ns + "Order", 1), new XElement(ns + "CommandLine", VirtioToolsCommand), new XElement(ns + "Description", "Install VirtIO drivers (NetKVM)"))) : null,
                    new XElement(ns + "OOBE", new XElement(ns + "HideOnlineAccountScreens", true), new XElement(ns + "HideWirelessSetupInOOBE", true)),
                    new XElement(ns + "UserAccounts", new XElement(ns + "LocalAccounts", new XElement(ns + "LocalAccount", new XAttribute(wcm + "action", "add"),
                        new XElement(ns + "Name", username), new XElement(ns + "DisplayName", username), new XElement(ns + "Group", "Administrators"),
                        new XElement(ns + "Password", new XElement(ns + "Value", password), new XElement(ns + "PlainText", true))))))))).ToString();
    }
    public static string SetupScript(bool windows, string publicKey)
    {
        var key = publicKey.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (key.Length < 2 || key[0] != "ssh-ed25519" || !Regex.IsMatch(key[1], "^[A-Za-z0-9+/]+={0,2}$")) throw new InvalidDataException("Expected an Ed25519 public key.");
        using var stream = typeof(GuestProvisioning).Assembly.GetManifestResourceStream("Aviary.Infrastructure.GuestSetup.setup-ssh." + (windows ? "ps1" : "sh")) ?? throw new IOException("Guest setup script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n").Replace("__PUBLIC_KEY__", key[0] + " " + key[1], StringComparison.Ordinal);
    }
    public static async Task WriteSshConfigAsync(VmConfiguration vm, string directory)
    {
        string host = vm.Engine == VmEngine.Qemu ? "127.0.0.1" : vm.SshHost;
        if (host.Length == 0) return;
        if (!IPAddress.TryParse(host, out _)) throw new InvalidDataException("Enter a valid guest IP address.");
        string EscapePath(string path) => Path.GetFullPath(path).Replace('\\', '/').Replace("\"", "\\\"");
        var access = AccessDirectory(directory);
        await File.WriteAllTextAsync(ConfigPath(directory), $"Host guest\n    HostName {host}\n    Port {(vm.Engine == VmEngine.Qemu ? vm.SshPort : 22)}\n    User aviary-agent\n    IdentityFile \"{EscapePath(Path.Combine(access, "id_ed25519"))}\"\n    IdentitiesOnly yes\n    PasswordAuthentication no\n    StrictHostKeyChecking ask\n    UserKnownHostsFile \"{EscapePath(Path.Combine(access, "known_hosts"))}\"\n");
    }
    public static async Task<VmConfiguration> PrepareAsync(VmConfiguration vm, string directory, CancellationToken token = default)
    {
        if (!vm.LocalWindowsAccount && !vm.SshEnabled) return vm;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Setup media requires Windows.");
        var access = AccessDirectory(directory); Directory.CreateDirectory(access); await ProtectDirectoryAsync(access, token);
        var staging = Path.Combine(access, "media-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            if (vm.LocalWindowsAccount) await File.WriteAllTextAsync(Path.Combine(staging, "autounattend.xml"), WindowsAnswerFile(vm.WindowsUserName, vm.SetupPassword, vm.DriverIsoPath.Length > 0), token);
            if (vm.SshEnabled)
            {
                var key = Path.Combine(access, "id_ed25519");
                var keygen = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh-keygen.exe");
                if (!File.Exists(key))
                {
                    try { await ProcessRunner.RunAsync(keygen, ["-t", "ed25519", "-N", "", "-C", "aviary-" + vm.Id, "-f", key], token); }
                    // Windows OpenSSH can save the private key but fail to apply public-file metadata.
                    // Recover only that specific case; deriving the public key below validates the private key.
                    catch (IOException ex) when (File.Exists(key) && ex.Message.Contains("Unable to save public key", StringComparison.Ordinal)) { }
                }
                var publicKey = (await ProcessRunner.RunAsync(keygen, ["-y", "-P", "", "-f", key], token)).Trim();
                _ = SetupScript(false, publicKey);
                await File.WriteAllTextAsync(key + ".pub", publicKey + "\n", token);
                await File.WriteAllTextAsync(Path.Combine(staging, "setup-ssh.ps1"), SetupScript(true, publicKey), token);
                await File.WriteAllTextAsync(Path.Combine(staging, "setup-ssh.sh"), SetupScript(false, publicKey), new UTF8Encoding(false), token);
                await WriteSshConfigAsync(vm, directory);
                await File.WriteAllTextAsync(Path.Combine(access, "ACCESS.md"), $"# {vm.Name} guest access\n\nGuest user: aviary-agent (standard user, no automatic administrator/sudo rights).\n\nRun setup-ssh.sh with sudo in Linux, or setup-ssh.ps1 from elevated Windows PowerShell, from the Aviary setup CD once. The scripts install OpenSSH and authorize this VM's public key. Internet access may be needed for guest packages.\n\nThen connect with: {SshCommand(directory)}\n\nFor Hyper-V, enter the guest IP in Aviary's SSH access dialog first. Use Default Switch for private host/guest networking. For QEMU, the forward binds only to 127.0.0.1:{vm.SshPort}.\n\nVerify the guest host-key fingerprint on the first connection. Codex and Claude can use the same command after that. No VM commands run automatically. Keep id_ed25519 private.\n", token);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "README.txt"), "Aviary setup media. Windows local-account answers apply during a fresh Windows installation. SSH requires running the appropriate setup-ssh script inside the installed guest. Eject this CD and remove setup.iso after Windows setup: it contains your setup password if local-account setup was selected.", token);
            var iso = Path.Combine(access, "setup.iso");
            await Task.Run(() => { if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(); CreateIso(staging, iso); }, token);
            return vm with { SetupIsoPath = iso, SetupPassword = "" };
        }
        finally
        {
            // Only this unique staging directory under the machine's access directory.
            if (Path.GetFullPath(staging).StartsWith(Path.GetFullPath(access) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(staging, true);
        }
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static async Task ProtectDirectoryAsync(string directory, CancellationToken token)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Cannot determine the current Windows account.");
        await ProcessRunner.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "icacls.exe"), [directory, "/inheritance:r", "/grant:r", "*" + sid + ":(OI)(CI)F", "*S-1-5-18:(OI)(CI)F"], token);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void CreateIso(string sourceDirectory, string destination)
    {
        var type = Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage") ?? throw new IOException("Windows setup-media creation is unavailable.");
        dynamic image = Activator.CreateInstance(type)!;
        object? root = null, result = null, imageStream = null;
        nint read = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            image.FileSystemsToCreate = 3; image.VolumeName = "AVIARY_SETUP";
            root = image.Root; ((dynamic)root).AddTree(Path.GetFullPath(sourceDirectory), false);
            result = image.CreateResultImage(); imageStream = ((dynamic)result).ImageStream;
            var stream = (IStream)imageStream; using var output = File.Create(destination); var bytes = new byte[64 * 1024];
            while (true) { stream.Read(bytes, bytes.Length, read); int count = Marshal.ReadInt32(read); if (count == 0) break; output.Write(bytes, 0, count); }
        }
        finally
        {
            Marshal.FreeHGlobal(read);
            foreach (var value in new object?[] { imageStream, result, root, image }) if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }
}
