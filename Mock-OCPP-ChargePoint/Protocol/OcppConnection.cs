using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace MockOcpp.Protocol;

public sealed class OcppConnectionOptions
{
    public string SubProtocol { get; init; } = "ocpp1.6";
    public string? BasicAuthUser { get; init; }
    public string? BasicAuthPassword { get; init; }
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(5);
    public bool Trace { get; set; }
}

/// <summary>
/// The OCPP-J transport for one charge point: a resilient WebSocket plus the
/// "one CALL outstanding per direction" queue the spec mandates. Outbound calls
/// are funnelled through a channel and sent one at a time; a CALL from the CSMS
/// is handed to <see cref="OnCall"/> and its answer written straight back.
/// </summary>
public sealed class OcppConnection : IAsyncDisposable
{
    private readonly Uri _uri;
    private readonly OcppConnectionOptions _opt;
    private readonly Channel<Outbound> _outbox = Channel.CreateUnbounded<Outbound>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcFrame>> _pending = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _runLoop;
    private long _idCounter;

    public OcppConnection(string baseUrl, string chargePointId, OcppConnectionOptions options)
    {
        _opt = options;
        var trimmed = baseUrl.TrimEnd('/');
        _uri = new Uri($"{trimmed}/{Uri.EscapeDataString(chargePointId)}");
        ChargePointId = chargePointId;
    }

    public string ChargePointId { get; }
    public bool IsConnected { get; private set; }

    /// <summary>Inbound CS-&gt;CP CALL handler. Return the CALLRESULT payload or a CALLERROR.</summary>
    public Func<RpcFrame, Task<CallResult>>? OnCall { get; set; }

    public event Action<bool>? ConnectionChanged;
    public event Action<string>? Log;
    public event Action<bool, string>? Trace;   // (outbound, rawJson)

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _runLoop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_runLoop is not null)
        {
            try { await _runLoop; } catch { /* shutting down */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _ws?.Dispose();
        _cts?.Dispose();
    }

    // --- outbound ----------------------------------------------------------

    /// <summary>Enqueue a CP-&gt;CS CALL. Completes with the CALLRESULT payload,
    /// or throws <see cref="OcppCallException"/> on a CALLERROR.</summary>
    public Task<JsonObject> SendCallAsync(string action, JsonObject payload, bool transactional = false)
    {
        var item = new Outbound(action, payload, transactional);
        _outbox.Writer.TryWrite(item);
        return item.Completion.Task;
    }

    private sealed record Outbound(string Action, JsonObject Payload, bool Transactional)
    {
        public TaskCompletionSource<JsonObject> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; set; }
    }

    // --- main loop -------------------------------------------------------

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(ct);
                var receive = ReceiveLoopAsync(ct);
                var pump = PumpLoopAsync(ct);
                await Task.WhenAny(receive, pump);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"connection error: {ex.Message}");
            }

            SetConnected(false);
            FailAllPending("socket closed");
            if (ct.IsCancellationRequested) break;

            Log?.Invoke($"reconnecting in {_opt.ReconnectDelay.TotalSeconds:0}s");
            try { await Task.Delay(_opt.ReconnectDelay, ct); }
            catch (OperationCanceledException) { break; }
        }

        try
        {
            if (_ws is { State: WebSocketState.Open })
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { /* ignore */ }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.AddSubProtocol(_opt.SubProtocol);
        if (!string.IsNullOrEmpty(_opt.BasicAuthUser))
        {
            var raw = $"{_opt.BasicAuthUser}:{_opt.BasicAuthPassword}";
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            _ws.Options.SetRequestHeader("Authorization", "Basic " + b64);
        }

        Log?.Invoke($"connecting to {_uri}");
        await _ws.ConnectAsync(_uri, ct);
        SetConnected(true);
        Log?.Invoke($"connected (subprotocol {_ws.SubProtocol ?? "?"})");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var sb = new StringBuilder();
        while (_ws is { State: WebSocketState.Open } && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(buffer, ct);
            }
            catch (Exception) { break; }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Log?.Invoke("server closed the socket");
                break;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage)
                continue;

            var text = sb.ToString();
            sb.Clear();
            _ = DispatchAsync(text, ct);
        }
    }

    private async Task DispatchAsync(string text, CancellationToken ct)
    {
        Trace?.Invoke(false, text);
        if (!RpcFrame.TryParse(text, out var frame, out var err))
        {
            Log?.Invoke($"dropping malformed frame: {err}");
            if (!string.IsNullOrEmpty(frame.UniqueId))
                await SendRawAsync(RpcFrame.Error(frame.UniqueId, RpcErrorCode.FormationViolation, err), ct);
            return;
        }

        switch (frame.Type)
        {
            case MessageType.CallResult:
            case MessageType.CallError:
                if (_pending.TryRemove(frame.UniqueId, out var tcs))
                    tcs.TrySetResult(frame);
                else
                    Log?.Invoke($"unmatched {frame.Type} id={frame.UniqueId}");
                break;

            case MessageType.Call:
                await HandleInboundCallAsync(frame, ct);
                break;
        }
    }

    private async Task HandleInboundCallAsync(RpcFrame frame, CancellationToken ct)
    {
        CallResult outcome;
        try
        {
            outcome = OnCall is null
                ? CallResult.Fail(RpcErrorCode.NotImplemented, $"{frame.Action} not handled")
                : await OnCall(frame);
        }
        catch (Exception ex)
        {
            outcome = CallResult.Fail(RpcErrorCode.InternalError, ex.Message);
        }

        var reply = outcome.Ok
            ? RpcFrame.Result(frame.UniqueId, outcome.Payload)
            : RpcFrame.Error(frame.UniqueId, outcome.ErrorCode, outcome.ErrorDescription);
        await SendRawAsync(reply, ct);
    }

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
        {
            Outbound item;
            try { item = await _outbox.Reader.ReadAsync(ct); }
            catch (OperationCanceledException) { break; }

            await SendOneAsync(item, ct);
        }
    }

    private async Task SendOneAsync(Outbound item, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            item.Attempts++;
            var uid = Interlocked.Increment(ref _idCounter).ToString();
            var tcs = new TaskCompletionSource<RpcFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[uid] = tcs;

            try
            {
                await SendRawAsync(RpcFrame.Call(uid, item.Action, item.Payload), ct);
            }
            catch (Exception ex)
            {
                _pending.TryRemove(uid, out _);
                // Re-queue transactional messages; drop the rest.
                if (item.Transactional && !ct.IsCancellationRequested)
                {
                    _outbox.Writer.TryWrite(item);
                    return;
                }
                item.Completion.TrySetException(ex);
                return;
            }

            var done = await Task.WhenAny(tcs.Task, Task.Delay(_opt.CallTimeout, ct));
            if (done == tcs.Task)
            {
                _pending.TryRemove(uid, out _);
                var frame = tcs.Task.Result;
                if (frame.Type == MessageType.CallError)
                    item.Completion.TrySetException(
                        new OcppCallException(item.Action, frame.ErrorCode, frame.ErrorDescription));
                else
                    item.Completion.TrySetResult(frame.Payload);
                return;
            }

            // timed out
            _pending.TryRemove(uid, out _);
            if (item.Attempts >= _opt.MaxAttempts && !item.Transactional)
            {
                item.Completion.TrySetException(
                    new OcppCallException(item.Action, RpcErrorCode.GenericError, "no response from CSMS"));
                return;
            }
            Log?.Invoke($"{item.Action} timed out, retry {item.Attempts}");
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private async Task SendRawAsync(string text, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open })
            throw new InvalidOperationException("socket not open");

        await _sendGate.WaitAsync(ct);
        try
        {
            Trace?.Invoke(true, text);
            await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionChanged?.Invoke(value);
    }

    private void FailAllPending(string reason)
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(new OcppCallException("(pending)", RpcErrorCode.GenericError, reason));
        }
    }

    /// <summary>Force a disconnect/reconnect cycle (used by Reset and the REPL).</summary>
    public void Reconnect()
    {
        try { _ws?.Abort(); } catch { /* ignore */ }
    }
}
