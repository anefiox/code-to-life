using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using CodeToLife.Runtime.Dap;

namespace CodeToLife.Tests;

public class DapClientTests
{
    [Fact]
    public async Task Reads_content_length_framed_messages()
    {
        var bytes = Frame("""{"seq":1,"type":"event","event":"initialized"}""") + Frame("""{"seq":2,"type":"event","event":"output","body":{"output":"héllo"}}""");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(bytes));

        var first = await DapClient.ReadMessageAsync(stream, default);
        var second = await DapClient.ReadMessageAsync(stream, default);
        var end = await DapClient.ReadMessageAsync(stream, default);

        Assert.Equal("initialized", first?["event"]?.GetValue<string>());
        Assert.Equal("héllo", second?["body"]?["output"]?.GetValue<string>());
        Assert.Null(end);
    }

    [Fact]
    public async Task Matches_responses_to_requests_and_raises_events()
    {
        // A pretend adapter on the other end of two pipes.
        using var toAdapter = new AnonymousPipeServerStream(PipeDirection.In);
        using var adapterIn = new AnonymousPipeClientStream(PipeDirection.Out, toAdapter.ClientSafePipeHandle);
        using var fromAdapter = new AnonymousPipeServerStream(PipeDirection.Out);
        using var clientIn = new AnonymousPipeClientStream(PipeDirection.In, fromAdapter.ClientSafePipeHandle);

        await using var client = DapClient.Over(adapterIn, clientIn);
        var events = new List<string>();
        client.EventReceived += (name, _) => { lock (events) events.Add(name); };

        var adapter = Task.Run(async () =>
        {
            var request = await DapClient.ReadMessageAsync(toAdapter, default);
            var seq = request!["seq"]!.GetValue<int>();
            await Write(fromAdapter, new JsonObject { ["seq"] = 10, ["type"] = "event", ["event"] = "stopped", ["body"] = new JsonObject { ["reason"] = "breakpoint" } });
            await Write(fromAdapter, new JsonObject
            {
                ["seq"] = 11, ["type"] = "response", ["request_seq"] = seq, ["command"] = "threads", ["success"] = true,
                ["body"] = new JsonObject { ["threads"] = new JsonArray(new JsonObject { ["id"] = 7, ["name"] = "Main" }) },
            });

            var failing = await DapClient.ReadMessageAsync(toAdapter, default);
            await Write(fromAdapter, new JsonObject
            {
                ["seq"] = 12, ["type"] = "response", ["request_seq"] = failing!["seq"]!.GetValue<int>(), ["command"] = "evaluate", ["success"] = false, ["message"] = "no such variable",
            });
        });

        var body = await client.RequestAsync("threads", timeout: TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<DapException>(() => client.RequestAsync("evaluate", new JsonObject { ["expression"] = "x" }, TimeSpan.FromSeconds(5)));
        await adapter;

        Assert.Equal(7, body["threads"]![0]!["id"]!.GetValue<int>());
        Assert.Contains("no such variable", error.Message);
        lock (events) Assert.Contains("stopped", events);
    }

    static string Frame(string json) => $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";

    static async Task Write(Stream stream, JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(Frame(message.ToJsonString()));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }
}
