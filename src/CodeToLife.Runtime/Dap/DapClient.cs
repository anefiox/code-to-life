using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeToLife.Runtime.Dap;

/// <summary>
/// A minimal Debug Adapter Protocol client: starts an adapter (netcoredbg) and talks to it over
/// stdin/stdout with <c>Content-Length</c> framed JSON, the same wire format VS Code uses.
/// </summary>
public sealed class DapClient : IAsyncDisposable
{
    readonly Process _process;
    readonly Stream _input;
    readonly Stream _output;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _stop = new();
    readonly Task _readLoop;
    int _seq;

    /// <summary>Raised for every DAP event (stopped, output, terminated...), on the reader thread.</summary>
    public event Action<string, JsonObject>? EventReceived;

    /// <summary>Raised once when the adapter goes away, for whatever reason.</summary>
    public event Action<string>? Closed;

    public bool IsRunning => !_process.HasExited;

    DapClient(Process process)
    {
        _process = process;
        _input = process.StandardInput.BaseStream;
        _output = process.StandardOutput.BaseStream;
        _readLoop = Task.Run(ReadLoop);
        _ = Task.Run(async () =>
        {
            // netcoredbg writes diagnostics to stderr; drain it so it can't block.
            try { while (await process.StandardError.ReadLineAsync() is not null) { } } catch { }
        });
    }

    public static DapClient Start(string adapterPath, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(adapterPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start the debug adapter at '{adapterPath}'.");
        return new DapClient(process);
    }

    /// <summary>For tests: speak DAP over any pair of streams.</summary>
    internal static DapClient Over(Stream toAdapter, Stream fromAdapter) => new(toAdapter, fromAdapter);

    DapClient(Stream toAdapter, Stream fromAdapter)
    {
        _process = new Process();
        _input = toAdapter;
        _output = fromAdapter;
        _readLoop = Task.Run(ReadLoop);
    }

    public async Task<JsonObject> RequestAsync(string command, JsonObject? arguments = null, TimeSpan? timeout = null)
    {
        var seq = Interlocked.Increment(ref _seq);
        var message = new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command };
        if (arguments is not null) message["arguments"] = arguments;

        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = completion;
        await SendAsync(message);

        using var timer = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        await using var _ = timer.Token.Register(() => completion.TrySetException(new TimeoutException($"The debugger didn't answer '{command}' in time.")));
        var response = await completion.Task;
        if (response["success"]?.GetValue<bool>() != true)
            throw new DapException(command, response["message"]?.GetValue<string>() ?? response["body"]?["error"]?["format"]?.GetValue<string>() ?? "request failed");
        return response["body"] as JsonObject ?? [];
    }

    async Task SendAsync(JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync();
        try
        {
            await _input.WriteAsync(header);
            await _input.WriteAsync(body);
            await _input.FlushAsync();
        }
        finally { _writeLock.Release(); }
    }

    async Task ReadLoop()
    {
        var reason = "the debugger exited";
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(_output, _stop.Token);
                if (message is null) break;
                Dispatch(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or JsonException)
        {
            reason = ex is OperationCanceledException ? "stopped" : ex.Message;
        }
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("The debugger connection closed."));
        Closed?.Invoke(reason);
    }

    void Dispatch(JsonObject message)
    {
        switch (message["type"]?.GetValue<string>())
        {
            case "response":
                var requestSeq = message["request_seq"]?.GetValue<int>() ?? -1;
                if (_pending.TryRemove(requestSeq, out var completion)) completion.TrySetResult(message);
                break;
            case "event":
                var name = message["event"]?.GetValue<string>() ?? "";
                try { EventReceived?.Invoke(name, message["body"] as JsonObject ?? []); }
                catch { /* a bad listener mustn't kill the connection */ }
                break;
            case "request":
                // Reverse requests (runInTerminal, startDebugging): we don't support them, say so.
                _ = SendAsync(new JsonObject
                {
                    ["seq"] = Interlocked.Increment(ref _seq),
                    ["type"] = "response",
                    ["request_seq"] = message["seq"]?.GetValue<int>() ?? 0,
                    ["command"] = message["command"]?.GetValue<string>(),
                    ["success"] = false,
                    ["message"] = "not supported",
                });
                break;
        }
    }

    /// <summary>Reads one <c>Content-Length</c> framed message. Null at end of stream.</summary>
    internal static async Task<JsonObject?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = -1;
        while (true)
        {
            var line = await ReadHeaderLineAsync(stream, cancellationToken);
            if (line is null) return null;
            if (line.Length == 0)
            {
                if (length >= 0) break;
                continue; // stray blank line before a header
            }
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[(colon + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken);
            if (n == 0) return null;
            read += n;
        }
        return JsonNode.Parse(buffer) as JsonObject;
    }

    static async Task<string?> ReadHeaderLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one, cancellationToken);
            if (n == 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString([.. bytes]);
            if (one[0] == '\n')
            {
                if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
                return Encoding.ASCII.GetString([.. bytes]);
            }
            bytes.Add(one[0]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            if (_process.Id > 0 && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(new CancellationTokenSource(2000).Token);
            }
        }
        catch { }
        try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _process.Dispose();
    }
}

public sealed class DapException(string command, string message) : Exception($"{command}: {message}");
