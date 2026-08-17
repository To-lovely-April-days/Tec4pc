using Tec.Core.Catalog;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 中止之后把设备收到安全态。
///
/// 中止本身只取消配方的执行循环，机器不会因此停下来——温控器还守着最后那个
/// 设定值，泵还在打。实测过：按下停止时 37.2 ℃，十几秒后一路升到 60 ℃ 稳住。
/// 操作人按下那颗红钮时想的绝不是这个。
///
/// **该关什么归驱动说**（Core 不认识具体设备），做了什么逐条进记录——
/// GLP 要答得出「停的那一刻机器被动了什么」。
/// </summary>
public class SafeStopTests
{
    private static Recipes.Recipe Waiting(double seconds = 30)
        => Harness.RecipeOf("等着", Harness.Mk(BuiltinCommands.Wait, ("dur", seconds)));

    /// <summary>一台什么都不做的设备，用来盯停机这条路本身。</summary>
    private sealed class StubSession : IDeviceSession
    {
        private readonly Func<int, CancellationToken, ValueTask<IReadOnlyList<string>?>>? _stop;

        public StubSession(string id, Func<int, CancellationToken, ValueTask<IReadOnlyList<string>?>>? stop)
        {
            InstanceId = id;
            _stop = stop;
        }

        public string InstanceId { get; }
        public DeviceState State => DeviceState.Ready;
        public event EventHandler<DeviceState>? StateChanged { add { } remove { } }
        public IReadOnlyList<TagDescriptor> Tags => Array.Empty<TagDescriptor>();
        public IObservable<Sample> Samples { get; } = new Broadcast<Sample>();
        public int WellCount => 1;
        public IReadOnlyList<ICapability> CapabilitiesOf(int well) => Array.Empty<ICapability>();
        public ICommandHandler? Resolve(string commandId) => null;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>停机时拿到的令牌。**不能是那个刚被 Cancel 掉的**。</summary>
        public CancellationToken SeenToken { get; private set; }
        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
        {
            Calls++;
            SeenToken = ct;
            // 没给 stop 就是「这个驱动没实现」，走接口的默认实现那条路
            return _stop is null
                ? ValueTask.FromResult<IReadOnlyList<string>?>(null)
                : _stop(well, ct);
        }
    }

    private static IEnumerable<string> SafeStopLines(ChannelRun r)
        => r.Events.Where(e => e.Kind == EventKind.SafeStop).Select(e => e.Text);

    [Fact]
    public async Task 中止之后叫驱动收到安全态并逐条进记录()
    {
        await using var h = new Harness(200);
        var ch = await h.ReactorChannelAsync(1);
        var stub = new StubSession("R9", (_, _) =>
            ValueTask.FromResult<IReadOnlyList<string>?>(new[] { "已切断加热输出", "搅拌保持 400 rpm" }));
        ch.Attach(stub, 0, false);

        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        h.Engine.Runner(1)!.Abort("张三", "操作人停止 CH1");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(1, stub.Calls);
        Assert.Contains("R9 已切断加热输出", SafeStopLines(run));
        Assert.Contains("R9 搅拌保持 400 rpm", SafeStopLines(run));
    }

    [Fact]
    public async Task 正常跑完不动设备()
    {
        // 收尾状态是配方作者定的：降温结晶跑完就该保持在 5 ℃ 过夜，
        // 替他关掉才是帮倒忙
        await using var h = new Harness(600);
        var ch = await h.ReactorChannelAsync(1);
        var stub = new StubSession("R9", (_, _) =>
            ValueTask.FromResult<IReadOnlyList<string>?>(new[] { "已切断加热输出" }));
        ch.Attach(stub, 0, false);

        var run = h.Engine.StartChannel(1, Waiting(1), "张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(ChannelRunState.Completed, run.State);
        Assert.Equal(0, stub.Calls);
        Assert.Empty(SafeStopLines(run));
    }

    [Fact]
    public async Task 驱动没实现就照实说输出还开着()
    {
        // 不假装停干净了——现场得知道这台还开着
        await using var h = new Harness(200);
        var ch = await h.ReactorChannelAsync(1);
        ch.Attach(new StubSession("R9", null), 0, false);

        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Contains(SafeStopLines(run), t => t.Contains("R9") && t.Contains("未声明停机动作"));
    }

    [Fact]
    public async Task 实现了但没什么可停的就不记一行()
    {
        // 一支 pH 探头本来就没有输出可关，为它写一句「输出保持原样」是噪声
        await using var h = new Harness(200);
        var ch = await h.ReactorChannelAsync(1);
        ch.Attach(new StubSession("PH9", (_, _) =>
            ValueTask.FromResult<IReadOnlyList<string>?>(Array.Empty<string>())), 0, false);

        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.DoesNotContain(SafeStopLines(run), t => t.Contains("PH9"));
    }

    [Fact]
    public async Task 某台停不下来不能把别的几台拖住()
    {
        // 停不下来是最该留痕的一种：机器还开着，而且没人知道
        await using var h = new Harness(200);
        var ch = await h.ReactorChannelAsync(1);
        ch.Attach(new StubSession("BAD", (_, _) => throw new InvalidOperationException("串口没响应")), 0, false);
        var good = new StubSession("P9", (_, _) =>
            ValueTask.FromResult<IReadOnlyList<string>?>(new[] { "加料泵已停" }));
        ch.Attach(good, 0, false);

        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Contains(SafeStopLines(run), t => t.Contains("BAD") && t.Contains("停机失败"));
        Assert.Contains("P9 加料泵已停", SafeStopLines(run));      // 后面那台照样停了
        Assert.Equal(1, good.Calls);
    }

    [Fact]
    public async Task 停机用的不是那个刚被取消掉的令牌()
    {
        // 中止是靠 Cancel 那个令牌实现的。拿它去停设备等于一条指令都发不出去——
        // 每一次下发都会当场抛 OperationCanceled，机器一动不动
        await using var h = new Harness(200);
        var ch = await h.ReactorChannelAsync(1);
        var stub = new StubSession("R9", (_, _) =>
            ValueTask.FromResult<IReadOnlyList<string>?>(Array.Empty<string>()));
        ch.Attach(stub, 0, false);

        h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        Assert.Equal(1, stub.Calls);
        Assert.False(stub.SeenToken.IsCancellationRequested);
        Assert.True(stub.SeenToken.CanBeCanceled);   // 有预算，真机不响应时不把收尾卡死
    }

    [Fact]
    public async Task 停机记录排在中止与结束之间()
    {
        // 读记录的人是按时间顺序看的：先看到「谁停的」，再看到「机器被动了什么」，
        // 最后才是「这一趟结束了」
        await using var h = new Harness(200);
        var ch = await h.ReactorChannelAsync(1);
        ch.Attach(new StubSession("R9", (_, _) =>
            ValueTask.FromResult<IReadOnlyList<string>?>(new[] { "已切断加热输出" })), 0, false);

        var run = h.Engine.StartChannel(1, Waiting(), "张三");
        await Task.Delay(200);
        h.Engine.Runner(1)!.Abort("张三");
        await h.Engine.Runner(1)!.Completion;

        var kinds = run.Events.Select(e => e.Kind).ToList();
        Assert.True(kinds.IndexOf(EventKind.Aborted) < kinds.IndexOf(EventKind.SafeStop));
        Assert.True(kinds.IndexOf(EventKind.SafeStop) < kinds.IndexOf(EventKind.ChannelFinished));
    }
}
