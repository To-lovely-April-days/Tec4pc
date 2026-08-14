using System.Text;
using TecControl.Core.Comm;
using TecControl.Core.Protocol;

namespace TecControl.Core.Tests;

/// <summary>用预置应答的假串口验证 TecClient 的收发与成帧逻辑。</summary>
file sealed class FakeTransport : ISerialTransport
{
    private readonly Queue<byte[]> _replies = new();
    private byte[]? _pending;
    private int _pendingOffset;

    public List<string> SentCommands { get; } = [];

    public void EnqueueReply(string reply) => _replies.Enqueue(Encoding.ASCII.GetBytes(reply));

    public bool IsOpen => true;
    public void Open() { }
    public void Close() { }
    public void DiscardInput() { }
    public void Dispose() { }

    public void Write(byte[] buffer, int offset, int count)
    {
        SentCommands.Add(Encoding.ASCII.GetString(buffer, offset, count));
        _pending = _replies.Count > 0 ? _replies.Dequeue() : null;
        _pendingOffset = 0;
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        if (_pending is null || _pendingOffset >= _pending.Length) return 0;
        var n = Math.Min(count, _pending.Length - _pendingOffset);
        Array.Copy(_pending, _pendingOffset, buffer, offset, n);
        _pendingOffset += n;
        return n;
    }
}

public class TecClientTests
{
    [Fact]
    public async Task Query_SendsCommandAndParsesReply()
    {
        var transport = new FakeTransport();
        transport.EnqueueReply("OKTC1:TG=2500000@\r\n");
        using var client = new TecClient(transport) { MinCommandGapMs = 0 };

        var value = await client.QueryAsync(1, TecCmd.Target);

        Assert.Equal("TC1:TG=?@\n", transport.SentCommands.Single());
        Assert.Equal(2500000, value);
    }

    [Fact]
    public async Task Set_VerifiesEcho()
    {
        var transport = new FakeTransport();
        transport.EnqueueReply("OKTC1:TG=2500000@\r\n");
        using var client = new TecClient(transport) { MinCommandGapMs = 0 };

        await client.SetAsync(1, TecCmd.Target, 2500000);

        Assert.Equal("TC1:TG=2500000@\n", transport.SentCommands.Single());
    }

    [Fact]
    public async Task Set_EchoMismatch_Throws()
    {
        var transport = new FakeTransport();
        transport.EnqueueReply("OKTC1:TG=2400000@\r\n");
        transport.EnqueueReply("OKTC1:TG=2400000@\r\n");   // 坏帧重试后仍不符
        using var client = new TecClient(transport) { MinCommandGapMs = 0 };

        await Assert.ThrowsAsync<TecProtocolException>(() =>
            client.SetAsync(1, TecCmd.Target, 2500000));
    }

    [Fact]
    public async Task QueryText_ReturnsRawString()
    {
        var transport = new FakeTransport();
        transport.EnqueueReply("OKTEC=215L@\r\n");
        using var client = new TecClient(transport) { MinCommandGapMs = 0 };

        var model = await client.QueryTextAsync(null, TecCmd.Model);

        Assert.Equal("TEC=?@\n", transport.SentCommands.Single());
        Assert.Equal("215L", model);
    }

    [Fact]
    public async Task NoReply_ThrowsTimeout()
    {
        var transport = new FakeTransport();
        using var client = new TecClient(transport)
        {
            MinCommandGapMs = 0, ReplyTimeoutMs = 80, InterByteIdleMs = 10,
        };

        await Assert.ThrowsAsync<TimeoutException>(() => client.QueryAsync(1, TecCmd.Target));
        Assert.Equal(2, transport.SentCommands.Count);   // 默认重试 1 次：共发送 2 次
    }

    [Fact]
    public async Task NoReplyOnce_RetriesAndSucceeds()
    {
        // 现场实测：大电流 PWM 环境一整天偶发一次丢帧。丢一帧应重发补救，
        // 不应作为周期失败上报（SET 均为幂等的绝对值写入，重发安全）。
        var transport = new FakeTransport();
        transport.EnqueueReply("");                          // 第一次发送：无应答
        transport.EnqueueReply("OKTC1:TG=2500000@\r\n");     // 重试成功
        using var client = new TecClient(transport)
        {
            MinCommandGapMs = 0, ReplyTimeoutMs = 80, InterByteIdleMs = 10,
        };

        var value = await client.QueryAsync(1, TecCmd.Target);

        Assert.Equal(2, transport.SentCommands.Count);
        Assert.Equal(2500000, value);
    }

    [Fact]
    public async Task CorruptedReplyOnce_RetriesAndSucceeds()
    {
        // 现场实测（−30℃ 大功率段）：分隔符 '@'(0x40) 被干扰打成 'P'(0x50)，
        // TC2:OUTV 与 SINTERIORTEMP 粘连成非数字文本 → 解析缺字段。重发一次应救回。
        var transport = new FakeTransport();
        transport.EnqueueReply("OKTC1:TG=2500000PSOMETHING=1@\r\n");   // 坏帧：@→P 粘连
        transport.EnqueueReply("OKTC1:TG=2500000@\r\n");               // 重发后正常
        using var client = new TecClient(transport) { MinCommandGapMs = 0 };

        var value = await client.QueryAsync(1, TecCmd.Target);

        Assert.Equal(2, transport.SentCommands.Count);
        Assert.Equal(2500000, value);
    }

    [Fact]
    public async Task CorruptedRepliesTwice_StillThrows()
    {
        // 连续坏帧（真干扰源/线路故障）不无限重发：重试用尽后照常上报
        var transport = new FakeTransport();
        transport.EnqueueReply("OKTC1:TG=25PX=1@\r\n");
        transport.EnqueueReply("OKTC1:TG=25PX=1@\r\n");
        using var client = new TecClient(transport) { MinCommandGapMs = 0 };

        await Assert.ThrowsAsync<TecProtocolException>(() => client.QueryAsync(1, TecCmd.Target));
        Assert.Equal(2, transport.SentCommands.Count);
    }

    [Fact]
    public async Task SetEchoMismatch_RetriesOnceThenThrows()
    {
        // 设备真实拒写/钳位时，重发后回读依旧不符 → 仍然抛出（不吞掉真问题）
        var transport = new FakeTransport();
        transport.EnqueueReply("OKTC1:TG=2400000@\r\n");
        transport.EnqueueReply("OKTC1:TG=2400000@\r\n");
        using var client = new TecClient(transport) { MinCommandGapMs = 0 };

        await Assert.ThrowsAsync<TecProtocolException>(() =>
            client.SetAsync(1, TecCmd.Target, 2500000));
        Assert.Equal(2, transport.SentCommands.Count);
    }

    [Fact]
    public async Task ReplyWithoutCrLf_CompletesOnIdle()
    {
        // DATADEMAND 长应答可能不带 \r\n 结尾，靠线路空闲判帧
        var transport = new FakeTransport();
        transport.EnqueueReply("TC1:TCADJTEMP=2259187@SINTERIORTEMP=23@");
        using var client = new TecClient(transport)
        {
            MinCommandGapMs = 0, ReplyTimeoutMs = 200, InterByteIdleMs = 10,
        };

        var reply = await client.ExecAsync("DATADEMAND=2@\n");

        Assert.Contains("SINTERIORTEMP=23", reply);
    }
}
