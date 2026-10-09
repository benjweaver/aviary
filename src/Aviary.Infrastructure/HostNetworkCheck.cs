namespace Aviary.Infrastructure;

// Hyper-V guests behind the Default Switch can download extremely slowly (KB/s) when Receive Segment Coalescing
// is on for the host's physical adapter: coalesced segments break on their way through the switch's NAT (seen with
// an Aquantia AQC107). QEMU guests are unaffected: user-mode networking re-originates their traffic on the host.
public static class HostNetworkCheck
{
    const string Script = "Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | Get-NetAdapterRsc -ErrorAction SilentlyContinue | Where-Object { $_.IPv4Enabled -or $_.IPv6Enabled } | ForEach-Object Name";

    // Names of physical adapters that are up with RSC on. Reading this needs no administrator rights.
    public static async Task<IReadOnlyList<string>> AdaptersWithRscAsync(CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await ProcessRunner.RunCapturedAsync(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", Script], TimeSpan.FromSeconds(20), token);
        return result.ExitCode != 0 ? [] : result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string FixCommand(IReadOnlyList<string> adapters) => "Disable-NetAdapterRsc -Name " + string.Join(", ", adapters.Select(a => "\"" + a.Replace("\"", "`\"") + "\""));
}
