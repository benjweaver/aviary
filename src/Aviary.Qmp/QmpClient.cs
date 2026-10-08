using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace Aviary.Qmp;

public sealed class QmpClient : IAsyncDisposable
{
    readonly TcpClient client = new(); readonly CancellationTokenSource lifetime = new(); readonly SemaphoreSlim writerLock = new(1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    StreamReader? reader; StreamWriter? writer; Task? readLoop; long nextId;
    public event Action<string, JsonElement>? EventReceived;
    public static bool IsEvent(JsonElement message) => message.TryGetProperty("event", out _);
    public async Task ConnectAsync(int port, CancellationToken token = default)
    {
        await client.ConnectAsync("127.0.0.1", port, token); var stream = client.GetStream();
        reader = new(stream, Encoding.UTF8, false, 4096, true); writer = new(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true, NewLine = "\r\n" };
        var greeting = await reader.ReadLineAsync(token) ?? throw new IOException("QMP disconnected before greeting.");
        using var document = JsonDocument.Parse(greeting); if (!document.RootElement.TryGetProperty("QMP", out _)) throw new IOException("Invalid QMP greeting.");
        readLoop = ReadLoopAsync(); await ExecuteAsync("qmp_capabilities", null, token);
    }
    public async Task<JsonElement> ExecuteAsync(string command, object? arguments = null, CancellationToken token = default)
    {
        if (writer is null || lifetime.IsCancellationRequested) throw new IOException("QMP is disconnected.");
        var id = Interlocked.Increment(ref nextId); var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, lifetime.Token);
        // A peer closing after its final response must not cancel an already completed reply.
        try
        {
            await writerLock.WaitAsync(writeTimeout.Token);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(new { execute = command, arguments = arguments ?? new { }, id }).AsMemory(), writeTimeout.Token); } finally { writerLock.Release(); }
            return await completion.Task.WaitAsync(timeout.Token);
        }
        finally { pending.TryRemove(id, out _); }
    }
    async Task ReadLoopAsync()
    {
        Exception failure = new IOException("QMP connection closed.");
        try
        {
            while (await reader!.ReadLineAsync(lifetime.Token) is { } line)
            {
                using var doc = JsonDocument.Parse(line); var message = doc.RootElement;
                if (IsEvent(message)) { EventReceived?.Invoke(message.GetProperty("event").GetString()!, message.Clone()); continue; }
                if (!message.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value) || !pending.TryRemove(value, out var completion)) continue;
                if (message.TryGetProperty("error", out var error)) completion.TrySetException(new IOException(error.ToString()));
                else if (message.TryGetProperty("return", out var result)) completion.TrySetResult(result.Clone());
                else completion.TrySetException(new IOException("Malformed QMP response."));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or ObjectDisposedException) { failure = ex; }
        finally { lifetime.Cancel(); foreach (var pair in pending) pair.Value.TrySetException(failure); }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); client.Dispose(); if (readLoop is not null) await readLoop; reader?.Dispose(); writer?.Dispose(); lifetime.Dispose(); writerLock.Dispose(); }
}

