using System.Text.Json;
using System.Xml.Linq;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qemu;
using Aviary.HyperV;
using Xunit;
namespace Aviary.Tests;

public sealed class GuestProvisioningTests
{
    [Fact]
    public void WindowsAnswersCreateLocalAccountWithoutDiskOrActivationChanges()
    {
        const string password = "Some<&strongPassword123!";
        var doc = XDocument.Parse(GuestProvisioning.WindowsAnswerFile("aviary", password));
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        Assert.Equal(password, doc.Descendants(ns + "Value").Single().Value);
        Assert.Equal("true", doc.Descendants(ns + "HideOnlineAccountScreens").Single().Value);
        Assert.Equal("aviary", doc.Descendants(ns + "LocalAccount").Single().Element(ns + "Name")!.Value);
        Assert.Empty(doc.Descendants(ns + "DiskConfiguration")); Assert.Empty(doc.Descendants(ns + "ProductKey"));
        var json = JsonSerializer.Serialize(new VmConfiguration { SetupPassword = password }); Assert.DoesNotContain(password, json); Assert.DoesNotContain("SetupPassword", json);
    }
    [Theory]
    [InlineData("bad user", "long-enough-password")]
    [InlineData("user", "short")]
    public void InvalidLocalAccountIsRejected(string user, string password) => Assert.Throws<InvalidDataException>(() => GuestProvisioning.WindowsAnswerFile(user, password));
    [Fact]
    public void ForwardIsLoopbackOnlyAndGuestPortIsFixed()
    {
        var vm = new VmConfiguration { DiskPath = @"C:\VM\disk.qcow2", SshEnabled = true, SshPort = 22222, SetupIsoPath = @"C:\VM\setup.iso" };
        var args = QemuCommandBuilder.Build(vm, new(@"C:\QEMU", "test", [GuestArchitecture.X86_64]), new("X64", 8, 16384, false, "Windows"), 4444, 5900).ArgumentList;
        Assert.Contains("user,model=e1000,hostfwd=tcp:127.0.0.1:22222-:22", args); Assert.Contains("ide-cd,drive=setup,bus=ide.1,unit=0", args);
        Assert.Throws<InvalidDataException>(() => (vm with { NetworkEnabled = false }).Validate());
    }
    [Fact]
    public void RecommendationsDoNotClaimWindowsGpuAcceleration()
    {
        var text = GuestRecommendations.For("Windows", VmEngine.HyperV, true, true, true);
        Assert.Contains("Recommended: Hyper-V", text); Assert.Contains("3D acceleration is not automatic", text);
    }
    [Fact]
    public async Task GeneratedWindowsSshScriptParsesAndCannotInjectPublicKey()
    {
        var script = GuestProvisioning.SetupScript(true, "ssh-ed25519 AAAABBBB test");
        await new HyperVCommands().RunAsync("$t=$null;$e=$null;[System.Management.Automation.Language.Parser]::ParseInput($p.Script,[ref]$t,[ref]$e)|Out-Null;if($e.Count){throw ($e|Out-String)}", new { Script = script });
        Assert.Contains("AuthenticationMethods publickey", script);
        Assert.DoesNotContain("\r", GuestProvisioning.SetupScript(false, "ssh-ed25519 AAAABBBB test"));
        Assert.Throws<InvalidDataException>(() => GuestProvisioning.SetupScript(true, "ssh-ed25519 ABC';evil"));
    }
    [Fact]
    public async Task RealWindowsMediaAndSshProfileContainPublicKeyButNeverPrivateKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "aviary-provision-test-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var vm = await GuestProvisioning.PrepareAsync(new() { OperatingSystem = "Windows", LocalWindowsAccount = true, SetupPassword = "Test-only-password123!", SshEnabled = true, SshPort = 22222 }, root);
            Assert.True(File.Exists(vm.SetupIsoPath)); Assert.Equal("", vm.SetupPassword);
            var bytes = await File.ReadAllBytesAsync(vm.SetupIsoPath);
            Assert.Contains("CD001", System.Text.Encoding.ASCII.GetString(bytes));
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.Contains("HideOnlineAccountScreens", text); Assert.Contains("ssh-ed25519", text); Assert.DoesNotContain("OPENSSH PRIVATE KEY", text);
            var config = await File.ReadAllTextAsync(GuestProvisioning.ConfigPath(root));
            Assert.Contains("HostName 127.0.0.1", config); Assert.Contains("StrictHostKeyChecking ask", config); Assert.DoesNotContain("StrictHostKeyChecking no", config);
            Assert.True(File.Exists(Path.Combine(root, "access", "id_ed25519")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(root, "access"), "media-*"));
        }
        finally { Directory.Delete(root, true); }
    }
}
