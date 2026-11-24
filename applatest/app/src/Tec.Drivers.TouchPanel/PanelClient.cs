using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tec.Drivers.TouchPanel;

/// <summary>触摸屏那头不答应时抛这个，Message 就是它回的那句话（「这个通道正在跑序列…」「触摸屏上关了允许远程控制…」）。</summary>
public sealed class PanelRefusedException : InvalidOperationException
{
    public PanelRefusedException(string message) : base(message) { }
}

/// <summary>
/// 和触摸屏控制核心说话的那条线：一行一条 JSON 命令，一行一条应答（协议见 hmi-cpp/core/app/core_app.cpp 开头的命令表）。
///
/// 一次只走一条命令（串行），因为应答是按顺序回的、没有编号可对。
/// 线断了不在这里重试命令——命令重发可能把同一件事做两遍（比如再开一次加料）；
/// 这里只负责下一条命令来的时候重新连。断没断、要不要报，由上面的会话按轮询结果说。
/// </summary>
internal sealed class PanelClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private NetworkStream? _stream;

    public PanelClient(string host, int port, TimeSpan? timeout = null)
    {
        _host = host;
        _port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public string Endpoint => $"{_host}:{_port}";

    /// <summary>发一条命令、等一行应答。连不上 / 超时抛 IOException；触摸屏回 ok=false 抛 PanelRefusedException。</summary>
    public async Task<JsonObject> AskAsync(JsonObject command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(_timeout);
            try
            {
                if (_tcp is null) await ConnectAsync(limit.Token).ConfigureAwait(false);
                var line = Encoding.UTF8.GetBytes(command.ToJsonString() + "\n");
                await _stream!.WriteAsync(line, limit.Token).ConfigureAwait(false);
                var reply = await _reader!.ReadLineAsync(limit.Token).ConfigureAwait(false)
                            ?? throw new IOException("触摸屏把连接断开了");
                var obj = JsonNode.Parse(reply) as JsonObject ?? throw new IOException("触摸屏回的不是一条合法的应答");
                if (obj["ok"]?.GetValue<bool>() != true)
                    throw new PanelRefusedException(obj["error"]?.GetValue<string>() ?? "触摸屏没有答应这条命令");
                return obj;
            }
            catch (PanelRefusedException) { throw; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Drop();
                throw new IOException($"触摸屏 {Endpoint} 在 {_timeout.TotalSeconds:0} 秒内没有应答");
            }
            catch (Exception ex) when (ex is SocketException or IOException or JsonException or ObjectDisposedException)
            {
                // 这条线已经说不清走到哪了（半条命令、半条应答），整条丢掉，下次重新连
                Drop();
                throw ex as IOException ?? new IOException($"连不上触摸屏 {Endpoint}：{ex.Message}", ex);
            }
        }
        finally { _gate.Release(); }
    }

    public Task<JsonObject> AskAsync(string cmd, CancellationToken ct) => AskAsync(new JsonObject { ["cmd"] = cmd }, ct);

    private async Task ConnectAsync(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new StreamReader(_stream, new UTF8Encoding(false), false, 8192, leaveOpen: true);
    }

    private void Drop()
    {
        try { _reader?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _reader = null; _stream = null; _tcp = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Drop(); }
        finally { _gate.Release(); }
        _gate.Dispose();
    }
}
