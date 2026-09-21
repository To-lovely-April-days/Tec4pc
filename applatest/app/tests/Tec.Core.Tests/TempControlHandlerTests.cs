using Tec.Core.Benches;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 配方「控温」三档到达方式各自往驱动写什么。
/// 盯的是「尽快」：它是普通模式的升温——**不写斜率**，目标一步交给温控器
/// （SetTargetAsync；RD105 上落成 SPEED=0），跟 HMI 手动面板的「尽快」同一个动作。
/// 从前它按设备标称最大速率去 RampAsync，在 RD105 上就是 5 ℃/min 的斜坡，不是「尽快」。
/// </summary>
public class TempControlHandlerTests
{
    private sealed class RecTemp : ITemperatureControl
    {
        public readonly List<string> Log = new();
        public int Channel => 1;
        public TempLimits Limits { get; } = new(-40, 180, 5);
        public double CurrentReactor => 25;
        public double CurrentJacket => 25;
        public Task SetTargetAsync(TempTarget t, CancellationToken ct)
        { Log.Add($"set {t.Value} {t.Kind}"); return Task.CompletedTask; }
        public Task RampAsync(double t, double r, TempChannelKind k, CancellationToken ct)
        { Log.Add($"ramp {t} {r:0.##} {k}"); return Task.CompletedTask; }
        public Task<bool> WaitReachedAsync(double t, double tol, TimeSpan to, CancellationToken ct)
            => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct) { Log.Add("stop"); return Task.CompletedTask; }
        public IObservable<Sample> Temperature { get; } = new Broadcast<Sample>();
    }

    private sealed class OneCap(ICapability cap) : ICapabilityLookup
    {
        public T? Get<T>() where T : class, ICapability => cap as T;
        public bool Has<T>() where T : class, ICapability => cap is T;
        public IReadOnlyList<ICapability> All => new[] { cap };
    }

    private static async Task<RecTemp> Run(params (string, object)[] p)
    {
        var temp = new RecTemp();
        var ctx = new CommandContext
        {
            Channel = 1,
            Capabilities = new OneCap(temp),
            Now = () => DateTimeOffset.UnixEpoch,
            TimeScale = 1000,   // 「到达后等待稳定」那 1 min 别真等
        };
        var input = new CommandInput(ParameterSet.Of(p));
        await new TempControlHandler().ExecuteAsync(ctx, input, CancellationToken.None);
        return temp;
    }

    [Fact]
    public async Task 尽快_目标一步写给温控器_不走斜坡()
    {
        var t = await Run(("target", 70d), ("task", "尽快"), ("wait", false));

        Assert.Equal(new[] { "set 70 Reactor" }, t.Log);
    }

    [Fact]
    public async Task 尽快_控温对象是夹套就写夹套()
    {
        var t = await Run(("target", 70d), ("task", "尽快"), ("obj", "夹套 Tj"), ("wait", false));

        Assert.Equal(new[] { "set 70 Jacket" }, t.Log);
    }

    [Fact]
    public async Task 按时长_时长填0当尽快()
    {
        var t = await Run(("target", 70d), ("task", "按时长"), ("dur", 0d), ("wait", false));

        Assert.Equal(new[] { "set 70 Reactor" }, t.Log);
    }

    [Fact]
    public async Task 按时长_温差除以时长_不超设备最大速率()
    {
        // (70 − 25) / 15 = 3 ℃/min，在 5 以内
        var t = await Run(("target", 70d), ("task", "按时长"), ("dur", 15d), ("wait", false));
        Assert.Equal(new[] { "ramp 70 3 Reactor" }, t.Log);

        // (70 − 25) / 3 = 15 ℃/min，压到设备的 5
        var t2 = await Run(("target", 70d), ("task", "按时长"), ("dur", 3d), ("wait", false));
        Assert.Equal(new[] { "ramp 70 5 Reactor" }, t2.Log);
    }

    [Fact]
    public async Task 按速率_老配方没有task键也按速率()
    {
        var t = await Run(("target", 70d), ("task", "按速率"), ("rate", 1.5d), ("wait", false));
        Assert.Equal(new[] { "ramp 70 1.5 Reactor" }, t.Log);

        var old = await Run(("target", 70d), ("rate", 2d), ("wait", false));
        Assert.Equal(new[] { "ramp 70 2 Reactor" }, old.Log);
    }
}
