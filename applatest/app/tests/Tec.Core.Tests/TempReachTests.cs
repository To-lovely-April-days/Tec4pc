using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 判到达（用户定的）：缺省 ±0.1 ℃、要连续稳定才算到达；看的是控温对象（Tr / Tj）那一路。
/// 读数擦进允差一拍就跳出去不算；釜内读数断了不算稳。
/// </summary>
public sealed class TempReachTests
{
    /// <summary>读数按脚本走的假控温：每读一次 CurrentReactor / CurrentJacket 往前走一格，走完停在最后一格。</summary>
    private sealed class Scripted(double[] tr, double[]? tj = null) : ITemperatureControl
    {
        private int _i, _j;
        public int Channel => 1;
        public TempLimits Limits => new(-40, 180, 5);
        public double CurrentReactor => tr[Math.Min(_i++, tr.Length - 1)];
        public double CurrentJacket => tj is null ? double.NaN : tj[Math.Min(_j++, tj.Length - 1)];
        public IObservable<Sample> Temperature => throw new NotSupportedException();
        public Task SetTargetAsync(TempTarget target, CancellationToken ct) => Task.CompletedTask;
        public Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        /// <summary>驱动那一侧的「进允差」：看 Tr（脚本里同一条读数流）。</summary>
        public async Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct)
        {
            for (var k = 0; k < 10_000; k++)
            {
                if (Math.Abs(CurrentReactor - target) <= tolerance) return true;
                await Task.Yield();
            }
            return false;
        }
    }

    private static CommandContext Ctx() => new()
    {
        Channel = 1,
        Capabilities = new NoCaps(),
        Now = () => DateTimeOffset.Now,
        TimeScale = 100_000          // 1 s 一拍压到几乎不等
    };

    private sealed class NoCaps : ICapabilityLookup
    {
        public T? Get<T>() where T : class, ICapability => null;
        public bool Has<T>() where T : class, ICapability => false;
        public IReadOnlyList<ICapability> All => Array.Empty<ICapability>();
    }

    [Fact]
    public void 控温步缺省允差是正负0点1()
    {
        var d = CommandSpecs.Temperature.Single(c => c.Id == CommandSpecs.Control);
        var tol = d.Parameters.Fields.Single(f => f.Key == "tol");
        Assert.Equal(0.1, Convert.ToDouble(tol.Default));
        Assert.Contains(d.Parameters.Fields, f => f.Key == "settle");
    }

    [Fact]
    public async Task 擦进允差一下又出去_不算到达_连续稳定才算()
    {
        // 25.05 进一下 → 25.3 出去 → 之后一直 25.02：要从第二次进去开始数够 3 拍
        var t = new Scripted(new[] { 25.05, 25.3, 25.3, 25.02 });
        var sw = await TempReach.WaitStableAsync(Ctx(), t, 25, 0.1, TempChannelKind.Reactor,
            TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.True(sw);
    }

    [Fact]
    public async Task 一直在允差边上来回跳_预算用完判未到达()
    {
        var seq = Enumerable.Range(0, 400).Select(i => i % 2 == 0 ? 25.05 : 25.2).ToArray();
        var t = new Scripted(seq);
        var ok = await TempReach.WaitStableAsync(new CommandContext
        {
            Channel = 1, Capabilities = new NoCaps(), Now = () => DateTimeOffset.Now, TimeScale = 1000
        }, t, 25, 0.1, TempChannelKind.Reactor, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.False(ok);
    }

    [Fact]
    public async Task 允差0点5时25点3算到_0点1时不算()
    {
        var wide = await TempReach.WaitStableAsync(Ctx(), new Scripted(new[] { 25.3 }), 25, 0.5,
            TempChannelKind.Reactor, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.True(wide);
        var tight = await TempReach.WaitStableAsync(new CommandContext
        {
            Channel = 1, Capabilities = new NoCaps(), Now = () => DateTimeOffset.Now, TimeScale = 1000
        }, new Scripted(new[] { 25.3 }), 25, 0.1, TempChannelKind.Reactor, TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.False(tight);
    }

    [Fact]
    public async Task 夹套对象_稳定判据看Tj不看Tr()
    {
        // Tr 一直在目标上，Tj 出界：控夹套就不该算稳
        var t = new Scripted(new[] { 25.0 }, new[] { 25.0, 26.0, 26.0, 26.0 });
        var ok = await TempReach.WaitStableAsync(new CommandContext
        {
            Channel = 1, Capabilities = new NoCaps(), Now = () => DateTimeOffset.Now, TimeScale = 1000
        }, t, 25, 0.1, TempChannelKind.Jacket, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.False(ok);
    }

    [Fact]
    public async Task 釜内读数断了不算稳()
    {
        var t = new Scripted(new[] { 25.0, double.NaN, double.NaN });
        var ok = await TempReach.WaitStableAsync(new CommandContext
        {
            Channel = 1, Capabilities = new NoCaps(), Now = () => DateTimeOffset.Now, TimeScale = 1000
        }, t, 25, 0.1, TempChannelKind.Reactor, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.False(ok);
    }
}
