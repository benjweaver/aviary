using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Aviary.Core;
using Aviary.Infrastructure;

namespace Aviary.Qemu;

// What the control channel needs from the app: its machines, and a way to save changes that also updates the UI.
public interface IMachineLibrary
{
    IReadOnlyList<VmConfiguration> Machines { get; }
    VmStore Store { get; }
    MachineBackend Backend { get; }
    Task SaveAsync(VmConfiguration updated);
}

// The operations behind Aviary's MCP tools. Each takes JSON arguments and returns a JSON-serializable result.
public sealed class ControlService(IMachineLibrary library)
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    static string OpenSsh(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", exe);

    public Task<object> InvokeAsync(string method, JsonElement args, CancellationToken token) => method switch
    {
        "list_machines" => Task.FromResult<object>(library.Machines.Select(Describe).ToArray()),
        "start_machine" => StartAsync(Find(args), token),
        "stop_machine" => StopAsync(Find(args), Bool(args, "force"), token),
        "screenshot" => ScreenshotAsync(Find(args), token),
        "type_text" => Done(library.Backend.TypeTextAsync(Running(Find(args)).Id, String(args, "text"), token)),
        "press_keys" => PressAsync(Running(Find(args)), args, token),
        "setup_ssh" => SetupSshAsync(Find(args), Bool(args, "type"), !(args.TryGetProperty("keep_unlocked", out var k) && k.ValueKind == JsonValueKind.False), Int(args, "wait_seconds", 120), token),
        "run_command" => RunAsync(Find(args), String(args, "command"), Int(args, "timeout_seconds", 120), token),
        "put_file" => args.TryGetProperty("remote_path", out var remote) && remote.ValueKind == JsonValueKind.String && remote.GetString()!.Length > 0
            ? CopyAsync(Find(args), String(args, "local_path"), String(args, "remote_path"), upload: true, token)
            : PutInDownloadsAsync(Find(args), String(args, "local_path"), token),
        "get_file" => CopyAsync(Find(args), String(args, "local_path"), String(args, "remote_path"), upload: false, token),
        _ => throw new InvalidOperationException($"Unknown method '{method}'."),
    };

    static async Task<object> Done(Task task) { await task; return new { ok = true }; }
    static string String(JsonElement args, string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length > 0 ? v.GetString()! : throw new ArgumentException($"'{name}' is required.");
    static bool Bool(JsonElement args, string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static int Int(JsonElement args, string name, int fallback) => args.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) && i > 0 ? i : fallback;

    VmConfiguration Find(JsonElement args)
    {
        var key = String(args, "machine");
        return library.Machines.FirstOrDefault(m => m.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? library.Machines.FirstOrDefault(m => m.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No machine named '{key}'. Machines: {string.Join(", ", library.Machines.Select(m => m.Name))}.");
    }
    VmConfiguration Running(VmConfiguration vm) => library.Backend.GetStatus(vm.Id).Status == VmStatus.Running ? vm : throw new InvalidOperationException($"{vm.Name} isn't running (it's {library.Backend.GetStatus(vm.Id).Status}). Start it first.");

    object Describe(VmConfiguration vm)
    {
        var state = library.Backend.GetStatus(vm.Id);
        return new
        {
            Id = vm.Id, vm.Name, Os = vm.OperatingSystem, Engine = vm.Engine.ToString(), Status = state.Status.ToString(), state.Error,
            Cpus = vm.CpuCores, MemoryMb = vm.MemoryMB,
            Ssh = new { vm.SshEnabled, Ready = vm.SshEnabled && vm.SshUser.Length > 0, User = vm.SshUser, Connect = vm.SshEnabled && vm.SshUser.Length > 0 ? GuestProvisioning.SshCommand(library.Store.DirectoryFor(vm.Id)) : null },
        };
    }

    async Task<object> StartAsync(VmConfiguration vm, CancellationToken token)
    {
        if (library.Backend.GetStatus(vm.Id).Status is VmStatus.Running or VmStatus.Starting) return Describe(vm);
        await library.Backend.StartAsync(vm, token);
        return Describe(vm);
    }
    async Task<object> StopAsync(VmConfiguration vm, bool force, CancellationToken token)
    {
        if (force) await library.Backend.ForceStopAsync(vm.Id, token); else await library.Backend.StopAsync(vm.Id, token);
        return Describe(vm);
    }
    // Any live state: seeing the screen matters most when a machine is starting, paused or stuck shutting down.
    async Task<object> ScreenshotAsync(VmConfiguration vm, CancellationToken token) =>
        library.Backend.GetStatus(vm.Id).Status is VmStatus.Stopped or VmStatus.Error
            ? throw new InvalidOperationException($"{vm.Name} isn't running. Start it first.")
            : new { PngBase64 = Convert.ToBase64String(await library.Backend.ScreenshotAsync(vm.Id, token)) };
    async Task<object> PressAsync(VmConfiguration vm, JsonElement args, CancellationToken token)
    {
        var combos = args.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array ? keys.EnumerateArray().Select(k => k.GetString()!).ToArray() : [String(args, "keys")];
        foreach (var combo in combos) await library.Backend.PressKeysAsync(vm.Id, combo, token);
        return new { ok = true };
    }

    // Turns SSH on if needed, starts the guest-side setup, optionally types the command, and waits for the guest.
    async Task<object> SetupSshAsync(VmConfiguration vm, bool type, bool keepUnlocked, int waitSeconds, CancellationToken token)
    {
        Running(vm);
        if (!vm.NetworkEnabled) throw new InvalidOperationException("Turn on networking for this machine first.");
        if (!vm.SshEnabled)
        {
            int sshPort = vm.Engine == VmEngine.Qemu ? GuestProvisioning.AvailablePort() : 22, agentPort = 0;
            if (vm.Engine == VmEngine.Qemu) do agentPort = GuestProvisioning.AvailablePort(); while (agentPort == sshPort);
            vm = vm with { SshEnabled = true, SshPort = sshPort, SshAgentPort = agentPort };
            await library.SaveAsync(vm);
            library.Backend.EnableSsh(vm);
            if (vm.Engine == VmEngine.Qemu && !vm.UsesSshAgent)
                return new { Ready = false, Message = "SSH is now on. Restart this Windows machine so its SSH port opens, then call setup_ssh again." };
        }
        var setup = await library.Backend.BeginSshSetupAsync(vm, keepUnlocked, token);
        var machine = vm;
        var finished = setup.User.ContinueWith(async t =>
        {
            if (t.IsCompletedSuccessfully) await FinishSshAsync(library.Machines.First(m => m.Id == machine.Id), t.Result);
        }, TaskScheduler.Default).Unwrap();
        if (type) await library.Backend.TypeTextAsync(vm.Id, setup.Command + "\n", token);
        var instruction = GuestSsh.SetupInstruction(vm);
        if (await Task.WhenAny(finished, Task.Delay(TimeSpan.FromSeconds(waitSeconds), token)) != finished)
            return new { Ready = false, setup.Command, Message = (type ? "Typed the setup command; the guest hasn't reported back yet. Take a screenshot to check the guest. " : instruction + " Then run the command. ") + "Setup completes in the background; call list_machines to see when SSH is ready." };
        await finished; // surfaces setup errors
        var user = await setup.User;
        return new { Ready = true, User = user, Connect = GuestProvisioning.SshCommand(library.Store.DirectoryFor(vm.Id)) };
    }

    async Task FinishSshAsync(VmConfiguration vm, string user)
    {
        vm = vm with { SshUser = user };
        await library.SaveAsync(vm);
        await RefreshSshConfigAsync(vm, CancellationToken.None);
    }

    async Task<string> RefreshSshConfigAsync(VmConfiguration vm, CancellationToken token)
    {
        var directory = library.Store.DirectoryFor(vm.Id);
        var (host, port) = await library.Backend.SshEndpointAsync(vm, token);
        await GuestSsh.WriteConfigAsync(vm, directory, host, port);
        return GuestProvisioning.ConfigPath(directory);
    }

    async Task<string> ReadyConfigAsync(VmConfiguration vm, CancellationToken token)
    {
        Running(vm);
        if (!vm.SshEnabled || vm.SshUser.Length == 0) throw new InvalidOperationException($"SSH isn't set up for {vm.Name}. Set it up with SSH access in the machine's menu, or the setup_ssh tool.");
        return await RefreshSshConfigAsync(vm, token);
    }

    async Task<object> RunAsync(VmConfiguration vm, string command, int timeoutSeconds, CancellationToken token)
    {
        var config = await ReadyConfigAsync(vm, token);
        var result = await ProcessRunner.RunCapturedAsync(OpenSsh("ssh.exe"), ["-F", config, "-o", "BatchMode=yes", "guest", command], TimeSpan.FromSeconds(timeoutSeconds), token);
        // ssh itself exits 255 when it can't connect; report that as an error rather than a command result.
        if (result.ExitCode == 255 && result.Output.Length == 0) throw new IOException("SSH connection failed: " + result.Error.Trim());
        return new { result.ExitCode, Stdout = result.Output, Stderr = result.Error, result.TimedOut };
    }

    async Task<object> PutInDownloadsAsync(VmConfiguration vm, string localPath, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(localPath)) throw new ArgumentException("local_path must be an absolute path on this PC.");
        return new { ok = true, Guest_folder = await SendFilesAsync(vm, [localPath], token) };
    }

    // Files dropped on a machine in Aviary land in the guest's Downloads folder. Hyper-V copies over VMBus (no
    // network or SSH needed); QEMU guests use the SSH connection. Returns where they went, for the UI to report.
    public async Task<string> SendFilesAsync(VmConfiguration vm, IReadOnlyList<string> paths, CancellationToken token = default)
    {
        Running(vm);
        if (paths.Count == 0) return "";
        foreach (var path in paths) if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Nothing to copy at " + path);
        if (library.Backend.IsHyperVMachine(vm.Id))
            return await library.Backend.SendFilesOverVmBusAsync(vm, vm.SshUser.Length > 0 ? vm.SshUser : "", paths, token);
        var config = await ReadyConfigAsync(vm, token);
        var mkdir = vm.OperatingSystem == "Windows" ? "New-Item -ItemType Directory -Force Downloads | Out-Null" : "mkdir -p Downloads";
        await ProcessRunner.RunCapturedAsync(OpenSsh("ssh.exe"), ["-F", config, "-o", "BatchMode=yes", "guest", mkdir], TimeSpan.FromSeconds(30), token);
        var result = await ProcessRunner.RunCapturedAsync(OpenSsh("scp.exe"), ["-F", config, "-o", "BatchMode=yes", "-r", .. paths, "guest:Downloads/"], TimeSpan.FromMinutes(30), token);
        if (result.ExitCode != 0) throw new IOException("Copy failed: " + (result.Error.Trim().Length > 0 ? result.Error.Trim() : "scp exit " + result.ExitCode));
        return "Downloads";
    }

    async Task<object> CopyAsync(VmConfiguration vm, string localPath, string remotePath, bool upload, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(localPath)) throw new ArgumentException("local_path must be an absolute path on this PC.");
        if (upload && !File.Exists(localPath) && !Directory.Exists(localPath)) throw new FileNotFoundException("Nothing to upload at " + localPath);
        var config = await ReadyConfigAsync(vm, token);
        string remote = "guest:" + remotePath;
        string[] arguments = ["-F", config, "-o", "BatchMode=yes", "-r", .. upload ? new[] { localPath, remote } : [remote, localPath]];
        var result = await ProcessRunner.RunCapturedAsync(OpenSsh("scp.exe"), arguments, TimeSpan.FromMinutes(30), token);
        if (result.ExitCode != 0) throw new IOException("Copy failed: " + (result.Error.Trim().Length > 0 ? result.Error.Trim() : "scp exit " + result.ExitCode));
        return new { ok = true };
    }
}

// Serves ControlService on the per-user named pipe. Requests on one connection run concurrently.
public sealed class ControlServer : IAsyncDisposable
{
    readonly ControlService service;
    readonly CancellationTokenSource lifetime = new();
    readonly Task loop;
    public ControlServer(ControlService service, string? pipeName = null) { this.service = service; loop = AcceptAsync(pipeName ?? ControlPipe.Name); }

    async Task AcceptAsync(string name)
    {
        while (!lifetime.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(lifetime.Token); }
            catch { await pipe.DisposeAsync(); return; }
            _ = ServeAsync(pipe);
        }
    }

    async Task ServeAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var writeLock = new SemaphoreSlim(1);
        var pending = new List<Task>();
        try
        {
            while (await reader.ReadLineAsync(lifetime.Token) is { } line) pending.Add(HandleAsync(line, writer, writeLock));
            await Task.WhenAll(pending);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    async Task HandleAsync(string line, StreamWriter writer, SemaphoreSlim writeLock)
    {
        JsonElement id = default; string response;
        try
        {
            using var request = JsonDocument.Parse(line);
            id = request.RootElement.GetProperty("id").Clone();
            var method = request.RootElement.GetProperty("method").GetString()!;
            var args = request.RootElement.TryGetProperty("params", out var p) ? p.Clone() : JsonDocument.Parse("{}").RootElement;
            var result = await service.InvokeAsync(method, args, lifetime.Token);
            response = JsonSerializer.Serialize(new { id, result }, ControlService.Json);
        }
        catch (Exception ex) { response = JsonSerializer.Serialize(new { id, error = ex.Message }, ControlService.Json); }
        await writeLock.WaitAsync();
        try { await writer.WriteLineAsync(response); } catch (IOException) { } finally { writeLock.Release(); }
    }

    public async ValueTask DisposeAsync() { lifetime.Cancel(); try { await loop; } catch { } lifetime.Dispose(); }
}
