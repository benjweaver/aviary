using System.Text.Json;
using System.Text.Json.Nodes;
using Aviary.Core;
using Aviary.Infrastructure;
using Aviary.Mcp;
using Aviary.Qemu;
using Xunit;
namespace Aviary.Tests;

public sealed class McpTests
{
    sealed class FakeAviary(Func<string, JsonObject, JsonNode> handler) : IControl
    {
        public List<(string Method, JsonObject Args)> Calls { get; } = [];
        public Task<JsonNode> InvokeAsync(string method, JsonObject arguments) { Calls.Add((method, arguments)); return Task.FromResult(handler(method, arguments)); }
    }

    static async Task<JsonObject> Call(IControl aviary, string json) => (await Program.HandleAsync(json, aviary))!;

    [Fact]
    public async Task InitializeNegotiatesProtocolAndListsTools()
    {
        var aviary = new FakeAviary((_, _) => new JsonObject());
        var init = await Call(aviary, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
        Assert.Equal("2025-03-26", init["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.Equal("aviary", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Null(await Program.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", aviary));

        var tools = (await Call(aviary, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""))["result"]!["tools"]!.AsArray();
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();
        Assert.Equal(["list_machines", "start_machine", "stop_machine", "screenshot", "type_text", "press_keys", "setup_ssh", "run_command", "set_gpu_partition", "put_file", "get_file"], names);
        Assert.All(tools, t => Assert.Equal("object", t!["inputSchema"]!["type"]!.GetValue<string>()));
        Assert.Empty(aviary.Calls); // listing tools doesn't need the app
    }

    [Fact]
    public async Task ScreenshotIsReturnedAsAnImage()
    {
        var aviary = new FakeAviary((_, _) => new JsonObject { ["png_base64"] = "iVBORw0KGgo=" });
        var reply = await Call(aviary, """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"screenshot","arguments":{"machine":"cachyos"}}}""");
        var content = reply["result"]!["content"]![0]!;
        Assert.Equal("image", content["type"]!.GetValue<string>()); Assert.Equal("image/png", content["mimeType"]!.GetValue<string>());
        Assert.Equal("iVBORw0KGgo=", content["data"]!.GetValue<string>());
        Assert.Equal(("screenshot", "cachyos"), (aviary.Calls[0].Method, aviary.Calls[0].Args["machine"]!.GetValue<string>()));
    }

    [Fact]
    public async Task RunCommandReportsExitCodeAndBothStreams()
    {
        var aviary = new FakeAviary((_, _) => new JsonObject { ["exit_code"] = 2, ["stdout"] = "out\n", ["stderr"] = "bad\n", ["timed_out"] = false });
        var reply = await Call(aviary, """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"run_command","arguments":{"machine":"w","command":"ls"}}}""");
        Assert.Equal("exit code: 2\n--- stdout ---\nout\n--- stderr ---\nbad", reply["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.False(reply["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AviaryErrorsBecomeToolErrorsAndUnknownToolsAreProtocolErrors()
    {
        var aviary = new FakeAviary((_, _) => throw new ControlException("No machine named 'x'."));
        var reply = await Call(aviary, """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"start_machine","arguments":{"machine":"x"}}}""");
        Assert.True(reply["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("No machine named", reply["result"]!["content"]![0]!["text"]!.GetValue<string>());
        var unknown = await Call(aviary, """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"format_disk","arguments":{}}}""");
        Assert.Equal(-32602, unknown["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32601, (await Call(aviary, """{"jsonrpc":"2.0","id":7,"method":"resources/list"}"""))["error"]!["code"]!.GetValue<int>());
    }

    // The real pipe: ControlServer in-process, ControlClient as aviary-mcp would use it.
    sealed class FakeLibrary(string root) : IMachineLibrary
    {
        public IReadOnlyList<VmConfiguration> Machines { get; set; } = [new VmConfiguration { Name = "Alpine" }];
        public VmStore Store { get; } = new(root);
        public MachineBackend Backend { get; } = new(null, null, []);
        public Task SaveAsync(VmConfiguration updated) { Machines = Machines.Select(m => m.Id == updated.Id ? updated : m).ToArray(); return Task.CompletedTask; }
    }

    [Fact]
    public async Task ControlPipeRoundTripsAndReportsErrors()
    {
        var pipe = "aviary-test-" + Guid.NewGuid().ToString("N");
        var library = new FakeLibrary(Path.Combine(Path.GetTempPath(), "aviary-control-" + Guid.NewGuid()));
        await using var server = new ControlServer(new ControlService(library), pipe);
        await using var client = new ControlClient(pipe, appPath: Path.Combine(Path.GetTempPath(), "no-such-aviary.exe"));
        var machines = (await client.InvokeAsync("list_machines", [])).AsArray();
        Assert.Equal("Alpine", machines.Single()!["name"]!.GetValue<string>());
        Assert.Equal("Linux", machines.Single()!["os"]!.GetValue<string>());
        Assert.False(machines.Single()!["ssh"]!["ready"]!.GetValue<bool>());
        var error = await Assert.ThrowsAsync<ControlException>(() => client.InvokeAsync("run_command", new JsonObject { ["machine"] = "nope", ["command"] = "id" }));
        Assert.Contains("No machine named 'nope'", error.Message);
        // Concurrent requests on one connection are matched to their own responses.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.InvokeAsync("list_machines", [])));
        Assert.All(results, r => Assert.Single(r.AsArray()));
    }

    [Fact]
    public async Task ClientExplainsWhenAviaryIsNotRunning()
    {
        await using var client = new ControlClient("aviary-test-missing-" + Guid.NewGuid().ToString("N"), appPath: Path.Combine(Path.GetTempPath(), "no-such-aviary.exe"));
        var error = await Assert.ThrowsAsync<ControlException>(() => client.InvokeAsync("list_machines", []));
        Assert.Contains("Open Aviary", error.Message);
    }
}
