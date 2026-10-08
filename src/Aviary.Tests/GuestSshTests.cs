using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Aviary.Core;
using Aviary.HyperV;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Xunit;
namespace Aviary.Tests;

public sealed class GuestSshTests
{
    static int FreePort() => GuestProvisioning.AvailablePort();
    static (int Agent, int Client) Ports() { int a = FreePort(), c; do c = FreePort(); while (c == a); return (a, c); }

    static async Task<string> HttpGetAsync(int port, string path)
    {
        using var tcp = new TcpClient(); await tcp.ConnectAsync("127.0.0.1", port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 10.0.2.2\r\nUser-Agent: curl\r\n\r\n"));
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task SetupIsServedOnceAndReportsTheGuestUser()
    {
        var (agent, client) = Ports();
        await using var broker = new GuestSshBroker(agent, client);
        var user = new TaskCompletionSource<string>();
        var token = broker.OfferSetup(t => "script for " + t, u => user.TrySetResult(u), e => user.TrySetException(new Exception(e)));
        Assert.Equal($"http://10.0.2.2:{agent}/s/{token}", broker.SetupUrl(token));

        var served = await HttpGetAsync(agent, $"/s/{token}");
        Assert.StartsWith("HTTP/1.1 200", served); Assert.EndsWith("script for " + token, served);
        Assert.StartsWith("HTTP/1.1 404", await HttpGetAsync(agent, $"/s/{new string('0', 24)}"));

        Assert.StartsWith("HTTP/1.1 200", await HttpGetAsync(agent, $"/done/{token}/ok/Ben%20Weaver"));
        Assert.Equal("Ben Weaver", await user.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        // One-time: the script and the report are gone after setup finished.
        Assert.StartsWith("HTTP/1.1 404", await HttpGetAsync(agent, $"/s/{token}"));
        Assert.StartsWith("HTTP/1.1 404", await HttpGetAsync(agent, $"/done/{token}/ok/someone"));
    }

    [Fact]
    public async Task FailureReportsReachTheCaller()
    {
        var (agent, client) = Ports();
        await using var broker = new GuestSshBroker(agent, client);
        var error = new TaskCompletionSource<string>();
        var token = broker.OfferSetup(_ => "", _ => { }, e => error.TrySetResult(e));
        await HttpGetAsync(agent, $"/done/{token}/error/no-sshd");
        Assert.Equal("no-sshd", await error.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ClientIsPairedWithAnIdleAgentAndBytesFlowBothWays()
    {
        var (agentPort, clientPort) = Ports();
        await using var broker = new GuestSshBroker(agentPort, clientPort);
        using var agent = new TcpClient(); await agent.ConnectAsync("127.0.0.1", agentPort);
        var guest = agent.GetStream();
        await guest.WriteAsync(Encoding.ASCII.GetBytes(GuestSshBroker.AgentHello + "\n"));
        for (int i = 0; i < 50 && broker.IdleAgents == 0; i++) await Task.Delay(20);
        Assert.Equal(1, broker.IdleAgents);

        using var ssh = new TcpClient(); await ssh.ConnectAsync("127.0.0.1", clientPort);
        var host = ssh.GetStream();
        var go = new byte[1]; await guest.ReadExactlyAsync(go).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, go[0]); // tells the agent to start sshd on this connection

        await guest.WriteAsync("SSH-2.0-guest\r\n"u8.ToArray());
        var banner = new byte[15]; await host.ReadExactlyAsync(banner).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("SSH-2.0-guest\r\n", Encoding.ASCII.GetString(banner));
        await host.WriteAsync("SSH-2.0-client\r\n"u8.ToArray());
        var reply = new byte[16]; await guest.ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("SSH-2.0-client\r\n", Encoding.ASCII.GetString(reply));
    }

    [Fact]
    public async Task ClientWithoutAnAgentIsClosedRatherThanHanging()
    {
        var (agentPort, clientPort) = Ports();
        await using var broker = new GuestSshBroker(agentPort, clientPort);
        using var ssh = new TcpClient(); await ssh.ConnectAsync("127.0.0.1", clientPort);
        var read = await ssh.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task SetupOnlyBrokerLeavesTheSshPortToQemu()
    {
        await using var broker = new GuestSshBroker(FreePort(), 0);
        Assert.Equal(0, broker.ClientPort);
    }

    static GuestSsh.Keys SampleKeys => new("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIClient", "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----\n", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHost");

    [Fact]
    public void LinuxSetupScriptIsFilledInAndPosix()
    {
        var script = GuestSsh.LinuxSetupScript(43210, "0123456789abcdef01234567", SampleKeys);
        Assert.DoesNotContain("__", script); Assert.DoesNotContain("\r", script);
        Assert.Contains("port=43210", script); Assert.Contains("/done/0123456789abcdef01234567", script);
        Assert.Contains(SampleKeys.AuthorizedKey, script);
        var hostKey = System.Text.RegularExpressions.Regex.Match(script, "printf '%s' '([A-Za-z0-9+/=]+)' \\| base64 -d").Groups[1].Value;
        Assert.Equal(SampleKeys.HostPrivateKey, Encoding.ASCII.GetString(Convert.FromBase64String(hostKey)));
        Assert.Contains("PasswordAuthentication=no", script); Assert.DoesNotContain("sudo", script);
    }

    [Theory]
    [InlineData("10.0.2.2", "http://10.0.2.2:5000/done/abc")]
    [InlineData("gateway", "kvp:abc")]
    public async Task WindowsSetupScriptParsesAndIsFilledIn(string remote, string report)
    {
        var script = GuestSsh.WindowsSetupScript(SampleKeys, remote, report);
        Assert.DoesNotContain("__", script);
        Assert.Contains($"'{report}'", script); Assert.Contains($"'{remote}'", script); Assert.Contains(SampleKeys.HostPublicKey, script);
        await new HyperVCommands().RunAsync("$t=$null;$e=$null;[System.Management.Automation.Language.Parser]::ParseInput($p.Script,[ref]$t,[ref]$e)|Out-Null;if($e.Count){throw ($e|Out-String)}", new { Script = script });
    }

    [Fact]
    public async Task KeysAndConfigPinTheGuestHostKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "aviary-ssh-test-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var vm = new VmConfiguration { SshEnabled = true, SshPort = 40000, SshAgentPort = 40001, SshUser = "Ben Weaver" };
            var keys = await GuestSsh.EnsureKeysAsync(vm, root);
            Assert.StartsWith("ssh-ed25519 ", keys.AuthorizedKey); Assert.StartsWith("ssh-ed25519 ", keys.HostPublicKey);
            Assert.Contains("OPENSSH PRIVATE KEY", keys.HostPrivateKey);
            Assert.Equal(keys, await GuestSsh.EnsureKeysAsync(vm, root)); // stable across calls
            Assert.Equal($"{GuestSsh.HostAlias(vm)} {keys.HostPublicKey}\n", await File.ReadAllTextAsync(GuestSsh.KnownHostsPath(root)));

            await GuestSsh.WriteConfigAsync(vm, root, "127.0.0.1", 40000);
            var config = await File.ReadAllTextAsync(GuestProvisioning.ConfigPath(root));
            Assert.Contains("Port 40000", config); Assert.Contains("User \"Ben Weaver\"", config);
            Assert.Contains("StrictHostKeyChecking yes", config); Assert.Contains("HostKeyAlias " + GuestSsh.HostAlias(vm), config);
            Assert.Contains("PasswordAuthentication no", config);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("Linux", false)]
    [InlineData("Windows", true)]
    public void OnlyWindowsQemuGuestsGetAnInboundPortForward(string os, bool forwarded)
    {
        var vm = new VmConfiguration { OperatingSystem = os, DiskPath = @"C:\VM\disk.qcow2", SshEnabled = true, SshPort = 40000, SshAgentPort = 40001 };
        var nic = QemuCommandBuilder.Build(vm, new(@"C:\QEMU", "test", [GuestArchitecture.X86_64]), new("X64", 8, 16384, false, "Windows"), TestEndpoints.Fake).ArgumentList.Single(a => a.StartsWith("user,", StringComparison.Ordinal));
        Assert.Equal(forwarded, nic.Contains("hostfwd=tcp:127.0.0.1:40000-:22"));
    }

    [Fact]
    public void QemuKeyboardTypesShiftedCharactersAndChords()
    {
        var keys = QemuKeyboard.Text("a$B|\n").ToList();
        Assert.Equal(["a"], keys[0]); Assert.Equal(["shift", "4"], keys[1]); Assert.Equal(["shift", "b"], keys[2]);
        Assert.Equal(["shift", "backslash"], keys[3]); Assert.Equal(["ret"], keys[4]);
        Assert.Equal(["ctrl", "alt", "t"], QemuKeyboard.Chord("Ctrl+Alt+T"));
        Assert.Equal(["meta_l", "r"], QemuKeyboard.Chord("win+r"));
        Assert.Equal(["f5"], QemuKeyboard.Chord("F5"));
        Assert.Throws<ArgumentException>(() => QemuKeyboard.Text("é").ToList());
        Assert.Throws<ArgumentException>(() => QemuKeyboard.Chord("hyper+x"));
    }

    [Fact]
    public void HyperVKeyboardSplitsLinesIntoEnterKeys()
    {
        var steps = HyperVKeyboard.Text("whoami\nexit").ToList();
        Assert.Equal("whoami", steps[0].Text); Assert.Equal([0x0D], steps[1].Keys!); Assert.Equal("exit", steps[2].Text);
        Assert.Equal([0x11, 0x12, 0x2E], HyperVKeyboard.Chord("ctrl+alt+delete").Keys!);
        Assert.Equal([0x5B, (int)'X'], HyperVKeyboard.Chord("win+x").Keys!);
    }

    [Fact]
    public void ScreenshotPngHasValidHeaderAndChecksums()
    {
        var pixels = new byte[4 * 3 * 2]; BinaryPrimitives.WriteUInt16LittleEndian(pixels, 0xF800); // first pixel red
        var png = Png.FromRgb565(pixels, 4, 3);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16))); Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        // Signature (8) + IHDR chunk (25), then IDAT: length, type, data, CRC; IEND is the last 12 bytes.
        int idatLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
        Assert.Equal("IDAT", Encoding.ASCII.GetString(png, 37, 4)); Assert.Equal(png.Length, 41 + idatLength + 4 + 12);
        Assert.Equal(0xAE426082u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(png.Length - 4))); // IEND's fixed CRC
        using var decoder = new System.IO.Compression.ZLibStream(new MemoryStream(png, 41, idatLength), System.IO.Compression.CompressionMode.Decompress);
        var raw = new MemoryStream(); decoder.CopyTo(raw);
        Assert.Equal(3 * (1 + 4 * 3), raw.Length);
        Assert.Equal(new byte[] { 0, 255, 0, 0 }, raw.ToArray()[..4]);
    }
}
