using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Qmp;
namespace Aviary.Qemu;

public sealed class QemuBackend(VmStore store, QemuInstallation qemu, HostCapabilities host) : IVirtualMachineBackend, IAsyncDisposable
{
    sealed class Session(Process process, QmpClient qmp, int displayPort, ProcessLifetime lifetime) : IDisplayConnection
    {
        public ProcessLifetime Lifetime { get; } = lifetime; public Process Process { get; } = process; public QmpClient Qmp { get; } = qmp; public int Port { get; } = displayPort;
        public string LastError = ""; public bool Forced; public volatile bool Exited; public int Id { get; } = process.Id; public DateTimeOffset StartedAt { get; } = new(process.StartTime); public Task? Monitor;
    }
    readonly ConcurrentDictionary<Guid, Session> sessions = new(); readonly ConcurrentBag<Process> completed = []; readonly ConcurrentDictionary<Guid, VmState> states = new(); readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    public event Action<VmState>? StateChanged;
    public VmState GetStatus(Guid id) => states.GetValueOrDefault(id) ?? new(id, VmStatus.Stopped);
    void Set(Guid id, VmStatus status, string? error = null, Session? session = null) { var state = new VmState(id, status, error, session?.Id, session?.StartedAt); states[id] = state; StateChanged?.Invoke(state); }
    public IDisplayConnection GetDisplay(Guid id) => sessions.TryGetValue(id, out var session) ? session : throw new InvalidOperationException("VM is not running.");
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
    static int FreePort(int minimum = 1024) { for (int i = 0; i < 100; i++) { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); if (port >= minimum) return port; } throw new IOException("No local port is available."); }
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
            if (configuration.SshEnabled) await GuestProvisioning.WriteSshConfigAsync(configuration, store.DirectoryFor(configuration.Id));
            Set(configuration.Id, VmStatus.Starting); var qmpPort = FreePort(); var vncPort = FreePort(5900); while (vncPort == qmpPort) vncPort = FreePort(5900);
            var process = new Process { StartInfo = QemuCommandBuilder.Build(configuration, qemu, host, qmpPort, vncPort) }; var qmp = new QmpClient();
            if (!process.Start()) throw new IOException("QEMU could not start.");
            ProcessLifetime ownership; try { ownership = new ProcessLifetime(process); } catch { if (!process.HasExited) process.Kill(true); process.Dispose(); await qmp.DisposeAsync(); throw; }
            var session = new Session(process, qmp, vncPort, ownership); sessions[configuration.Id] = session;
            var stdout = CaptureAsync(configuration.Id, process.StandardOutput, "stdout", session); var stderr = CaptureAsync(configuration.Id, process.StandardError, "stderr", session);
            session.Monitor = MonitorAsync(configuration.Id, session, stdout, stderr);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                // Each failed TCP connection uses a fresh socket inside the probe; the QMP client connects only when ready.
                while (true) { if (process.HasExited) throw new IOException(session.LastError.Length > 0 ? session.LastError : "QEMU exited before its control channel became ready."); using var probe = new TcpClient(); using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token); attempt.CancelAfter(500); try { await probe.ConnectAsync(IPAddress.Loopback, qmpPort, attempt.Token); break; } catch (Exception ex) when ((ex is SocketException or OperationCanceledException) && !timeout.IsCancellationRequested) { await Task.Delay(100, timeout.Token); } }
                qmp.EventReceived += (name, _) => { if (!process.HasExited) { if (name == "STOP") Set(configuration.Id, VmStatus.Paused, session: session); if (name == "RESUME") Set(configuration.Id, VmStatus.Running, session: session); } };
                await qmp.ConnectAsync(qmpPort, timeout.Token); await qmp.ExecuteAsync("query-status", token: timeout.Token); Set(configuration.Id, VmStatus.Running, session: session);
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
        finally { session.Exited = true; sessions.TryRemove(id, out _); await session.Qmp.DisposeAsync(); session.Lifetime.Dispose(); completed.Add(session.Process); }
    }
    Session Require(Guid id) => sessions.TryGetValue(id, out var value) ? value : throw new InvalidOperationException("VM is not running.");
    public async Task StopAsync(Guid id, CancellationToken token = default) { var session = Require(id); await session.Qmp.ExecuteAsync("system_powerdown", token: token); Set(id, VmStatus.Stopping, session: session); }
    public async Task ForceStopAsync(Guid id, CancellationToken token = default) { var session = Require(id); session.Forced = true; if (!session.Process.HasExited) session.Process.Kill(true); if (session.Monitor is not null) await session.Monitor.WaitAsync(token); }
    public async Task PauseAsync(Guid id, CancellationToken token = default) { var s = Require(id); await s.Qmp.ExecuteAsync("stop", token: token); Set(id, VmStatus.Paused, session: s); }
    public async Task ResumeAsync(Guid id, CancellationToken token = default) { var s = Require(id); await s.Qmp.ExecuteAsync("cont", token: token); Set(id, VmStatus.Running, session: s); }
    public async Task ResetAsync(Guid id, CancellationToken token = default) => await Require(id).Qmp.ExecuteAsync("system_reset", token: token);
    public async ValueTask DisposeAsync() { foreach (var id in sessions.Keys) { if (sessions.ContainsKey(id)) await ForceStopAsync(id); } while (completed.TryTake(out var process)) process.Dispose(); }
}



