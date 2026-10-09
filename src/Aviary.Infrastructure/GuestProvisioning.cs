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
    public static string SshCommand(string machineDirectory) => "ssh -F \"" + ConfigPath(machineDirectory) + "\" guest";
    // The guest-tools install runs at first sign-in, finding the driver CD by its installer since drive letters vary.
    public static string VirtioToolsCommand => $"cmd /c for %d in (D E F G H I J K L M N O P Q R S T U V W X Y Z) do @if exist %d:\\{VirtioDrivers.GuestToolsInstaller} start /wait msiexec /i %d:\\{VirtioDrivers.GuestToolsInstaller} /qn /norestart";
    // Keeps automatic sign-in on after the answer file's logon count runs out, and lets Windows 11 honor it.
    public const string KeepAutoSignInCommand = "cmd /c reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v AutoLogonCount /f & reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\PasswordLess\\Device\" /v DevicePasswordLessBuildVersion /t REG_DWORD /d 0 /f";
    public static string WindowsAnswerFile(string username, string password, bool installVirtioTools = false, bool autoSignIn = false)
    {
        if (!Regex.IsMatch(username, "^[a-zA-Z][a-zA-Z0-9_-]{0,19}$") || password.Length < 12) throw new InvalidDataException("Choose a local username and a password of at least 12 characters.");
        XNamespace ns = "urn:schemas-microsoft-com:unattend", wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        var commands = new List<(string Command, string Description)>();
        if (autoSignIn) commands.Add((KeepAutoSignInCommand, "Keep automatic sign-in on"));
        if (installVirtioTools) commands.Add((VirtioToolsCommand, "Install VirtIO drivers (NetKVM)"));
        return new XDocument(new XElement(ns + "unattend", new XAttribute(XNamespace.Xmlns + "wcm", wcm),
            new XElement(ns + "settings", new XAttribute("pass", "oobeSystem"),
                new XElement(ns + "component", new XAttribute("name", "Microsoft-Windows-Shell-Setup"), new XAttribute("processorArchitecture", "amd64"), new XAttribute("publicKeyToken", "31bf3856ad364e35"), new XAttribute("language", "neutral"), new XAttribute("versionScope", "nonSxS"),
                    autoSignIn ? new XElement(ns + "AutoLogon", new XElement(ns + "Password", new XElement(ns + "Value", password), new XElement(ns + "PlainText", true)),
                        new XElement(ns + "Enabled", true), new XElement(ns + "LogonCount", 9999999), new XElement(ns + "Username", username)) : null,
                    commands.Count == 0 ? null : new XElement(ns + "FirstLogonCommands", commands.Select((c, i) => new XElement(ns + "SynchronousCommand", new XAttribute(wcm + "action", "add"),
                        new XElement(ns + "Order", i + 1), new XElement(ns + "CommandLine", c.Command), new XElement(ns + "Description", c.Description)))),
                    new XElement(ns + "OOBE", new XElement(ns + "HideOnlineAccountScreens", true), new XElement(ns + "HideWirelessSetupInOOBE", true)),
                    new XElement(ns + "UserAccounts", new XElement(ns + "LocalAccounts", new XElement(ns + "LocalAccount", new XAttribute(wcm + "action", "add"),
                        new XElement(ns + "Name", username), new XElement(ns + "DisplayName", username), new XElement(ns + "Group", "Administrators"),
                        new XElement(ns + "Password", new XElement(ns + "Value", password), new XElement(ns + "PlainText", true))))))))).ToString();
    }
    // Fresh Windows installs: a setup CD whose autounattend.xml creates a local account. SSH is set up
    // later from the running guest (see GuestSsh), so nothing else goes on the CD.
    public static async Task<VmConfiguration> PrepareAsync(VmConfiguration vm, string directory, CancellationToken token = default)
    {
        if (!vm.LocalWindowsAccount) return vm with { SetupPassword = "" };
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Setup media requires Windows.");
        var access = AccessDirectory(directory); Directory.CreateDirectory(access); await ProtectDirectoryAsync(access, token);
        var staging = Path.Combine(access, "media-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(staging, "autounattend.xml"), WindowsAnswerFile(vm.WindowsUserName, vm.SetupPassword, vm.DriverIsoPath.Length > 0, vm.WindowsAutoSignIn), token);
            await File.WriteAllTextAsync(Path.Combine(staging, "README.txt"), "Aviary setup media for a fresh Windows installation. It contains your local account password: eject it with Eject and remove setup CD after Windows setup.", token);
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
    public static async Task ProtectDirectoryAsync(string directory, CancellationToken token)
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
