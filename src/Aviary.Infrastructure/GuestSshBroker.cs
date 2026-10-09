using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Aviary.Infrastructure;

// Reverse SSH for Linux guests on QEMU. Guests reach the host's loopback as 10.0.2.2, so the guest agent
// dials out to AgentPort and waits; when an ssh client connects to ClientPort, Aviary pairs the two and
// sends one byte telling the agent to start sshd on that connection. Guest firewalls never see inbound
// traffic. AgentPort also serves the one-time setup script over HTTP. Any guest or local process can reach
// AgentPort, so an impostor agent is possible; ssh rejects it because Aviary pins the guest's host key.
public sealed partial class GuestSshBroker : IAsyncDisposable
{
    public const string AgentHello = "AVIARY-AGENT";
    readonly TcpListener agents;
    readonly TcpListener? clients;
    readonly ConcurrentQueue<TcpClient> idle = new();
    readonly SemaphoreSlim available = new(0);
    readonly ConcurrentDictionary<string, Setup> setups = new();
    readonly ConcurrentDictionary<string, string> files = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Task agentLoop, clientLoop;
    sealed record Setup(string Script, Action<string> Ready, Action<string> Failed);

    public int AgentPort { get; }
    public int ClientPort { get; }

    public GuestSshBroker(int agentPort, int clientPort)
    {
        agents = new TcpListener(IPAddress.Loopback, agentPort);
        clients = clientPort == 0 ? null : new TcpListener(IPAddress.Loopback, clientPort);
        agents.Start();
        try { clients?.Start(); } catch { agents.Stop(); throw; }
        AgentPort = agentPort; ClientPort = clientPort;
        agentLoop = AcceptAgentsAsync(); clientLoop = clients is null ? Task.CompletedTask : AcceptClientsAsync();
    }

    public int IdleAgents => idle.Count;

    // clientPort 0 serves setup only (Windows guests, whose sshd is reached through a QEMU port forward).
    // Registers a one-time setup script; returns its token. Ready receives the guest user name, Failed an error code.
    // The script is built from the token because it reports back to /done/<token>.
    public string OfferSetup(Func<string, string> script, Action<string> ready, Action<string> failed)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        setups[token] = new(script(token), ready, failed);
        return token;
    }

    // Serves a host file (the OpenSSH MSI for Windows guests) at an unguessable URL until the broker closes.
    public string OfferFile(string path)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        files[token] = path;
        return $"http://10.0.2.2:{AgentPort}/f/{token}";
    }

    public string SetupUrl(string token) => $"http://10.0.2.2:{AgentPort}/s/{token}";
    public string ReportUrl(string token) => $"http://10.0.2.2:{AgentPort}/done/{token}";

    async Task AcceptAgentsAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient connection;
            try { connection = await agents.AcceptTcpClientAsync(lifetime.Token); } catch { return; }
            _ = Task.Run(() => ClassifyAsync(connection));
        }
    }

    async Task ClassifyAsync(TcpClient connection)
    {
        try
        {
            connection.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var line = await ReadLineAsync(connection.GetStream(), timeout.Token);
            if (line == AgentHello) { idle.Enqueue(connection); available.Release(); return; }
            if (line.StartsWith("GET ", StringComparison.Ordinal)) { await ServeAsync(connection, line, timeout.Token); }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        connection.Dispose();
    }

    // Reads one line byte by byte so nothing after it is consumed from the stream.
    static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>(); var one = new byte[1];
        while (bytes.Count < 1024)
        {
            if (await stream.ReadAsync(one, token) == 0) throw new IOException("Connection closed.");
            if (one[0] == '\n') break;
            if (one[0] != '\r') bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    [GeneratedRegex("^GET /(s|done|f)/([0-9a-f]{24})(?:/(ok|error)/([A-Za-z0-9_.%-]{1,96}))? HTTP/1\\.[01]$")]
    private static partial Regex Request();

    async Task ServeAsync(TcpClient connection, string requestLine, CancellationToken token)
    {
        var stream = connection.GetStream();
        while ((await ReadLineAsync(stream, token)).Length > 0) { } // headers
        var match = Request().Match(requestLine);
        string status = "404 Not Found", body = "";
        if (match.Success && match.Groups[1].Value == "f" && !match.Groups[3].Success && files.TryGetValue(match.Groups[2].Value, out var file) && File.Exists(file))
        {
            var bytes = await File.ReadAllBytesAsync(file, token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), token);
            await stream.WriteAsync(bytes, token);
            return;
        }
        if (match.Success && match.Groups[1].Value == "s" && setups.TryGetValue(match.Groups[2].Value, out var offered) && !match.Groups[3].Success)
        { status = "200 OK"; body = offered.Script; }
        else if (match.Success && match.Groups[1].Value == "done" && match.Groups[3].Success && setups.TryRemove(match.Groups[2].Value, out var finished))
        {
            status = "200 OK";
            if (match.Groups[3].Value == "ok") finished.Ready(Uri.UnescapeDataString(match.Groups[4].Value)); else finished.Failed(Uri.UnescapeDataString(match.Groups[4].Value));
        }
        var payload = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token);
    }

    async Task AcceptClientsAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await clients!.AcceptTcpClientAsync(lifetime.Token); } catch { return; }
            _ = Task.Run(() => PairAsync(client));
        }
    }

    async Task PairAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); wait.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                try { await available.WaitAsync(wait.Token); } catch (OperationCanceledException) { return; }
                if (!idle.TryDequeue(out var agent)) continue;
                using (agent)
                {
                    // Skip agents whose guest went away while they were idle.
                    if (agent.Client.Poll(0, SelectMode.SelectRead) && agent.Client.Available == 0) continue;
                    try
                    {
                        var guest = agent.GetStream(); var host = client.GetStream();
                        await guest.WriteAsync(new byte[] { 1 }, lifetime.Token);
                        await Task.WhenAny(host.CopyToAsync(guest, lifetime.Token), guest.CopyToAsync(host, lifetime.Token));
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); agents.Stop(); clients?.Stop();
        try { await Task.WhenAll(agentLoop, clientLoop); } catch { }
        while (idle.TryDequeue(out var agent)) agent.Dispose();
        lifetime.Dispose(); available.Dispose();
    }
}
