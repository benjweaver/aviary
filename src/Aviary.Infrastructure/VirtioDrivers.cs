using System.Security.Cryptography;

namespace Aviary.Infrastructure;

// The virtio-win driver CD (NetKVM, viostor, balloon, guest tools) for Windows guests on QEMU.
// Downloaded once into Aviary's data folder and shared by every machine that attaches it.
public static class VirtioDrivers
{
    public const string Version = "0.1.302";
    public const long Size = 877_373_440;
    // Upstream publishes no checksum file; this pins the HTTPS download of this exact release.
    const string Sha256 = "303f7ae40dad495d6ae474fdc571df58958a4dbc5c37a522d80f9a203867949d";
    static readonly Uri Source = new($"https://fedorapeople.org/groups/virt/virtio-win/direct-downloads/archive-virtio/virtio-win-{Version}-1/virtio-win-{Version}.iso");
    // Guest-tools installer at the root of the CD; installs NetKVM and the other drivers silently.
    public const string GuestToolsInstaller = "virtio-win-gt-x64.msi";

    public static string CachedPath => Path.Combine(AppPaths.DataDirectory, "Drivers", $"virtio-win-{Version}.iso");
    public static bool IsCached => File.Exists(CachedPath) && new FileInfo(CachedPath).Length == Size;

    public static async Task<string> EnsureAsync(IProgress<double>? progress = null, CancellationToken token = default)
    {
        if (IsCached) return CachedPath;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await DownloadVerifiedAsync(http, Source, Sha256, CachedPath, progress, token);
        return CachedPath;
    }

    // Streams to a temporary file beside the destination and only moves it into place once the hash matches.
    public static async Task DownloadVerifiedAsync(HttpClient http, Uri source, string sha256, string destination, IProgress<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(partial);
                var buffer = new byte[1 << 20]; long received = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    received += read;
                    if (total > 0) progress?.Report((double)received / total.Value);
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The VirtIO driver download did not match its expected checksum. Nothing was saved.");
            }
            File.Move(partial, destination, true);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
