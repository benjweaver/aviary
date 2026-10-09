using System.Xml.Linq;
using Aviary.HyperV;
using Aviary.Infrastructure;
using Xunit;
namespace Aviary.Tests;

public sealed class AutoSignInTests
{
    static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void AnswerFileSignsInAutomaticallyAndKeepsItOn(bool drivers, int commands)
    {
        var doc = XDocument.Parse(GuestProvisioning.WindowsAnswerFile("aviary", "long-enough-password", drivers, autoSignIn: true));
        var autoLogon = doc.Descendants(Ns + "AutoLogon").Single();
        Assert.Equal("true", autoLogon.Element(Ns + "Enabled")!.Value);
        Assert.Equal("aviary", autoLogon.Element(Ns + "Username")!.Value);
        Assert.Equal("long-enough-password", autoLogon.Element(Ns + "Password")!.Element(Ns + "Value")!.Value);
        var lines = doc.Descendants(Ns + "SynchronousCommand").ToList();
        Assert.Equal(commands, lines.Count);
        Assert.Equal(Enumerable.Range(1, commands).Select(i => i.ToString()), lines.Select(l => l.Element(Ns + "Order")!.Value));
        Assert.Contains("AutoLogonCount", lines[0].Element(Ns + "CommandLine")!.Value);
        // Shell-Setup settings are alphabetical: AutoLogon, FirstLogonCommands, OOBE, UserAccounts.
        var order = doc.Descendants(Ns + "component").Single().Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(order.OrderBy(n => n, StringComparer.Ordinal), order);
    }

    [Fact]
    public void QemuWindowsSetupSkipsTpmAndSecureBootChecks()
    {
        var doc = XDocument.Parse(GuestProvisioning.WindowsAnswerFile(null, null, bypassHardwareChecks: true));
        var pe = doc.Descendants(Ns + "settings").Single(s => (string?)s.Attribute("pass") == "windowsPE");
        var paths = pe.Descendants(Ns + "Path").Select(p => p.Value).ToList();
        Assert.Contains(@"reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f", paths);
        Assert.Contains(paths, p => p.Contains("BypassSecureBootCheck"));
        Assert.Equal(Enumerable.Range(1, paths.Count).Select(i => i.ToString()), pe.Descendants(Ns + "Order").Select(o => o.Value));
        Assert.Equal("Microsoft-Windows-Setup", (string?)pe.Element(Ns + "component")!.Attribute("name"));
        Assert.DoesNotContain(doc.Descendants(Ns + "settings"), s => (string?)s.Attribute("pass") == "oobeSystem"); // no account asked for
    }

    [Fact]
    public async Task OnlyQemuWindowsMachinesGetTheBypass()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "aviary-bypass-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var qemu = await GuestProvisioning.PrepareAsync(new() { OperatingSystem = "Windows" }, root);
            var text = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(qemu.SetupIsoPath));
            Assert.Contains("BypassTPMCheck", text);
            var hyperV = await GuestProvisioning.PrepareAsync(new() { OperatingSystem = "Windows", Engine = Aviary.Core.VmEngine.HyperV, DiskFormat = Aviary.Core.DiskFormat.Vhdx }, Path.Combine(root, "hv"));
            Assert.Equal("", hyperV.SetupIsoPath);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void AnswerFileWithoutAutoSignInHasNoAutoLogon() =>
        Assert.Empty(XDocument.Parse(GuestProvisioning.WindowsAnswerFile("aviary", "long-enough-password")).Descendants(Ns + "AutoLogon"));

    [Theory]
    [InlineData(true, "1")]
    [InlineData(false, "0")]
    public void LinuxSetupCanKeepTheDesktopUnlocked(bool keep, string flag)
    {
        var script = GuestSsh.LinuxSetupScript(4000, new string('a', 24), new("ssh-ed25519 AAAAC3", "key", "ssh-ed25519 AAAAC3h"), keep);
        Assert.Contains($"if [ \"{flag}\" = 1 ]; then", script);
        Assert.Contains("--key Autolock false", script); Assert.Contains("org.gnome.desktop.screensaver lock-enabled false", script);
        Assert.DoesNotContain("__KEEP_UNLOCKED__", script);
    }

    [Fact]
    public async Task HyperVAutoSignInScriptParsesAndTakesThePasswordFromStdin()
    {
        var script = HyperVScripts.AutoSignIn;
        Assert.DoesNotContain("$p.Password", script);
        Assert.Contains("$secret", script);
        await new HyperVCommands().RunAsync("$t=$null;$e=$null;[System.Management.Automation.Language.Parser]::ParseInput($p.Script,[ref]$t,[ref]$e)|Out-Null;if($e.Count){throw ($e|Out-String)}", new { Script = script });
    }

    [Fact]
    public async Task SecretsTravelOnStdinNotTheCommandLine()
    {
        var commands = new HyperVCommands();
        Assert.Equal("s3cret value!", await commands.RunWithSecretAsync("$secret", new { }, "s3cret value!"));
        var start = HyperVCommands.Build("$secret", new { }, readSecret: true);
        Assert.True(start.RedirectStandardInput);
        Assert.DoesNotContain(start.ArgumentList, a => a.Contains("s3cret"));
    }
}
