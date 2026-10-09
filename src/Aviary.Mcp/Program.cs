using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aviary.Core;

// aviary-mcp: a stdio MCP server that lets AI tools drive Aviary's virtual machines. It forwards each tool call to
// the running Aviary app over a per-user named pipe, starting Aviary if needed. stdout carries only JSON-RPC.
namespace Aviary.Mcp;

public static class Program
{
    const string Version = "0.2.0";
    static readonly string[] Protocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

    static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name, ["description"] = description,
        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()) },
    };
    static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    static readonly JsonObject Machine = Str("Machine name or id, as shown by list_machines.");

    public static JsonArray Tools() =>
    [
        Tool("list_machines", "List Aviary virtual machines with their OS, engine (Qemu or HyperV), status, and whether SSH is ready.", []),
        Tool("start_machine", "Start a virtual machine. Returns once it is running; the guest OS keeps booting afterwards.", new() { ["machine"] = Machine.DeepClone() }, "machine"),
        Tool("stop_machine", "Shut down a virtual machine. Graceful by default (like pressing the power button); force=true powers it off immediately and may lose unsaved guest work.",
            new() { ["machine"] = Machine.DeepClone(), ["force"] = new JsonObject { ["type"] = "boolean" } }, "machine"),
        Tool("screenshot", "Capture the guest's screen as a PNG image. Use it to see what's happening before typing or after running UI actions. Shows the VM's own console; a Hyper-V Enhanced Session (RDP) isn't visible here. A black screen usually means the guest display is asleep: press_keys \"shift\" wakes it.", new() { ["machine"] = Machine.DeepClone() }, "machine"),
        Tool("type_text", "Type text into the guest as keyboard input (US layout; \\n presses Enter). Goes to whatever has focus, so check with screenshot first.",
            new() { ["machine"] = Machine.DeepClone(), ["text"] = Str("Text to type.") }, "machine", "text"),
        Tool("press_keys", "Press key combinations in the guest, e.g. \"enter\", \"ctrl+alt+t\", \"win+r\", \"alt+f4\". Pass one combination or an array to press in order.",
            new() { ["machine"] = Machine.DeepClone(), ["keys"] = new JsonObject { ["description"] = "A combination like \"ctrl+c\", or an array of them.", ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }) } }, "machine", "keys"),
        Tool("setup_ssh", "Set up SSH so run_command and file copies work. Linux guests (QEMU) need a terminal focused in the guest and OpenSSH server installed, but no sudo. Windows guests need an administrator PowerShell focused. With type=true the setup command is typed into the focused window for you. Waits up to wait_seconds for the guest to finish.",
            new() { ["machine"] = Machine.DeepClone(), ["type"] = new JsonObject { ["type"] = "boolean", ["description"] = "Type the setup command into the guest's focused window." }, ["keep_unlocked"] = new JsonObject { ["type"] = "boolean", ["description"] = "Linux: turn off automatic screen locking for this user (default true) so screenshots and typing keep working." }, ["wait_seconds"] = new JsonObject { ["type"] = "integer" } }, "machine"),
        Tool("run_command", "Run a shell command in the guest over SSH and return its exit code and output. Linux runs it with the user's shell; Windows with PowerShell. Requires setup_ssh.",
            new() { ["machine"] = Machine.DeepClone(), ["command"] = Str("Command line to run in the guest."), ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Default 120." } }, "machine", "command"),
        Tool("set_gpu_partition", "Hyper-V Windows guests: give the guest a partition of this PC's GPU (enable=true) or remove it (enable=false). Turning it on needs the guest running with SSH set up: it copies the host GPU driver in, shuts the guest down, attaches the partition and starts it again. The host GPU keeps working; the guest uses it only while running. Experimental on consumer GPUs.",
            new() { ["machine"] = Machine.DeepClone(), ["enable"] = new JsonObject { ["type"] = "boolean" } }, "machine", "enable"),
        Tool("put_file", "Copy a file or folder from this PC into the guest. With remote_path it goes there over SSH. Without it, it goes to the guest user's Downloads folder: on Hyper-V that works without SSH (copied over VMBus).",
            new() { ["machine"] = Machine.DeepClone(), ["local_path"] = Str("Absolute path on this PC."), ["remote_path"] = Str("Destination path in the guest. Optional: omit to use the guest's Downloads folder.") }, "machine", "local_path"),
        Tool("get_file", "Copy a file or folder from the guest to this PC over SSH.",
            new() { ["machine"] = Machine.DeepClone(), ["remote_path"] = Str("Path in the guest."), ["local_path"] = Str("Absolute destination path on this PC.") }, "machine", "remote_path", "local_path"),
    ];

    public static async Task<int> Main()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var writeLock = new SemaphoreSlim(1);
        await using var aviary = new ControlClient();
        var calls = new List<Task>();
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            calls.Add(Task.Run(async () =>
            {
                var reply = await HandleAsync(line, aviary);
                if (reply is null) return;
                await writeLock.WaitAsync();
                try { await stdout.WriteLineAsync(reply.ToJsonString()); } finally { writeLock.Release(); }
            }));
        }
        await Task.WhenAll(calls);
        return 0;
    }

    public static async Task<JsonObject?> HandleAsync(string line, IControl aviary)
    {
        JsonNode? id = null;
        try
        {
            var message = JsonNode.Parse(line)!.AsObject();
            id = message["id"]?.DeepClone();
            var method = message["method"]?.GetValue<string>();
            if (id is null) return null; // notification (e.g. notifications/initialized)
            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = Protocols.Contains(message["params"]?["protocolVersion"]?.GetValue<string>()) ? message["params"]!["protocolVersion"]!.GetValue<string>() : Protocols[0],
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "aviary", ["version"] = Version },
                    ["instructions"] = "Aviary runs Windows and Linux virtual machines on this PC. Use list_machines first. Use screenshot to see the guest, type_text/press_keys to drive its UI, and run_command for shell access once setup_ssh has succeeded.",
                },
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = Tools() },
                "tools/call" => await CallAsync(message["params"]!["name"]!.GetValue<string>(), message["params"]?["arguments"]?.AsObject() ?? [], aviary),
                _ => throw new JsonRpcException(-32601, "Method not found: " + method),
            };
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (JsonRpcException ex) { return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = ex.Code, ["message"] = ex.Message } }; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        { return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "Invalid request: " + ex.Message } }; }
    }

    sealed class JsonRpcException(int code, string message) : Exception(message) { public int Code { get; } = code; }

    static JsonObject Text(string text, bool error = false) => new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }), ["isError"] = error };

    static async Task<JsonObject> CallAsync(string tool, JsonObject arguments, IControl aviary)
    {
        if (!Tools().Any(t => t!["name"]!.GetValue<string>() == tool)) throw new JsonRpcException(-32602, "Unknown tool: " + tool);
        JsonNode result;
        try { result = await aviary.InvokeAsync(tool, arguments); }
        catch (ControlException ex) { return Text(ex.Message, error: true); }
        switch (tool)
        {
            case "screenshot":
                return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = result["png_base64"]!.GetValue<string>() }), ["isError"] = false };
            case "run_command":
                var text = new StringBuilder($"exit code: {result["exit_code"]}");
                if (result["timed_out"]?.GetValue<bool>() == true) text.Append(" (timed out and was stopped)");
                var stdout = result["stdout"]?.GetValue<string>() ?? ""; var stderr = result["stderr"]?.GetValue<string>() ?? "";
                if (stdout.Length > 0) text.Append("\n--- stdout ---\n").Append(stdout.TrimEnd());
                if (stderr.Length > 0) text.Append("\n--- stderr ---\n").Append(stderr.TrimEnd());
                return Text(text.ToString());
            default:
                return Text(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}

public sealed class ControlException(string message) : Exception(message);

public interface IControl { Task<JsonNode> InvokeAsync(string method, JsonObject arguments); }

// Talks to the Aviary app's control pipe, launching Aviary if nothing answers.
public sealed class ControlClient(string? pipeName = null, string? appPath = null) : IControl, IAsyncDisposable
{
    readonly string name = pipeName ?? ControlPipe.Name;
    readonly SemaphoreSlim connectLock = new(1), writeLock = new(1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode>> pending = new();
    NamedPipeClientStream? pipe; StreamWriter? writer; long nextId;

    async Task<StreamWriter> ConnectAsync()
    {
        await connectLock.WaitAsync();
        try
        {
            if (pipe is { IsConnected: true } && writer is not null) return writer;
            pipe?.Dispose();
            pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(1500); }
            catch (TimeoutException)
            {
                LaunchAviary();
                try { await pipe.ConnectAsync(45000); }
                catch (TimeoutException) { throw new ControlException("Aviary isn't running and didn't start. Open Aviary, then try again."); }
            }
            writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _ = ReadAsync(new StreamReader(pipe, new UTF8Encoding(false)));
            return writer;
        }
        finally { connectLock.Release(); }
    }

    void LaunchAviary()
    {
        var app = appPath ?? Path.Combine(AppContext.BaseDirectory, "Aviary.App.exe");
        if (!File.Exists(app)) throw new ControlException("Aviary isn't running. Open Aviary, then try again.");
        Console.Error.WriteLine("aviary-mcp: starting Aviary");
        Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app)! });
    }

    async Task ReadAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                var reply = JsonNode.Parse(line)!.AsObject();
                if (!pending.TryRemove(reply["id"]!.GetValue<long>(), out var call)) continue;
                if (reply["error"] is { } error) call.TrySetException(new ControlException(error.GetValue<string>()));
                else call.TrySetResult(reply["result"]?.DeepClone() ?? new JsonObject());
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException) { }
        foreach (var call in pending) if (pending.TryRemove(call.Key, out var c)) c.TrySetException(new ControlException("Aviary closed the connection."));
    }

    public async Task<JsonNode> InvokeAsync(string method, JsonObject arguments)
    {
        var w = await ConnectAsync();
        var id = Interlocked.Increment(ref nextId);
        var call = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = call;
        await writeLock.WaitAsync();
        try { await w.WriteLineAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = arguments.DeepClone() }.ToJsonString()); }
        catch (IOException) { pending.TryRemove(id, out _); throw new ControlException("Lost the connection to Aviary. Try again."); }
        finally { writeLock.Release(); }
        return await call.Task;
    }

    public ValueTask DisposeAsync() { pipe?.Dispose(); connectLock.Dispose(); writeLock.Dispose(); return ValueTask.CompletedTask; }
}
