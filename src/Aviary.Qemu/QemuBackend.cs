using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qmp;
namespace Aviary.Qemu;

public sealed class QemuBackend(VmStore store, QemuInstallation qemu, HostCapabilities host) : IVirtualMachineBackend, IAsyncDisposable
{
    sealed class Session(Process process, QmpClient qmp, QemuEndpoints endpoints, ProcessLifetime lifetime) : IDisplayConnection
    {
        public ProcessLifetime Lifetime { get; } = lifetime; public Process Process { get; } = process; public QmpClient Qmp { get; } = qmp; public QemuEndpoints Endpoints { get; } = endpoints;
        public async Task<Stream> OpenAsync(CancellationToken token)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            // Windows AF_UNIX doesn't support the overlapped ConnectEx behind ConnectAsync (WSAEINVAL); reads and writes are fine.
            try { await Task.Run(() => socket.Connect(new UnixDomainSocketEndPoint(Endpoints.VncSocket)), token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
        public string LastError = ""; public bool Forced; public GuestSshBroker? Broker; public bool GlWindow; public volatile bool Exited; public int Id { get; } = process.Id; public DateTimeOffset StartedAt { get; } = new(process.StartTime); public Task? Monitor;
    }
    readonly ConcurrentDictionary<Guid, Session> sessions = new(); readonly ConcurrentBag<Process> completed = []; readonly ConcurrentDictionary<Guid, VmState> states = new(); readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    public event Action<VmState>? StateChanged;
    public VmState GetStatus(Guid id) => states.GetValueOrDefault(id) ?? new(id, VmStatus.Stopped);
    void Set(Guid id, VmStatus status, string? error = null, Session? session = null) { var state = new VmState(id, status, error, session?.Id, session?.StartedAt); states[id] = state; StateChanged?.Invoke(state); }
    public IDisplayConnection GetDisplay(Guid id) => sessions.TryGetValue(id, out var session)
        ? session.GlWindow ? throw new InvalidOperationException("This machine shows 3D graphics in its own window.") : session
        : throw new InvalidOperationException("VM is not running.");
    // 3D machines on Windows hosts draw in QEMU's own window instead of Aviary's display.
    public bool UsesOwnWindow(Guid id) => sessions.TryGetValue(id, out var session) && session.GlWindow;
    public void ShowOwnWindow(Guid id) { var session = Require(id); session.Process.Refresh(); WindowCapture.BringToFront(session.Process.MainWindowHandle); }
    public async Task<VmConfiguration> CreateAsync(VmConfiguration configuration, CancellationToken token = default)
    {
        HostProbe.ValidateAllocation(configuration, host);
        if (configuration.Engine != VmEngine.Qemu) throw new InvalidDataException("This machine requires the Hyper-V backend.");
        if (Directory.Exists(store.DirectoryFor(configuration.Id))) throw new IOException("VM already exists.");
        var dir = store.DirectoryFor(configuration.Id); Directory.CreateDirectory(Path.Combine(dir, "disks")); Directory.CreateDirectory(Path.Combine(dir, "logs")); Directory.CreateDirectory(Path.Combine(dir, "nvram"));
        var path = Path.Combine(dir, "disks", configuration.DiskFormat == DiskFormat.Qcow2 ? "system.qcow2" : "system.raw");
        await ProcessRunner.RunAsync(Path.Combine(qemu.Directory, "qemu-img.exe"), ["create", "-f", configuration.DiskFormat == DiskFormat.Qcow2 ? "qcow2" : "raw", path, $"{configuration.DiskGB}G"], token);
        var vm = await GuestProvisioning.PrepareAsync(configuration with { DiskPath = path }, dir, token); await store.SaveAsync(vm, token); return vm;
    }
    // The display socket's folder must be private: Windows AF_UNIX connections are checked against the file's access.
    static async Task PrepareRunDirectoryAsync(CancellationToken token)
    {
        Directory.CreateDirectory(QemuEndpoints.RunDirectory);
        if (OperatingSystem.IsWindows()) await GuestProvisioning.ProtectDirectoryAsync(QemuEndpoints.RunDirectory, token);
    }
    // QEMU serves the QMP pipe once it has parsed its options; the client waits for it and checks it's served by this user.
    static async Task<NamedPipeClientStream> ConnectQmpAsync(Process process, Session session, string pipe, CancellationToken token)
    {
        while (true)
        {
            if (process.HasExited) throw new IOException(session.LastError.Length > 0 ? session.LastError : "QEMU exited before its control channel became ready.");
            var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await client.ConnectAsync(500, token); return client; }
            catch (TimeoutException) { await client.DisposeAsync(); }
            catch { await client.DisposeAsync(); throw; }
        }
    }
    public async Task StartAsync(VmConfiguration configuration, CancellationToken token = default)
    {
        if (configuration.Engine != VmEngine.Qemu) throw new InvalidDataException("This machine requires the Hyper-V backend.");
        var gate = gates.GetOrAdd(configuration.Id, _ => new(1)); await gate.WaitAsync(token);
        try
        {
            if (sessions.ContainsKey(configuration.Id)) throw new InvalidOperationException("VM is already running.");
            if (!File.Exists(configuration.DiskPath)) throw new FileNotFoundException("The virtual disk is missing.", configuration.DiskPath);
            if (configuration.IsoPath.Length > 0 && !File.Exists(configuration.IsoPath)) throw new FileNotFoundException("The installer ISO is missing. Edit this VM to remove or replace it.", configuration.IsoPath);
            if (configuration.SetupIsoPath.Length > 0 && !File.Exists(configuration.SetupIsoPath)) throw new FileNotFoundException("The setup CD is missing. Eject setup media in this machine's menu.");
            if (configuration.DriverIsoPath.Length > 0 && !File.Exists(configuration.DriverIsoPath)) throw new FileNotFoundException("The VirtIO driver CD is missing. Eject it in this machine's menu, or attach it again to re-download.");
            if (configuration.SshEnabled) await GuestSsh.WriteConfigAsync(configuration, store.DirectoryFor(configuration.Id), "127.0.0.1", configuration.SshPort);
            Set(configuration.Id, VmStatus.Starting);
            await PrepareRunDirectoryAsync(token); var endpoints = QemuEndpoints.Create();
            var process = new Process { StartInfo = QemuCommandBuilder.Build(configuration, qemu, host, endpoints) }; var qmp = new QmpClient();
            if (!process.Start()) throw new IOException("QEMU could not start.");
            ProcessLifetime ownership; try { ownership = new ProcessLifetime(process); } catch { if (!process.HasExited) process.Kill(true); process.Dispose(); await qmp.DisposeAsync(); throw; }
            var session = new Session(process, qmp, endpoints, ownership) { GlWindow = QemuCommandBuilder.UsesGlWindow(configuration, qemu) }; sessions[configuration.Id] = session;
            var stdout = CaptureAsync(configuration.Id, process.StandardOutput, "stdout", session); var stderr = CaptureAsync(configuration.Id, process.StandardError, "stderr", session);
            session.Monitor = MonitorAsync(configuration.Id, session, stdout, stderr);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                qmp.EventReceived += (name, _) => { if (!process.HasExited) { if (name == "STOP") Set(configuration.Id, VmStatus.Paused, session: session); if (name == "RESUME") Set(configuration.Id, VmStatus.Running, session: session); } };
                await qmp.ConnectAsync(await ConnectQmpAsync(process, session, endpoints.QmpPipe, timeout.Token), timeout.Token); await qmp.ExecuteAsync("query-status", token: timeout.Token); Set(configuration.Id, VmStatus.Running, session: session);
                StartSshBroker(configuration, session);
            }
            catch { session.Forced = true; if (!process.HasExited) process.Kill(true); await session.Monitor; throw; }
        }
        catch (Exception ex) { if (!sessions.ContainsKey(configuration.Id)) Set(configuration.Id, VmStatus.Error, ex.Message); throw; }
        finally { gate.Release(); }
    }
    async Task CaptureAsync(Guid id, StreamReader reader, string source, Session session)
    {
        var dir = Path.Combine(store.DirectoryFor(id), "logs"); Directory.CreateDirectory(dir);
        await using var log = new StreamWriter(Path.Combine(dir, source + ".jsonl"), append: true) { AutoFlush = true };
        while (await reader.ReadLineAsync() is { } line) { if (source == "stderr") session.LastError = line; await log.WriteLineAsync(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, source, message = line })); }
    }
    async Task MonitorAsync(Guid id, Session session, Task stdout, Task stderr)
    {
        try { await session.Process.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); var code = session.Process.ExitCode; Set(id, code == 0 || session.Forced ? VmStatus.Stopped : VmStatus.Error, code == 0 || session.Forced ? null : $"QEMU exited with code {code}: {session.LastError}"); }
        catch (Exception ex) { Set(id, VmStatus.Error, ex.Message); }
        finally { session.Exited = true; sessions.TryRemove(id, out _); if (session.Broker is not null) await session.Broker.DisposeAsync(); await session.Qmp.DisposeAsync(); session.Lifetime.Dispose(); completed.Add(session.Process); try { File.Delete(session.Endpoints.VncSocket); } catch (IOException) { } }
    }
    // Linux guests dial out to the broker for every SSH session; Windows guests only fetch setup from it
    // (their sshd is reached through the -nic hostfwd). A busy port leaves SSH unavailable, not the VM.
    void StartSshBroker(VmConfiguration vm, Session session)
    {
        if (!vm.SshEnabled || vm.SshAgentPort == 0 || session.Broker is not null) return;
        try { session.Broker = new GuestSshBroker(vm.SshAgentPort, vm.UsesSshAgent ? vm.SshPort : 0); }
        catch (SocketException ex) { session.LastError = $"SSH unavailable: port {vm.SshAgentPort} or {vm.SshPort} is in use ({ex.SocketErrorCode})."; }
    }
    // Turning SSH on while the machine runs: Linux works immediately; Windows needs a restart for its port forward.
    public void EnableSsh(VmConfiguration vm) { if (sessions.TryGetValue(vm.Id, out var session)) StartSshBroker(vm, session); }

    public async Task<GuestSsh.Setup> BeginSshSetupAsync(VmConfiguration vm, bool keepUnlocked = true, CancellationToken token = default)
    {
        var session = Require(vm.Id);
        if (!vm.SshEnabled) throw new InvalidOperationException("Turn on SSH access first.");
        var broker = session.Broker ?? throw new InvalidOperationException(session.LastError.StartsWith("SSH unavailable", StringComparison.Ordinal) ? session.LastError : "Restart the machine to finish turning on SSH access.");
        var keys = await GuestSsh.EnsureKeysAsync(vm, store.DirectoryFor(vm.Id), token);
        var msi = vm.UsesSshAgent ? "" : broker.OfferFile(await OpenSshInstaller.EnsureAsync(token));
        var user = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Failed(string code) => user.TrySetException(new InvalidOperationException(code switch
        {
            "no-sshd" => "OpenSSH server isn't installed in the guest. Install it (openssh-server or openssh) and try again.",
            "not-admin" => "Run the command in PowerShell opened as administrator.",
            "no-service" => "The guest couldn't start its SSH service. Check `systemctl --user status aviary-ssh` in the guest.",
            _ => $"Guest setup failed ({code}). See the guest terminal for details.",
        }));
        string setupToken = broker.OfferSetup(t => vm.UsesSshAgent
            ? GuestSsh.LinuxSetupScript(broker.AgentPort, t, keys, keepUnlocked)
            : GuestSsh.WindowsSetupScript(keys, "10.0.2.2", broker.ReportUrl(t), msi), name => user.TrySetResult(name), Failed);
        // sh -c so the same line works from bash, zsh and fish; wget covers minimal installs without curl.
        string url = broker.SetupUrl(setupToken);
        string command = vm.UsesSshAgent ? $"sh -c 'curl -fsS {url} || wget -qO- {url}' | sh" : $"irm {url} | iex";
        return new(command, user.Task);
    }

    // Keyboard and screen access for automation (Aviary's MCP tools and SSH setup).
    public async Task SendChordAsync(Guid id, string[] qcodes, CancellationToken token = default)
    {
        var session = Require(id);
        // send-key holds keys for hold-time ms; pressing the next chord sooner leaves modifiers stuck.
        await session.Qmp.ExecuteAsync("send-key", new Dictionary<string, object> { ["keys"] = qcodes.Select(k => new { type = "qcode", data = k }).ToArray(), ["hold-time"] = 20 }, token);
        await Task.Delay(45, token);
    }
    public async Task TypeTextAsync(Guid id, string text, CancellationToken token = default)
    {
        var chords = QemuKeyboard.Text(text).ToList(); // validate everything before typing anything
        foreach (var chord in chords) await SendChordAsync(id, chord, token);
    }
    public async Task<byte[]> ScreenshotAsync(Guid id, CancellationToken token = default)
    {
        var session = Require(id);
        if (session.GlWindow) { session.Process.Refresh(); return WindowCapture.ClientAreaPng(session.Process.MainWindowHandle); }
        var path = Path.Combine(Path.GetTempPath(), "aviary-screen-" + Guid.NewGuid().ToString("N") + ".png");
        try { await session.Qmp.ExecuteAsync("screendump", new { filename = path, format = "png" }, token); return await File.ReadAllBytesAsync(path, token); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    Session Require(Guid id) => sessions.TryGetValue(id, out var value) ? value : throw new InvalidOperationException("VM is not running.");
    public async Task StopAsync(Guid id, CancellationToken token = default) { var session = Require(id); await session.Qmp.ExecuteAsync("system_powerdown", token: token); Set(id, VmStatus.Stopping, session: session); }
    public async Task ForceStopAsync(Guid id, CancellationToken token = default) { var session = Require(id); session.Forced = true; if (!session.Process.HasExited) session.Process.Kill(true); if (session.Monitor is not null) await session.Monitor.WaitAsync(token); }
    public async Task PauseAsync(Guid id, CancellationToken token = default) { var s = Require(id); await s.Qmp.ExecuteAsync("stop", token: token); Set(id, VmStatus.Paused, session: s); }
    public async Task ResumeAsync(Guid id, CancellationToken token = default) { var s = Require(id); await s.Qmp.ExecuteAsync("cont", token: token); Set(id, VmStatus.Running, session: s); }
    public async Task ResetAsync(Guid id, CancellationToken token = default) => await Require(id).Qmp.ExecuteAsync("system_reset", token: token);
    public async ValueTask DisposeAsync() { foreach (var id in sessions.Keys) { if (sessions.ContainsKey(id)) await ForceStopAsync(id); } while (completed.TryTake(out var process)) process.Dispose(); }
}



