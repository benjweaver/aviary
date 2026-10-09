using System.Text;
using Aviary.Core;

namespace Aviary.Infrastructure;

// Keys, guest setup scripts and the ssh_config that terminals and coding agents use: ssh -F <config> guest.
// Aviary generates the guest's host key itself and pins it, so the first connection needs no trust prompt.
public static class GuestSsh
{
    // Command to type into the guest, and the guest account name once setup reports back.
    public sealed record Setup(string Command, Task<string> User);
    public sealed record Keys(string AuthorizedKey, string HostPrivateKey, string HostPublicKey);

    public static string ClientKeyPath(string machineDirectory) => Path.Combine(GuestProvisioning.AccessDirectory(machineDirectory), "id_ed25519");
    public static string HostKeyPath(string machineDirectory) => Path.Combine(GuestProvisioning.AccessDirectory(machineDirectory), "guest_host_ed25519");
    public static string KnownHostsPath(string machineDirectory) => Path.Combine(GuestProvisioning.AccessDirectory(machineDirectory), "known_hosts");
    public static string HostAlias(VmConfiguration vm) => "aviary-" + vm.Id.ToString("N");
    // Where Aviary's SSH setup is written inside Windows guests on Hyper-V (Copy-VMFile target).
    public const string WindowsSetupPath = @"C:\ProgramData\Aviary\ssh-setup.ps1";

    static string KeyGen => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh-keygen.exe");

    static async Task<string> EnsureKeyAsync(string path, string comment, CancellationToken token)
    {
        if (!File.Exists(path))
        {
            try { await ProcessRunner.RunAsync(KeyGen, ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", path], token); }
            // Windows OpenSSH can save the private key but fail to apply public-file metadata.
            // Recover only that case; deriving the public key below validates the private key.
            catch (IOException ex) when (File.Exists(path) && ex.Message.Contains("Unable to save public key", StringComparison.Ordinal)) { }
        }
        var publicKey = string.Join(' ', (await ProcessRunner.RunAsync(KeyGen, ["-y", "-P", "", "-f", path], token)).Trim().Split(' ').Take(2));
        await File.WriteAllTextAsync(path + ".pub", publicKey + "\n", token);
        return publicKey;
    }

    public static async Task<Keys> EnsureKeysAsync(VmConfiguration vm, string machineDirectory, CancellationToken token = default)
    {
        var access = GuestProvisioning.AccessDirectory(machineDirectory);
        Directory.CreateDirectory(access);
        if (OperatingSystem.IsWindows()) await GuestProvisioning.ProtectDirectoryAsync(access, token);
        var client = await EnsureKeyAsync(ClientKeyPath(machineDirectory), "aviary-" + vm.Id, token);
        var hostPublic = await EnsureKeyAsync(HostKeyPath(machineDirectory), "aviary-guest-" + vm.Id, token);
        await File.WriteAllTextAsync(KnownHostsPath(machineDirectory), $"{HostAlias(vm)} {hostPublic}\n", token);
        var hostPrivate = (await File.ReadAllTextAsync(HostKeyPath(machineDirectory), token)).Replace("\r\n", "\n");
        return new(client, hostPrivate, hostPublic);
    }

    static string Template(string name)
    {
        using var stream = typeof(GuestSsh).Assembly.GetManifestResourceStream("Aviary.Infrastructure.GuestSetup." + name) ?? throw new IOException("Guest setup script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // POSIX sh, run as the desktop user via: curl -fsS http://10.0.2.2:<port>/s/<token> | sh
    public static string LinuxSetupScript(int agentPort, string token, Keys keys, bool keepUnlocked = true) => Template("agent-setup.sh").Replace("\r\n", "\n")
        .Replace("__KEEP_UNLOCKED__", keepUnlocked ? "1" : "0", StringComparison.Ordinal)
        .Replace("__PORT__", agentPort.ToString(), StringComparison.Ordinal)
        .Replace("__TOKEN__", token, StringComparison.Ordinal)
        .Replace("__AUTHORIZED_KEY__", keys.AuthorizedKey, StringComparison.Ordinal)
        .Replace("__HOST_KEY_B64__", Convert.ToBase64String(Encoding.ASCII.GetBytes(keys.HostPrivateKey)), StringComparison.Ordinal);

    // Windows PowerShell 5.1, run elevated. reportUrl is "kvp:<token>" on Hyper-V, which reports through KVP instead of HTTP.
    // openSshMsi is the MSI's path in the guest or a URL on Aviary's broker; empty falls back to Windows Update.
    public static string WindowsSetupScript(Keys keys, string firewallRemoteAddress, string reportUrl, string openSshMsi = "") => Template("windows-setup.ps1").Replace("\r\n", "\n").Replace("\n", "\r\n")
        .Replace("__AUTHORIZED_KEY__", keys.AuthorizedKey, StringComparison.Ordinal)
        .Replace("__HOST_KEY_B64__", Convert.ToBase64String(Encoding.ASCII.GetBytes(keys.HostPrivateKey)), StringComparison.Ordinal)
        .Replace("__HOST_PUBLIC_KEY__", keys.HostPublicKey, StringComparison.Ordinal)
        .Replace("__REMOTE_ADDRESS__", firewallRemoteAddress, StringComparison.Ordinal)
        .Replace("__REPORT_URL__", reportUrl, StringComparison.Ordinal)
        .Replace("__OPENSSH_MSI__", openSshMsi, StringComparison.Ordinal);

    public static string SetupInstruction(VmConfiguration vm) => vm.OperatingSystem == "Windows"
        ? "Open PowerShell as administrator in the guest (Win+X, then Terminal (Admin)) and leave it focused."
        : "Open a terminal in the guest and leave it focused. No sudo is needed; OpenSSH server, bash and curl or wget must be installed.";

    public static async Task WriteConfigAsync(VmConfiguration vm, string machineDirectory, string hostName, int port)
    {
        static string Escape(string path) => Path.GetFullPath(path).Replace('\\', '/').Replace("\"", "\\\"");
        var user = vm.SshUser.Length > 0 ? vm.SshUser : "aviary";
        Directory.CreateDirectory(GuestProvisioning.AccessDirectory(machineDirectory));
        await File.WriteAllTextAsync(GuestProvisioning.ConfigPath(machineDirectory),
            $"# {vm.Name}. Generated by Aviary; edits are overwritten.\n" +
            $"Host guest\n    HostName {hostName}\n    Port {port}\n    User \"{user}\"\n" +
            $"    IdentityFile \"{Escape(ClientKeyPath(machineDirectory))}\"\n    IdentitiesOnly yes\n    PasswordAuthentication no\n" +
            $"    HostKeyAlias {HostAlias(vm)}\n    HostKeyAlgorithms ssh-ed25519\n    StrictHostKeyChecking yes\n" +
            $"    UserKnownHostsFile \"{Escape(KnownHostsPath(machineDirectory))}\"\n    ConnectTimeout 20\n    ServerAliveInterval 30\n");
    }

    // Runs a command in the guest over the configured connection; used to confirm that setup worked.
    public static Task<string> RunAsync(string machineDirectory, string command, CancellationToken token = default) =>
        ProcessRunner.RunAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"),
            ["-F", GuestProvisioning.ConfigPath(machineDirectory), "-o", "BatchMode=yes", "guest", command], token);
}
