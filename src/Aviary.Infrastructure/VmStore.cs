using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Aviary.Core;
namespace Aviary.Infrastructure;

public sealed class VmStore(string root, string? portableRoot = null)
{
    public string Root { get; } = Path.GetFullPath(root);
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
    public string DirectoryFor(Guid id) => Path.Combine(Root, id.ToString("D"));
    public static VmConfiguration Deserialize(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Empty VM configuration.");
        var version = node["schemaVersion"]?.GetValue<int>() ?? 0;
        if (version == 0) node["schemaVersion"] = 1;
        else if (version != 1) throw new InvalidDataException($"Configuration version {version} is not supported.");
        var vm = node.Deserialize<VmConfiguration>(Json) ?? throw new InvalidDataException("Invalid VM configuration."); vm.Validate(); return vm;
    }
    public async Task SaveAsync(VmConfiguration vm, CancellationToken token = default)
    {
        vm.Validate(); var dir = DirectoryFor(vm.Id); Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "config.json"); var temporary = target + "." + Guid.NewGuid() + ".tmp";
        string StoredPath(string path) => portableRoot is not null && !string.IsNullOrEmpty(path) && Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(portableRoot)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(dir, path) : path;
        var saved = vm with { DiskPath = StoredPath(vm.DiskPath), IsoPath = StoredPath(vm.IsoPath), SetupIsoPath = StoredPath(vm.SetupIsoPath), DriverIsoPath = StoredPath(vm.DriverIsoPath) };
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(saved, Json), token); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<(List<VmConfiguration> Vms, List<string> Errors)> LoadAsync()
    {
        var vms = new List<VmConfiguration>(); var errors = new List<string>();
        if (!Directory.Exists(Root)) return (vms, errors);
        foreach (var file in Directory.EnumerateFiles(Root, "config.json", SearchOption.AllDirectories))
        {
            try
            {
                var vm = Deserialize(await File.ReadAllTextAsync(file)); var directory = DirectoryFor(vm.Id);
                if (Path.GetFullPath(Path.GetDirectoryName(file)!) != directory) throw new InvalidDataException("VM directory and ID do not match.");
                string ResolvedPath(string path) => string.IsNullOrEmpty(path) ? path : Path.GetFullPath(path, directory);
                vms.Add(vm with { DiskPath = ResolvedPath(vm.DiskPath), IsoPath = ResolvedPath(vm.IsoPath), SetupIsoPath = ResolvedPath(vm.SetupIsoPath), DriverIsoPath = ResolvedPath(vm.DriverIsoPath) });
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException) { errors.Add($"{file}: {ex.Message}"); }
        }
        return (vms, errors);
    }
    public void RemoveFromLibrary(Guid id) { var path = Path.Combine(DirectoryFor(id), "config.json"); File.Move(path, Path.Combine(DirectoryFor(id), "config.removed.json"), true); }
}
