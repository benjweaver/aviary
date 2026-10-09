namespace Aviary.Infrastructure;

// Microsoft's OpenSSH for Windows (PowerShell/Win32-OpenSSH) MSI, pushed into Windows guests during SSH setup.
// Installing it takes seconds and needs no guest network, unlike the Windows Update capability.
public static class OpenSshInstaller
{
    public const string Version = "10.0.0.0";
    public const string FileName = "OpenSSH-Win64-v10.0.0.0.msi";
    // Digest published by GitHub for the release asset.
    const string Sha256 = "ddec9c53864280759cf9f74791cefd387100e3946aa849a1c138a4ed1b96b7d9";
    static readonly Uri Source = new("https://github.com/PowerShell/Win32-OpenSSH/releases/download/10.0.0.0p2-Preview/" + FileName);
    // Where Hyper-V setup copies it inside the guest; the setup script deletes it afterwards.
    public const string GuestPath = @"C:\ProgramData\Aviary\" + FileName;

    public static string CachedPath => Path.Combine(AppPaths.DataDirectory, "Downloads", FileName);

    public static async Task<string> EnsureAsync(CancellationToken token = default)
    {
        if (File.Exists(CachedPath)) return CachedPath;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await VirtioDrivers.DownloadVerifiedAsync(http, Source, Sha256, CachedPath, null, token);
        return CachedPath;
    }
}
