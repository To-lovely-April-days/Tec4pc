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
    /// 【本地改动】按时长重试（ms）：大于 0 时不看 TimeoutRetries / CorruptRetries 的次数——
    /// 应答不对（无应答 / 解析失败）就整帧丢掉、清缓冲、重发同一条指令，直到拿到一帧解析得了的，
    /// 或者从第一次发出起满这么久才认输（用户定的：RD105 = 2000）。最后一次的等待也截在期限上。
    /// 期限从第一次发出算起，底下 Modbus 桥自己按时长重试用掉的时间也算在里面——两层不叠。
    /// 0 = 按次数（原来的行为）。
    /// </summary>
    public int RetryBudgetMs { get; set; }

    /// <summary>重发前歇一下（只在按时长重试时用）：被干扰打坏的那一拍别紧跟着又撞上。</summary>
    public int RetryBackoffMs { get; set; } = 20;

    private long BudgetEnd() => RetryBudgetMs > 0 ? Environment.TickCount64 + RetryBudgetMs : long.MaxValue;

    /// <summary>还重不重发：按时长时期限里还够发一问、等一小会儿；按次数时看次数。</summary>
    private bool Retry(int attempt, int maxByCount, long budgetEnd)
        => RetryBudgetMs > 0
            ? budgetEnd - Environment.TickCount64 > MinCommandGapMs + RetryBackoffMs + 20
            : attempt < maxByCount;

    /// <summary>
    /// 发送指令并用 parse 解析应答；应答坏帧（解析抛 TecProtocolException）时自动重发。
    /// 所有需要"发送 + 解析"的调用都应走这里，坏帧与丢帧获得一致的重试待遇。
    /// </summary>
    public Task<T> ExecParsedAsync<T>(string command, Func<string, T> parse,
        CancellationToken ct = default)
        => ExecParsedAsync(command, parse, BudgetEnd(), ct);

    private async Task<T> ExecParsedAsync<T>(string command, Func<string, T> parse, long budgetEnd,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var reply = await ExecAsync(command, budgetEnd, ct).ConfigureAwait(false);
            try
            {
                return parse(reply);
            }
            catch (TecProtocolException) when (Retry(attempt, CorruptRetries, budgetEnd))
            {
                if (RetryBudgetMs > 0) await Task.Delay(RetryBackoffMs, ct).ConfigureAwait(false);
            }
            catch (TecProtocolException ex) when (RetryBudgetMs > 0 && attempt > 0)
            {
                throw new TecProtocolException($"{ex.Message}（{RetryBudgetMs} ms 内共发 {attempt + 1} 遍都没拿到对的应答）");
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

    public async Task SetAsync(int? channel, string name, long value, CancellationToken ct = default)
    {
        // 【本地改动】回读不符跟坏帧分开算：解析不了的坏帧按时长重发（ExecParsedAsync 里）；
        // 解析得了、只是值不符——帧是好的，设备真的拒写/钳位的可能大——只再发一次，
        // 仍不符照常抛。不然一次真钳位要在总线上空打满 2 秒
        var cmd = TecAscii.BuildSet(channel, name, value);
        var echoed = await ExecParsedAsync(cmd, reply => TecAscii.GetField(reply, channel, name), ct).ConfigureAwait(false);
        if (echoed == value) return;
        echoed = await ExecParsedAsync(cmd, reply => TecAscii.GetField(reply, channel, name), ct).ConfigureAwait(false);
        if (echoed != value)
            throw new TecProtocolException(
                $"设置 {TecAscii.FieldKey(channel, name)} 失败：写入 {value}，设备回读 {echoed}");
    }

    /// <summary>
    /// 【本地改动】写、然后把设备回显（实际存下）的值原样带回来，回显不符**不抛**。控温回路写 PWMDUTY 用它：
    /// 现场 RD105 在 MODE=3 下会按自己的规矩改写小占空比（写 −4.41 % 回显 −6.43 %），那不是通信坏了，是设备的意思——
    /// 照它的算、记下来，别当断链停控（从前回显不符 = 坏帧 → 重发 → 仍不符 → 抛 → 连着 20 拍就停控，现场撞上了）。
    /// 坏帧照旧重发。
    /// </summary>
    public Task<long> SetEchoAsync(int? channel, string name, long value, CancellationToken ct = default) =>
        ExecParsedAsync(TecAscii.BuildSet(channel, name, value),
            reply => TecAscii.GetField(reply, channel, name), ct);

    /// <summary>发送一条原始指令并返回完整应答文本（瞬时无应答自动重试）。</summary>
    public Task<string> ExecAsync(string command, CancellationToken ct = default)
        => ExecAsync(command, BudgetEnd(), ct);

    private async Task<string> ExecAsync(string command, long budgetEnd, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 【本地改动】重发整个在工作线程里同步转完：丢掉的那几帧的异常不穿过 Task.Run，
            // VS 调试时不再每丢一帧弹一次「用户未处理的异常」（明明下一遍就救回来了）。
            // 真认输的那一个也在线程池里先接住、回到异步上下文再抛——直接穿过 Task.Run 抛，
            // VS 照样在抛出点误报「用户未处理」（线程池那半段栈上看不到调用方的 catch）
            string? reply = null;
            Exception? error = null;
            await Task.Run(() =>
            {
                try { reply = ExecWithRetry(command, budgetEnd, ct); }
                catch (Exception ex) { error = ex; }
            }, ct).ConfigureAwait(false);
            if (error is not null) throw error;
            return reply!;
        }
        finally
        {
            _lastCommandTick = Environment.TickCount64;
            _gate.Release();
        }
    }

    private string ExecWithRetry(string command, long budgetEnd, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var sinceLast = Environment.TickCount64 - _lastCommandTick;
            if (sinceLast < MinCommandGapMs) Thread.Sleep((int)(MinCommandGapMs - sinceLast));

            try
            {
                return ExecCore(command, budgetEnd);
            }
            catch (TimeoutException) when (Retry(attempt, TimeoutRetries, budgetEnd))
            {
                _lastCommandTick = Environment.TickCount64;   // 重试同样遵守指令间隔
                if (RetryBudgetMs > 0) Thread.Sleep(RetryBackoffMs);
            }
            catch (TimeoutException ex) when (RetryBudgetMs > 0 && attempt > 0)
            {
                throw new TimeoutException($"{ex.Message}（{RetryBudgetMs} ms 内共发 {attempt + 1} 遍都没拿到对的应答）", ex);
            }
        }
    }

    private string ExecCore(string command, long budgetEnd)
    {
        transport.DiscardInput();
        var tx = Encoding.ASCII.GetBytes(command);
        transport.Write(tx, 0, tx.Length);

        var buffer = new byte[4096];
        var received = new StringBuilder();
        // 等应答也截在按时长重试的期限上（期限里至少还给 InterByteIdleMs 收一拍）
        var deadline = Math.Min(Environment.TickCount64 + ReplyTimeoutMs,
                                Math.Max(budgetEnd, Environment.TickCount64 + InterByteIdleMs));

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
