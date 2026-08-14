using System.Text;
using TecControl.Core.Protocol;

namespace TecControl.Core.Comm;

/// <summary>
/// RD105 ASCII 协议客户端：负责指令的串行化发送（同一时刻只有一条指令在途）、
/// 指令间隔控制（协议要求 ≥5ms）与应答成帧。
/// </summary>
public sealed class TecClient(ISerialTransport transport) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastCommandTick;

    /// <summary>相邻两条指令之间的最小间隔（协议示例要求 5ms，留些余量）。</summary>
    public int MinCommandGapMs { get; set; } = 10;

    /// <summary>等待应答的总超时。</summary>
    public int ReplyTimeoutMs { get; set; } = 500;

    /// <summary>应答开始后，连续这么久没有新字节就认为一帧结束（兜底 DATADEMAND 等长应答）。</summary>
    public int InterByteIdleMs { get; set; } = 60;

    /// <summary>
    /// 瞬时无应答（丢帧）的自动重试次数。大电流 PWM 开关环境下，十万条指令量级
    /// 偶发丢一帧属正常物理现象（实测一整天出现一次），不值得记为周期失败。
    /// 本协议所有 SET 均为幂等的绝对值写入，重发安全；每次发送前都会清空接收缓冲，
    /// 迟到的旧应答不会串帧。
    /// </summary>
    public int TimeoutRetries { get; set; } = 1;

    /// <summary>
    /// 坏帧（应答收到但被干扰打坏、解析失败）的自动重试次数，与 TimeoutRetries 独立。
    /// 实测现场（−30℃ 大功率段）出现单比特翻转：分隔符 '@'(0x40) 被打成 'P'(0x50)，
    /// 相邻两字段粘连成非数字文本，解析报"缺少字段"。查询与 DATADEMAND 均为只读，
    /// SET 为幂等绝对值写入，重发一次即可救回，无需记为周期失败。
    /// </summary>
    public int CorruptRetries { get; set; } = 1;

    /// <summary>
    /// 发送指令并用 parse 解析应答；应答坏帧（解析抛 TecProtocolException）时自动重发。
    /// 所有需要"发送 + 解析"的调用都应走这里，坏帧与丢帧获得一致的重试待遇。
    /// </summary>
    public async Task<T> ExecParsedAsync<T>(string command, Func<string, T> parse,
        CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var reply = await ExecAsync(command, ct).ConfigureAwait(false);
            try
            {
                return parse(reply);
            }
            catch (TecProtocolException) when (attempt < CorruptRetries)
            {
            }
        }
    }

    public Task<long> QueryAsync(int? channel, string name, CancellationToken ct = default) =>
        ExecParsedAsync(TecAscii.BuildQuery(channel, name),
            reply => TecAscii.GetField(reply, channel, name), ct);

    /// <summary>查询文本型字段（如型号 TEC 实测返回 "215L" 这类非数字值）。</summary>
    public Task<string> QueryTextAsync(int? channel, string name, CancellationToken ct = default) =>
        ExecParsedAsync(TecAscii.BuildQuery(channel, name),
            reply => TecAscii.GetTextField(reply, channel, name), ct);

    public Task SetAsync(int? channel, string name, long value, CancellationToken ct = default) =>
        ExecParsedAsync<object?>(TecAscii.BuildSet(channel, name, value), reply =>
        {
            // 回读不符也走坏帧重试：若是应答被打坏，重发即恢复；
            // 若设备确实拒写/钳位，重发后回读依旧不符，照常抛出。
            var echoed = TecAscii.GetField(reply, channel, name);
            if (echoed != value)
                throw new TecProtocolException(
                    $"设置 {TecAscii.FieldKey(channel, name)} 失败：写入 {value}，设备回读 {echoed}");
            return null;
        }, ct);

    /// <summary>发送一条原始指令并返回完整应答文本（瞬时无应答自动重试）。</summary>
    public async Task<string> ExecAsync(string command, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var sinceLast = Environment.TickCount64 - _lastCommandTick;
                if (sinceLast < MinCommandGapMs)
                    await Task.Delay((int)(MinCommandGapMs - sinceLast), ct).ConfigureAwait(false);

                try
                {
                    return await Task.Run(() => ExecCore(command), ct).ConfigureAwait(false);
                }
                catch (TimeoutException) when (attempt < TimeoutRetries)
                {
                    _lastCommandTick = Environment.TickCount64;   // 重试同样遵守指令间隔
                }
            }
        }
        finally
        {
            _lastCommandTick = Environment.TickCount64;
            _gate.Release();
        }
    }

    private string ExecCore(string command)
    {
        transport.DiscardInput();
        var tx = Encoding.ASCII.GetBytes(command);
        transport.Write(tx, 0, tx.Length);

        var buffer = new byte[4096];
        var received = new StringBuilder();
        var deadline = Environment.TickCount64 + ReplyTimeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var n = transport.Read(buffer, 0, buffer.Length, InterByteIdleMs);
            if (n > 0)
            {
                received.Append(Encoding.ASCII.GetString(buffer, 0, n));
                // 正常应答以 \r\n 结束
                if (received.Length >= 2 &&
                    received[^2] == '\r' && received[^1] == '\n')
                    break;
            }
            else if (received.Length > 0)
            {
                // 已收到数据且线路空闲超过 InterByteIdleMs，视为一帧结束
                break;
            }
        }

        if (received.Length == 0)
            throw new TimeoutException($"指令无应答：{TecAscii.Printable(command)}");

        return received.ToString();
    }

    public void Dispose() => _gate.Dispose();
}
