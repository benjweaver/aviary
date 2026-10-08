using Aviary.Core;

namespace Aviary.Infrastructure;

public static class AppPaths
{
    public static bool Portable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
    public static string DataDirectory => Portable
        ? Path.Combine(AppContext.BaseDirectory, "Data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Branding.Name);
}
