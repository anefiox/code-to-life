using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace CodeToLife;

/// <summary>Pushes events to every open browser tab over Server-Sent Events.</summary>
public sealed class EventHub
{
    readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();
    readonly JsonSerializerOptions _json;

    public EventHub(JsonSerializerOptions json) => _json = json;

    public void Publish(string type, object payload)
    {
        var data = JsonSerializer.Serialize(payload, _json);
        var message = $"event: {type}\ndata: {data}\n\n";
        foreach (var client in _clients.Values) client.Writer.TryWrite(message);
    }

    public async Task StreamAsync(HttpContext context)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        var id = Guid.NewGuid();
        // A slow tab drops old live samples rather than making the server buffer forever.
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        _clients[id] = channel;
        try
        {
            await context.Response.WriteAsync("event: hello\ndata: {}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var beat = heartbeat.WaitForNextTickAsync(context.RequestAborted).AsTask();
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var read = channel.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
                if (await Task.WhenAny(read, beat) == beat)
                {
                    await context.Response.WriteAsync(": ping\n\n", context.RequestAborted);
                    beat = heartbeat.WaitForNextTickAsync(context.RequestAborted).AsTask();
                }
                else if (!await read) break;
                while (channel.Reader.TryRead(out var message))
                    await context.Response.WriteAsync(message, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        finally { _clients.TryRemove(id, out _); }
    }
}
