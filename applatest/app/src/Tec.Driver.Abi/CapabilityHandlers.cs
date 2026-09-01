namespace Tec.Driver.Abi;

/// <summary>
/// 指令语义的时间助手。仿真会话把 scale 设成 &gt;1 加速演示，真实会话恒为 1——
/// 同一套执行器两边跑，时间轴只在这里换算一次。
/// </summary>
public static class DriverTime
{
    public static Task DelayAsync(TimeSpan simulated, double scale, CancellationToken ct)
    {
        if (scale <= 0) scale = 1;
        var real = TimeSpan.FromTicks((long)(simulated.Ticks / scale));
        return real <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(real, ct);
    }

    /// <summary>轮询到条件成立或超时。返回是否成立。</summary>
    public static async Task<bool> PollAsync(Func<bool> predicate, TimeSpan timeout, double scale,
                                             Func<DateTimeOffset> now, CancellationToken ct)
    {
        var deadline = timeout > TimeSpan.Zero ? now() + timeout : DateTimeOffset.MaxValue;
        while (!ct.IsCancellationRequested)
        {
            if (predicate()) return true;
            if (now() >= deadline) return false;
            await DelayAsync(TimeSpan.FromSeconds(1), scale, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }
}

/// <summary>
/// 温度指令的**能力通用执行器**：只按 ITemperatureControl 说话，不认识任何具体
/// 设备类，所以仿真会话与真机会话认领的是同一套——「仿真调好的配方插上真机
/// 能跑」靠的就是这一份，不是两处各写各的「升温至」。
///
/// 从前它们住在仿真器程序集里、只被仿真会话注册，纯真机台面跑含温度步的配方
/// 会报「该通道没有能执行此指令的设备」——搬到 ABI（指令语义跟指令声明住一起）
/// 就是为了堵这个缺口。
/// </summary>
public static class CapabilityCommands
{
    /// <summary>会话的 Resolve 直接转发到这里；不认识的指令返回 null。</summary>
    public static ICommandHandler? Resolve(string commandId) => commandId switch
    {
        CommandSpecs.Control => new TempControlHandler(),
        CommandSpecs.Gradient => new TempGradientHandler(),
        CommandSpecs.Hold => new TempHoldHandler(),
        CommandSpecs.PassiveCool => new TempPassiveCoolHandler(),
        CommandSpecs.Reflux => new TempRefluxHandler(),
        _ => null
    };

    internal static ITemperatureControl Temp(CommandContext ctx)
        => ctx.Capabilities.Get<ITemperatureControl>()
           ?? throw new InvalidOperationException("该通道没有温度控制能力");
}

/// <summary>
/// 控温。到达即结束；勾了「到达后等待稳定」再多等一个允差窗。
/// 升温还是降温不用问——RampAsync 给的是目标值，往哪边走由当前温度决定。
/// 到达方式三档（iControl 的 Task）：尽快 = 设备最大变温能力（读的是
/// 设备自己的 Limits，不是编的数）；按时长 = 温差 ÷ 时长按当时实测现算；
/// 按速率 = 老行为，也是没有 task 键的老配方的缺省。
/// </summary>
public sealed class TempControlHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var temp = CapabilityCommands.Temp(ctx);
        var target = p.Num("target");
        var tol = p.Num("tol", 0.5);
        var kind = p.Str("obj", "釜内 Tr").Contains("Tj") ? TempChannelKind.Jacket : TempChannelKind.Reactor;
        var began = ctx.Now();

        var current = kind == TempChannelKind.Jacket ? temp.CurrentJacket : temp.CurrentReactor;
        var rate = CommandSpecs.TempTaskOf(p) switch
        {
            "尽快" => temp.Limits.MaxRatePerMin,
            // 时长填 0 当「尽快」处理——除零得到的不是快，是 NaN
            "按时长" => p.Num("dur") > 0
                ? Math.Min(temp.Limits.MaxRatePerMin, Math.Abs(target - current) / p.Num("dur"))
                : temp.Limits.MaxRatePerMin,
            _ => p.Num("rate", 2)
        };
        rate = Math.Max(rate, 0.05);

        await temp.RampAsync(target, rate, kind, ct).ConfigureAwait(false);
        // 到不了的目标不能把通道永远挂住：兜底超时按「温差 / 速率」的 3 倍给
        var budget = TimeSpan.FromMinutes(Math.Abs(target - temp.CurrentReactor) / rate * 3 + 10);
        var reached = await temp.WaitReachedAsync(target, tol, budget, ct).ConfigureAwait(false);

        if (reached && p.Flag("wait", true))
            await DriverTime.DelayAsync(TimeSpan.FromMinutes(1), ctx.TimeScale, ct).ConfigureAwait(false);

        return new CommandOutcome(reached ? EndReason.Reached : EndReason.Timeout, ctx.Now() - began)
        {
            Note = reached ? null : $"未在 {budget.TotalMinutes:F0} min 内到达 {target:F1} ℃"
        };
    }
}

/// <summary>梯度控温：逐段走完，每段先按速率到目标再保持。</summary>
public sealed class TempGradientHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput input, CancellationToken ct)
    {
        var temp = CapabilityCommands.Temp(ctx);
        var began = ctx.Now();
        var rounds = input.Flag("loop") ? 2 : 1;   // 勾了「循环执行该曲线」就再走一遍

        for (var k = 0; k < rounds; k++)
            foreach (var row in input.RowsOrEmpty)
            {
                ct.ThrowIfCancellationRequested();
                var target = row.Num("t");
                await temp.RampAsync(target, Math.Max(row.Num("r"), 0.01), TempChannelKind.Reactor, ct)
                          .ConfigureAwait(false);
                await temp.WaitReachedAsync(target, 0.5, TimeSpan.FromHours(4), ct).ConfigureAwait(false);
                await DriverTime.DelayAsync(TimeSpan.FromMinutes(row.Num("h")), ctx.TimeScale, ct).ConfigureAwait(false);
            }

        return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
    }
}

/// <summary>恒温保持：保持当前温度，只计时。</summary>
public sealed class TempHoldHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var temp = CapabilityCommands.Temp(ctx);
        var began = ctx.Now();
        await temp.SetTargetAsync(new TempTarget(temp.CurrentReactor), ct).ConfigureAwait(false);
        await DriverTime.DelayAsync(TimeSpan.FromMinutes(p.Num("dur")), ctx.TimeScale, ct).ConfigureAwait(false);
        return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
    }
}

/// <summary>
/// 蒸回流：把夹套跟随交给设备的 IRefluxControl（限速、钳制、断线停跟随都在
/// 实现方），这里只管开、计时、收——取消/中止也要把跟随停掉，不停的话
/// 步都没了夹套还在追。
/// </summary>
public sealed class TempRefluxHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var reflux = ctx.Capabilities.Get<IRefluxControl>()
            ?? throw new InvalidOperationException("该通道没有蒸回流（夹套跟随）能力");
        var began = ctx.Now();
        await reflux.StartAsync(p.Num("dt", 5), p.Num("tjmax", 120), ct).ConfigureAwait(false);
        try
        {
            await DriverTime.DelayAsync(TimeSpan.FromMinutes(p.Num("dur", 60)), ctx.TimeScale, ct)
                            .ConfigureAwait(false);
        }
        finally
        {
            try { await reflux.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
    }
}

/// <summary>自然冷却：停掉控温靠环境降，到点或超时结束。</summary>
public sealed class TempPassiveCoolHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var temp = CapabilityCommands.Temp(ctx);
        var began = ctx.Now();
        var target = p.Num("target", 25);
        await temp.StopAsync(ct).ConfigureAwait(false);
        var ok = await DriverTime.PollAsync(() => temp.CurrentReactor <= target + 0.5,
            TimeSpan.FromMinutes(p.Num("timeout", 120)), ctx.TimeScale, ctx.Now, ct).ConfigureAwait(false);
        return new CommandOutcome(ok ? EndReason.Reached : EndReason.Timeout, ctx.Now() - began)
        {
            Note = ok ? null : $"超时放弃，仍在 {temp.CurrentReactor:F1} ℃"
        };
    }
}
