using System.Reflection;
using TecControl.Core;
using TecControl.Core.Control;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Xunit;

namespace Tec.Core.Tests.Step1Pins;

/// <summary>
/// 【本地改动】F1 接缝哨兵（2026-11-16，外环「按实测实时算」第 1 步动产品代码之前，在 22b9ebe 上跑绿）。
/// 验收仿真（tests/Tec.Core.Tests/Accept/，别的会话的、未跟踪；这里一个类都不引用）靠反射吃这几样，不设 TEC_ACCEPT 时它不跑，
/// 改坏了要等别的会话开验收才炸。这里把它们钉在常跑的测试里，名单写死：
///   · HostControlLoop 的公有可写实例属性（含 init）名单——验收的压缩表要逐个归类，多一个就红。第 1 步一律加「方法」不加属性；
///   · DuoSession 的 TimeSpan / TimeSpan? 字段（实例 + static + 自动属性后备字段）和 TimeSpan / TimeSpan? 属性名单——验收逐个归「钟 / 墙钟」；
///   · HostControlLoop、RunawayDetector 的构造函数签名（验收按参数名 / 缺省值核）；
///   · 反射用到的字段名：Rd105TemperatureControl._loop、DuoSession._pollCts / _pollTask、HostControlLoop._channels、ChannelState.Pid / OuterPid；
///     外加 E0 金样台子自己反射的 DuoSession._rd、Rd105TemperatureControl.Recorder、Rd105LoopRecorder._pendingNote。
/// 第 1 步真要正经加 / 改其中一样，先回主会话定（DECISIONS 约束里写着不许），不要只改这里的名单。
/// </summary>
public sealed class SeamSentinelTests
{
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>22b9ebe 上 HostControlLoop 的公有可写实例属性（公有 set 或 init、不带索引），按序数排序。</summary>
    private static readonly string[] LoopWritable =
    {
        "AutoTuneTimeout", "EchoDwellMs", "GainSchedulingEnabled", "HeatBoost",
        "HeatBoostAbortBiasFloorC", "HeatBoostAbortOverC", "HeatBoostBrakeTime", "HeatBoostConservativeHeadroomC",
        "HeatBoostDecel", "HeatBoostDenyGrace", "HeatBoostEnterC", "HeatBoostFadeC",
        "HeatBoostHandAverage", "HeatBoostHandBandC", "HeatBoostHandBiasC", "HeatBoostHandHold",
        "HeatBoostHandStillCPerMin", "HeatBoostHeadroomC", "HeatBoostKv", "HeatBoostLinearC",
        "HeatBoostMaxDeltaC", "HeatBoostStallBandC", "HeatBoostStallC", "HeatBoostStallWindow",
        "HeatBoostTauHat", "HeatBoostWallHat", "HeatBoostWindow", "HeaterPwmFrequencyLevel",
        "HeatingEffectivenessRatio", "InnerSetpointMaxC", "InnerSetpointMinC", "ManagePwmFrequency",
        "MaxConsecutiveFailures", "OuterApproachFreezeCPerMin", "OuterApproachHorizon", "OuterApproachMinCPerMin",
        "OuterApproachWindow", "OuterBiasCheckAfterCross", "OuterBiasCheckBandC", "OuterBiasCheckCarryC",
        "OuterBiasCheckGapC", "OuterBiasCheckHold", "OuterBiasCheckMeanC", "OuterBiasCheckRefixGapC",
        "OuterBiasCheckTauHat", "OuterBiasCheckWallHat", "OuterBiasCheckWindow", "OuterBiasClampBandC",
        "OuterBiasClampMarginC", "OuterBoostClampC", "OuterBrakeBiasC", "OuterBrakeMinDistanceC",
        "OuterClampSlewCPerMin", "OuterCrawlBandC", "OuterCrawlJacketSpanC", "OuterCrawlRefixGapC",
        "OuterCrawlRefixMaxC", "OuterCrossCapJacketSteadyC", "OuterCrossCapMaxDropC", "OuterDischargeApproachBandC",
        "OuterDischargeMinGapC", "OuterFinishAwayCPerMin", "OuterFinishAwayRelease", "OuterFinishCarry",
        "OuterFinishCarryNearC", "OuterFinishCrawlHorizon", "OuterFinishGain", "OuterFinishMaxHold",
        "OuterFinishMaxKp", "OuterFinishMaxSetpointC", "OuterFinishRamp", "OuterFinishRelease",
        "OuterFinishSaturation", "OuterFinishSettleTime", "OuterFinishShotMinSpan", "OuterFinishShotTau",
        "OuterFinishShotWindow", "OuterHeatBalanceMinSpan", "OuterHeatBalanceSlopeSpan", "OuterHeatBalanceSpan",
        "OuterInnerLagFreezeC", "OuterIntegralBandC", "OuterLearnBandC", "OuterLearnStillC",
        "OuterLossGrace", "OuterPeriod", "OuterPushRefixJacketTrackC", "OuterPushRefixMaxDropC",
        "OuterShotHeatBalanceGapC", "OuterStartAverage", "OuterStartQuietInnerC", "OuterStartQuietOuterC",
        "OuterStartQuietWindow", "OuterSteadyCyclesToLearn", "OuterTrackingBoost", "Period",
        "SaturationWarnAfter", "SteadyBandC", "SteadyCyclesToLearn", "SyncMode",
        "TecPwmFrequencyLevel", "TuningSuspendLimit",
    };

    /// <summary>22b9ebe 上 DuoSession 类型恰为 TimeSpan 的字段（实例 + static；自动属性按属性名），按序数排序。</summary>
    private static readonly string[] SessionTimeSpanFields =
    {
        "AskTimeout", "ChillerMinGap", "ChillerRetryAfter", "ChillerStartWarn", "ChillerStopWaitOnDispose",
        "ChillerStopWarn", "CoolMinDuration", "EnterSlopeMinSpan", "ExitLead", "ReentryDwell",
        "SignDwell", "SlopeMinSpan", "SlopeWindowSpan", "StopResend", "TrFreshWindow",
        "_tick", "_waterLead",
    };

    /// <summary>22b9ebe 上 DuoSession 类型为 TimeSpan? 的字段：一个都没有。</summary>
    private static readonly string[] SessionNullableTimeSpanFields =
    {
    };

    /// <summary>22b9ebe 上 DuoSession 类型为 TimeSpan / TimeSpan? 的属性（含私有、static、只读计算属性）。</summary>
    private static readonly string[] SessionTimeSpanProperties =
    {
        "AskTimeout", "ChillerMinGap", "ChillerStartWarn", "ChillerStopWaitOnDispose", "ChillerStopWarn",
        "CoolMinDuration", "EnterSlopeMinSpan", "ExitLead", "ReentryDwell", "SignDwell",
        "SlopeMinSpan", "SlopeRetention", "SlopeWindowSpan",
    };

    private static string Listing(IEnumerable<string> names) => "{ " + string.Join(", ", names.Select(n => "\"" + n + "\"")) + " }";

    private static void SameNames(string what, string[] want, string[] got)
    {
        var extra = got.Except(want, StringComparer.Ordinal).ToArray();
        var gone = want.Except(got, StringComparer.Ordinal).ToArray();
        Assert.True(extra.Length == 0 && gone.Length == 0,
            $"{what}变了：多了 [{string.Join("、", extra)}]，少了 [{string.Join("、", gone)}]（现在 {got.Length} 个）。\n" +
            "验收仿真按这份名单逐个归类——第 1 步不许加 / 改名（加方法，不加属性 / 字段）；真要动先回主会话。\n现在的名单：" + Listing(got));
    }

    [Fact]
    public void 哨兵_回路公有可写实例属性名单不增不减_102个_含init()
    {
        var got = typeof(HostControlLoop).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0)
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        SameNames("HostControlLoop 的公有可写实例属性", LoopWritable, got);
        Assert.Equal(102, got.Length);
        // static 的公有可写属性验收不枚举，但同样不该冒出来（加了也是一个没人缩的口子）
        var staticWritable = typeof(HostControlLoop).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.SetMethod is { IsPublic: true }).Select(p => p.Name).ToArray();
        Assert.True(staticWritable.Length == 0, "HostControlLoop 冒出了 static 公有可写属性：" + string.Join("、", staticWritable));
    }

    private static string FieldName(FieldInfo f) => f.Name.StartsWith('<') ? f.Name[1..f.Name.IndexOf('>')] : f.Name;

    [Fact]
    public void 哨兵_DuoSession的TimeSpan字段和属性名单不增不减_含static和自动属性后备字段()
    {
        var fields = typeof(DuoSession).GetFields(All);
        SameNames("DuoSession 的 TimeSpan 字段", SessionTimeSpanFields,
            fields.Where(f => f.FieldType == typeof(TimeSpan)).Select(FieldName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        SameNames("DuoSession 的 TimeSpan? 字段", SessionNullableTimeSpanFields,
            fields.Where(f => f.FieldType == typeof(TimeSpan?)).Select(FieldName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        SameNames("DuoSession 的 TimeSpan / TimeSpan? 属性", SessionTimeSpanProperties,
            typeof(DuoSession).GetProperties(All).Where(p => p.PropertyType == typeof(TimeSpan) || p.PropertyType == typeof(TimeSpan?))
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void 哨兵_回路和跑飞检测的构造函数签名不变()
    {
        var loopCtor = typeof(HostControlLoop).GetConstructors().Single();
        Assert.Equal(new[] { ("controller", typeof(TecController)) }, loopCtor.GetParameters().Select(p => (p.Name!, p.ParameterType)));

        var runaway = typeof(RunawayDetector).GetConstructors().Single().GetParameters();
        Assert.Equal(new[] { "deltaC", "detectSeconds", "graceSeconds" }, runaway.Select(p => p.Name));
        Assert.All(runaway, p => Assert.Equal(typeof(double), p.ParameterType));
        Assert.Equal(new[] { 0.5, 180.0, 120.0 }, runaway.Select(p => Convert.ToDouble(p.DefaultValue, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void 哨兵_验收和金样反射用到的字段都还在_类型没变()
    {
        Field(typeof(Rd105TemperatureControl), "_loop", typeof(HostControlLoop));
        Field(typeof(DuoSession), "_pollCts", typeof(CancellationTokenSource));
        Field(typeof(DuoSession), "_pollTask", typeof(Task));
        var channels = Field(typeof(HostControlLoop), "_channels", null);
        Assert.True(channels.FieldType.IsArray, "HostControlLoop._channels 不再是数组了");
        var state = channels.FieldType.GetElementType()!;
        Assert.Equal("ChannelState", state.Name);
        foreach (var name in new[] { "Pid", "OuterPid" })
        {
            var f = state.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(f is not null, $"HostControlLoop.ChannelState.{name} 找不到（改名了？验收仿真 AcceptTimeWarpTests 反射它）");
            Assert.Equal(typeof(PidController), f!.FieldType);
        }

        // E0 金样台子自己的反射口
        Field(typeof(DuoSession), "_rd", typeof(Rd105Session));
        Field(typeof(Rd105LoopRecorder), "_pendingNote", typeof(string));
        var rec = typeof(Rd105TemperatureControl).GetProperty("Recorder", Priv);
        Assert.True(rec is not null && rec.PropertyType == typeof(Rd105LoopRecorder) && rec.SetMethod is not null,
            "Rd105TemperatureControl.Recorder（internal、可写）找不到——E0 金样台子靠它接住逐拍记录 Note");
    }

    private static FieldInfo Field(Type owner, string name, Type? type)
    {
        var f = owner.GetField(name, Priv);
        Assert.True(f is not null, $"{owner.Name}.{name} 找不到（改名了？验收仿真 / 金样台子反射它）");
        if (type is not null) Assert.Equal(type, f!.FieldType);
        return f!;
    }
}
