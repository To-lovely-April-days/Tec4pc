using TecControl.Core.Comm;

namespace Tec.Drivers.Rd105.Modbus;

/// <summary>
/// Modbus RTU 主站，问答式。IO8R（线圈/离散输入）和宇电两台（保持寄存器）共用这一层，
/// 各自的语义留在各自的客户端里，这里只管把一问一答走对。
///
/// 建在 ISerialTransport 上，理由同 Rd105Link：换成假串口就能在没有硬件的情况下
/// 把整条链路跑一遍。RTU 的帧边界靠 3.5 字符静默，但主站这边不用去数静默——
/// 应答长度由功能码决定，按长度读满即可；每次发问前清一次接收缓冲，
/// 上一问超时后迟到的应答不会混进这一问。丢帧 / 坏帧在这一层自动重发
/// （TimeoutRetries / CorruptRetries），偶发干扰不惊动上层。
/// </summary>
public sealed class ModbusRtuClient
{
    private readonly ISerialTransport _transport;
    private readonly SemaphoreSlim _lock;               // 半双工总线，一次只许一问
    private readonly int _timeoutMs;

    /// <param name="busLock">共口时传进来的总线锁：宇电两台导轨拼接后 485 自动并联
    /// （手册 §3.3），一条串口上挂两个从站，两个客户端必须共用一把锁，
    /// 否则一问未答又来一问，应答就串台了。不共口就不传，各用各的。</param>
    public ModbusRtuClient(ISerialTransport transport, byte station, int timeoutMs = 500,
                           SemaphoreSlim? busLock = null)
    {
        if (station is 0 or > 247)
            throw new ArgumentOutOfRangeException(nameof(station),
                $"Modbus 从站地址要在 1~247，给的是 {station}（0 是广播，收不到应答）");
        _transport = transport;
        Station = station;
        _timeoutMs = Math.Max(50, timeoutMs);
        _lock = busLock ?? new SemaphoreSlim(1, 1);
    }

    public byte Station { get; }

    /// <summary>应答前面最多容忍几个脏字节（485 收发切换的毛刺）。见 ReadResponse。</summary>
    public int MaxLeadingNoise { get; set; } = 4;

    /// <summary>
    /// 瞬时无应答（丢帧 / 应答烂尾没等齐）的自动重试次数。485 链路上大电流 PWM 开关
    /// 带来的干扰是常态——偶发一帧就差 CRC 两字节没到（现场抓到）属正常物理现象，
    /// 重发一次即恢复，不值得报给上层记周期失败。本层所有写都是幂等的绝对值写入
    /// （线圈开/关、寄存器写定值），超时重发安全；重发前重新留帧间空当并清接收缓冲，
    /// 上一答迟到的字节不会串进这一答。
    /// </summary>
    public int TimeoutRetries { get; set; } = 2;

    /// <summary>
    /// 坏帧（CRC 不过、站号/功能码/数据长度不对——应答被干扰打坏或上一问串了帧）的
    /// 自动重试次数，与 TimeoutRetries 独立。从站回了异常码（Code 有值）不重试：
    /// 那是配置/语义错，重发一百遍也是一样。
    /// </summary>
    public int CorruptRetries { get; set; } = 2;

    /// <summary>
    /// 应答开始收到字节后，连续这么久没有新字节就认定这一帧烂尾——不等满总超时。
    /// 9600 下相邻两字节约 1 ms，这个余量已是两个量级；超时判死要快：烂尾帧多是
    /// 485 干扰掐的，早死早重发，比干等满 500 ms 总超时快好几倍（慢的主因就是它）。
    /// 报错措辞与总超时不同：「烂尾」= 帧来了但被掐断；「没等到」= 应答整体来得晚/没来。
    /// </summary>
    public int InterByteIdleMs { get; set; } = 100;

    /// <summary>
    /// 重发前的额外等待（ms）。超时多半是**从站应答来得晚**：话音还在路上我们就重发，
    /// 会跟它迟到的应答在半双工总线上撞车——两边都打成烂帧，越试越烂、烂的位置
    /// 还都差不多。重发前等够一拍，让它把话说完、DiscardInput 把迟到字节收走。
    /// </summary>
    public int RetryBackoffMs { get; set; } = 200;

    /// <summary>
    /// 按时长重试（ms）：大于 0 时不看 TimeoutRetries / CorruptRetries 的次数——一问的应答
    /// 不对（超时 / 烂尾 / CRC 不过 / 站号功能码长度不对）就整帧丢掉、清缓冲、重发同一帧，
    /// 直到收到一帧校验通过的，或者从第一次发问起满这么久才认输（用户定的：RD105 = 2000）。
    /// 最后一次的等待也截在这个期限上，整问不会超过它。0 = 按次数（缺省，IO8R / 宇电照旧）。
    /// 从站回了异常码照旧不重试。
    /// </summary>
    public int RetryBudgetMs { get; set; }

    /// <summary>
    /// 上层整条指令的期限（Environment.TickCount64 刻度）：RD105 桥把一条 ASCII 指令拆成几帧
    /// （写完回读、DATADEMAND 五段读），按时长重试时几帧共用这一个期限，不是每帧各给 2 s。null = 不限。
    /// </summary>
    public long? CommandDeadline { get; set; }

    // ── 读 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 在后台线程跑一问一答，异常先在线程池里接住、回到异步上下文再抛——
    /// 让异常直接穿过 Task.Run 的话，VS 的「用户未处理」会在抛出点断下来
    /// （线程池那半段栈上看不到调用方的 catch），调试时被通信异常打断个没完。
    /// 接住再抛：调用方的 catch 语义不变，调试器不再误报（下面两条同步版注释记的也是这件事）。
    /// </summary>
    private static async Task<T> GuardAsync<T>(Func<T> work, CancellationToken ct)
    {
        Exception? error = null;
        T? result = default;
        await Task.Run(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        }, ct).ConfigureAwait(false);
        if (error is not null) throw error;
        return result!;
    }

    private static async Task GuardAsync(Action work, CancellationToken ct)
    {
        Exception? error = null;
        await Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { error = ex; }
        }, ct).ConfigureAwait(false);
        if (error is not null) throw error;
    }

    /// <summary>FC 01 读线圈（IO8R 的继电器 DO）。位序低位在前：第 0 点在首字节 bit0。</summary>
    public Task<bool[]> ReadCoilsAsync(ushort start, int count, CancellationToken ct = default)
        => GuardAsync(() => ReadBits(0x01, start, count, ct), ct);

    /// <summary>FC 02 读离散输入（IO8R 的光耦 DI）。</summary>
    public Task<bool[]> ReadDiscreteInputsAsync(ushort start, int count, CancellationToken ct = default)
        => GuardAsync(() => ReadBits(0x02, start, count, ct), ct);

    /// <summary>FC 03 读保持寄存器，字内大端。宇电整张表都从这里读。</summary>
    public Task<ushort[]> ReadHoldingRegistersAsync(ushort start, int count, CancellationToken ct = default)
        => GuardAsync(() => ReadRegs(0x03, start, count, ct), ct);

    /// <summary>FC 04 读输入寄存器（只读量）。冷水机的运行状态 / 继电器状态按协议表走这个。</summary>
    public Task<ushort[]> ReadInputRegistersAsync(ushort start, int count, CancellationToken ct = default)
        => GuardAsync(() => ReadRegs(0x04, start, count, ct), ct);

    /// <summary>
    /// 同步版（RD105 桥用：它本来就在 TecClient 的工作线程上同步跑）。不套 Task.Run——
    /// 套了的话这一读认输时异常要穿过线程池，VS 调试时会当「用户未处理」弹出来，哪怕上面接住了
    /// </summary>
    internal ushort[] ReadHoldingRegisters(ushort start, int count) => ReadRegs(0x03, start, count, CancellationToken.None);

    /// <summary>最后一次尝试收到了几个字节：&gt; 0 = 从站在答话（只是答得不对），0 = 一声没吭。</summary>
    public int LastResponseBytes => _rxTrace.Count;

    // ── 写 ─────────────────────────────────────────────────────────────

    /// <summary>FC 05 写单个线圈。协议只有 FF00（闭合）/ 0000（断开）两个值。</summary>
    public Task WriteCoilAsync(ushort address, bool on, CancellationToken ct = default)
        => GuardAsync(() =>
        {
            var value = on ? 0xFF00 : 0x0000;
            var body = Transact(0x05, new[] { Hi(address), Lo(address), Hi(value), Lo(value) }, ct);
            VerifyEcho(address, value, body);
        }, ct);

    /// <summary>FC 06 写单个保持寄存器。</summary>
    public Task WriteRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
        => GuardAsync(() =>
        {
            var body = Transact(0x06, new[] { Hi(address), Lo(address), Hi(value), Lo(value) }, ct);
            VerifyEcho(address, value, body);
        }, ct);

    /// <summary>FC 0F 写多个线圈。一帧落全部点位——安全场合「全断」就该是一帧，不是八帧。</summary>
    public Task WriteCoilsAsync(ushort start, IReadOnlyList<bool> values, CancellationToken ct = default)
        => GuardAsync(() =>
        {
            if (values.Count is < 1 or > 1968)
                throw new ArgumentOutOfRangeException(nameof(values), $"一次写 1~1968 个线圈，给了 {values.Count}");
            var nBytes = (values.Count + 7) / 8;
            var payload = new byte[5 + nBytes];
            payload[0] = Hi(start); payload[1] = Lo(start);
            payload[2] = Hi(values.Count); payload[3] = Lo(values.Count);
            payload[4] = (byte)nBytes;
            for (var i = 0; i < values.Count; i++)
                if (values[i]) payload[5 + i / 8] |= (byte)(1 << i % 8);
            var body = Transact(0x0F, payload, ct);
            VerifyEcho(start, values.Count, body);
        }, ct);

    /// <summary>FC 10 写多个保持寄存器。宇电限一帧 16 个，这个上限由它的客户端把，不在这层写死。</summary>
    public Task WriteRegistersAsync(ushort start, IReadOnlyList<ushort> values, CancellationToken ct = default)
        => GuardAsync(() => WriteRegs(start, values, ct), ct);

    /// <summary>同步版（RD105 桥用，理由同 ReadHoldingRegisters）。</summary>
    internal void WriteRegisters(ushort start, IReadOnlyList<ushort> values) => WriteRegs(start, values, CancellationToken.None);

    private void WriteRegs(ushort start, IReadOnlyList<ushort> values, CancellationToken ct)
    {
        if (values.Count is < 1 or > 123)
            throw new ArgumentOutOfRangeException(nameof(values), $"一次写 1~123 个寄存器，给了 {values.Count}");
        var payload = new byte[5 + values.Count * 2];
        payload[0] = Hi(start); payload[1] = Lo(start);
        payload[2] = Hi(values.Count); payload[3] = Lo(values.Count);
        payload[4] = (byte)(values.Count * 2);
        for (var i = 0; i < values.Count; i++)
        {
            payload[5 + i * 2] = Hi(values[i]);
            payload[6 + i * 2] = Lo(values[i]);
        }
        var body = Transact(0x10, payload, ct);
        VerifyEcho(start, values.Count, body);
    }

    // ── 一问一答 ───────────────────────────────────────────────────────

    private bool[] ReadBits(byte fc, ushort start, int count, CancellationToken ct)
    {
        if (count is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(count), $"一次读 1~2000 个位，要了 {count}");
        var body = Transact(fc, new[] { Hi(start), Lo(start), Hi(count), Lo(count) }, ct, (count + 7) / 8);
        var bits = new bool[count];
        for (var i = 0; i < count; i++)
            bits[i] = (body[1 + i / 8] >> i % 8 & 1) != 0;
        return bits;
    }

    private ushort[] ReadRegs(byte fc, ushort start, int count, CancellationToken ct)
    {
        if (count is < 1 or > 125)
            throw new ArgumentOutOfRangeException(nameof(count), $"一次读 1~125 个寄存器，要了 {count}");
        var body = Transact(fc, new[] { Hi(start), Lo(start), Hi(count), Lo(count) }, ct, count * 2);
        var regs = new ushort[count];
        for (var i = 0; i < count; i++)
            regs[i] = (ushort)(body[1 + i * 2] << 8 | body[2 + i * 2]);
        return regs;
    }

    /// <summary>
    /// 发一帧、收一帧。返回应答里去掉站号/功能码/CRC 的正文。
    /// 丢帧（超时）与坏帧按 TimeoutRetries / CorruptRetries 自动重发——重发走的还是
    /// 同一条「留空当 → 清缓冲 → 发问」流程，上一答迟到的字节不会混进这一答。
    /// </summary>
    private byte[] Transact(byte fc, byte[] payload, CancellationToken ct, int? expectedDataBytes = null)
    {
        _lock.Wait(ct);
        try
        {
            var frame = new byte[2 + payload.Length + 2];
            frame[0] = Station;
            frame[1] = fc;
            Array.Copy(payload, 0, frame, 2, payload.Length);
            ModbusCrc.Append(frame);

            // 按时长重试的期限：从第一次发问算起（RetryBudgetMs = 0 时不用）；
            // 上层给了整条指令的期限（RD105 桥：一条 ASCII 指令拆成几帧，几帧合起来不超过 2 s）就取早的
            var budgetEnd = RetryBudgetMs > 0 ? Environment.TickCount64 + RetryBudgetMs : long.MaxValue;
            if (RetryBudgetMs > 0 && CommandDeadline is { } cd) budgetEnd = Math.Min(budgetEnd, cd);
            for (var attempt = 0; ; attempt++)
            {
                // RTU 靠 3.5 个字符的静默分帧：上一答刚收完就发下一问，从站可能还没把上一帧
                // 收尾，这一问就丢了。RD105 桥上一条 ASCII 指令会连发两三帧（写完回读、
                // DATADEMAND 三段读），这里按帧统一留空当；协议附录的示例每帧前也 Sleep(5)
                var since = Environment.TickCount64 - _lastFrameEnd;
                if (since < InterFrameGapMs) Thread.Sleep((int)(InterFrameGapMs - since));

                _transport.DiscardInput();          // 上一问（含上一次重试）超时后迟到的字节别混进这一答
                _transport.Write(frame, 0, frame.Length);
                _lastWriteAt = Environment.TickCount64;
                _firstByteMs = -1;
                _rxTrace.Clear();
                try { return ReadResponse(fc, ct, expectedDataBytes, budgetEnd); }
                // 重试只救链路层的错；从站回了异常码（Code 有值）是配置/语义错，重发无用。
                // 重发前先等一拍：超时常是从站应答来得晚，立刻重发会跟它迟到的应答
                // 在半双工总线上撞车——两边都打成烂帧，越试越烂
                catch (TimeoutException) when (Retry(attempt, TimeoutRetries, budgetEnd)) { Thread.Sleep(RetryBackoffMs); }
                catch (ModbusException ex) when (ex.Code is null && Retry(attempt, CorruptRetries, budgetEnd)) { Thread.Sleep(RetryBackoffMs); }
                // 报错把这一问一答的原始字节带上：现场对着串口助手 / 示波器一眼能看出
                // 是没发对、没收到，还是收到的不是想要的
                catch (ModbusException ex) { throw new ModbusException($"{ex.Message}{Tries(attempt)}｜发 {Hex(frame)}｜收 {(_rxTrace.Count == 0 ? "无" : Hex(_rxTrace))}", ex.Code); }
                catch (TimeoutException ex) { throw new TimeoutException($"{ex.Message}{Tries(attempt)}｜发 {Hex(frame)}｜收 {(_rxTrace.Count == 0 ? "无" : Hex(_rxTrace))}", ex); }
                finally { _lastFrameEnd = Environment.TickCount64; }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 还重不重发：按时长时看期限里还够不够等一拍再发一问（不够就别发——发了也等不到答），
    /// 按次数时看次数。
    /// </summary>
    private bool Retry(int attempt, int maxByCount, long budgetEnd)
        => RetryBudgetMs > 0
            ? budgetEnd - Environment.TickCount64 > RetryBackoffMs + InterFrameGapMs + 20
            : attempt < maxByCount;

    /// <summary>认输时报错里带上一共发了几遍（按时长重试时才有意义：看得出线上干扰多重）。</summary>
    private string Tries(int attempt)
        => RetryBudgetMs > 0 && attempt > 0 ? $"（{RetryBudgetMs} ms 内共发 {attempt + 1} 遍都没拿到对的应答）" : "";

    private long _lastFrameEnd = long.MinValue / 2;
    /// <summary>这一问收到的全部原始字节（含滑过的脏字节），报错时原样带出去。</summary>
    private readonly List<byte> _rxTrace = new();

    /// <summary>本次尝试发完帧的墙钟时刻。</summary>
    private long _lastWriteAt;
    /// <summary>首字节在发问后多少 ms 到达（-1 = 一直没来）。报错带上它定病因：
    /// +几 ms 就到、帧烂在中段 = 线上被掐；+几百 ms 才来 = 从站应答本身就慢，
    /// 那是总超时给小了 / 从站太忙，不是干扰。</summary>
    private long _firstByteMs = -1;

    /// <summary>报错里带的首字节 timing（见 _firstByteMs）。</summary>
    private string FirstByteNote() => _firstByteMs < 0 ? "，首字节没来" : $"，首字节 +{_firstByteMs} ms";

    /// <summary>相邻两帧之间至少留这么久（ms）。3.5 字符在 9600 是 4 ms、4800 是 8 ms，取 10 留余量。</summary>
    public int InterFrameGapMs { get; set; } = 10;

    private byte[] ReadResponse(byte fc, CancellationToken ct, int? expectedDataBytes, long budgetEnd)
    {
        // 单问的等待也截在按时长重试的期限上：整问不超过 RetryBudgetMs
        var deadline = Math.Min(Environment.TickCount64 + _timeoutMs, budgetEnd);

        var head = new byte[2];
        ReadExact(head, deadline, "应答", ct);

        // 485 收发切换的毛刺：应答前面常带一两个脏字节（0xF8 / 0xFF 这类高位全 1 的——
        // 驱动器使能 / 释放总线那一下在接收端看起来像半个起始位）。现场第一次接 RD105
        // 就撞上了：读到的开头是 F8 01 03 …，真应答就跟在后面。站号对不上先往后滑，
        // 滑过 MaxLeadingNoise 个字节还对不上才算真的站号不对
        // 只滑过「不可能是站号」的字节（0 是广播、248~255 协议保留）——别的站号答话
        // 是另一回事（配错了站号），那得原样报出来，不能滑进人家的帧里去把 CRC 错当成结论
        var noise = new List<byte>();
        while (head[0] != Station && IsNoise(head[0]) && noise.Count < MaxLeadingNoise)
        {
            noise.Add(head[0]);
            head[0] = head[1];
            var next = new byte[1];
            ReadExact(next, deadline, "应答", ct);
            head[1] = next[0];
        }

        if (head[0] != Station)
            throw new ModbusException(noise.Count == 0
                ? $"应答站号不对：问的是 {Station} 号，答话的是 {head[0]} 号——查一下总线上是不是接错/配错了站号"
                : $"应答站号不对：问的是 {Station} 号，收到的开头是 {Hex(noise)} {head[0]:X2} …——" +
                  "查一下总线上是不是接错/配错了站号；波特率不对时收到的也是这种乱字节");

        if (head[1] == (fc | 0x80))
        {
            // 异常应答：功能码置高位 + 1 字节异常码
            var tail = new byte[3];
            ReadExact(tail, deadline, "异常应答", ct);
            VerifyCrc(new[] { head[0], head[1], tail[0] }, tail[1], tail[2]);
            throw new ModbusException(tail[0]);
        }

        if (head[1] != fc)
            throw new ModbusException($"应答功能码不对：问 {fc:X2}，答 {head[1]:X2}");

        if (fc is 0x01 or 0x02 or 0x03 or 0x04)
        {
            // 变长应答：字节数 + 数据 + CRC
            var cnt = new byte[1];
            ReadExact(cnt, deadline, "应答长度", ct);
            // 长度跟这一问的对不上：多半是上一问迟到的应答串了帧（或固件按自己的脾气答，
            // 见桥上的 RegsPerReadCap）——算坏帧。剩下的字节不读了，重发前清缓冲会带走
            if (expectedDataBytes is { } want && cnt[0] != want)
                throw new ModbusException($"应答长度不对：这一问该答 {want} 字节数据，收到的说自己有 {cnt[0]} 字节");
            var rest = new byte[cnt[0] + 2];
            ReadExact(rest, deadline, "应答数据", ct);

            var whole = new byte[3 + cnt[0]];
            whole[0] = head[0]; whole[1] = head[1]; whole[2] = cnt[0];
            Array.Copy(rest, 0, whole, 3, cnt[0]);
            VerifyCrc(whole, rest[^2], rest[^1]);

            var body = new byte[1 + cnt[0]];
            body[0] = cnt[0];
            Array.Copy(rest, 0, body, 1, cnt[0]);
            return body;
        }
        else
        {
            // 定长应答（05/06/0F/10）：地址 + 值（或个数）+ CRC
            var rest = new byte[6];
            ReadExact(rest, deadline, "应答数据", ct);
            VerifyCrc(new[] { head[0], head[1], rest[0], rest[1], rest[2], rest[3] }, rest[4], rest[5]);
            return rest[..4];
        }
    }

    private void ReadExact(byte[] buf, long deadline, string what, CancellationToken ct)
    {
        var got = 0;
        var lastByteAt = Environment.TickCount64;
        while (got < buf.Length)
        {
            ct.ThrowIfCancellationRequested();
            var remain = (int)(deadline - Environment.TickCount64);
            if (remain <= 0)
                throw new TimeoutException(
                    $"{_timeoutMs} ms 内没等到完整{what}（收到 {got}/{buf.Length} 字节{FirstByteNote()}）——" +
                    "从站没上电、站号/波特率不对，或者线断了");
            // 已经开始收的帧烂尾了（485 干扰掐断）：早死早重发，不干等满总超时
            if (got > 0 && Environment.TickCount64 - lastByteAt > InterByteIdleMs)
                throw new TimeoutException(
                    $"应答烂尾：{what}收到 {got}/{buf.Length} 字节后超过 {InterByteIdleMs} ms 没有新字节{FirstByteNote()}——" +
                    "485 干扰把帧掐断了，重发即可");
            var n = _transport.Read(buf, got, buf.Length - got, Math.Min(remain, 50));
            if (n == 0) Thread.Sleep(1);        // 假串口的 Read 立即返回 0；真串口自己会等
            else
            {
                if (_rxTrace.Count == 0) _firstByteMs = Environment.TickCount64 - _lastWriteAt;
                _rxTrace.AddRange(buf.AsSpan(got, n).ToArray());
                got += n;
                lastByteAt = Environment.TickCount64;
            }
        }
    }

    private static void VerifyCrc(byte[] content, byte lo, byte hi)
    {
        var crc = ModbusCrc.Compute(content);
        if (lo != (byte)crc || hi != (byte)(crc >> 8))
            throw new ModbusException(
                $"应答 CRC 校验不过（算得 {crc:X4}，收到 {hi:X2}{lo:X2}）——线路有干扰或波特率不对");
    }

    /// <summary>05/06 回显地址+值，0F/10 回显地址+个数。对不上说明从站没照办。</summary>
    private static void VerifyEcho(int address, int value, byte[] body)
    {
        if (body.Length != 4 || (body[0] << 8 | body[1]) != address || (body[2] << 8 | body[3]) != value)
            throw new ModbusException("写应答的回显与下发不一致——从站没有按下发的内容执行");
    }

    private static byte Hi(int v) => (byte)(v >> 8);
    private static byte Lo(int v) => (byte)v;
    private static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));
    /// <summary>不可能是从站站号的字节：0 是广播地址，248~255 协议保留——只有毛刺才长这样。</summary>
    private static bool IsNoise(byte b) => b == 0 || b >= 248;
}
