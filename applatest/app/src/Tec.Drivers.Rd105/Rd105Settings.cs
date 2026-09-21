using System.Globalization;
using TecControl.Core;
using TecControl.Core.Comm;
using TecControl.Core.Protocol;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// RD105 温控器的参数面板（IDeviceSettings）：把协议 §3 里那些寄存器按四组摆出来——
/// 「实时状态」（两路温度 / 目标 / 输出 / 电流 / 使能 / 自整定、自身温度、告警），
/// 「工位 A · TC1」「工位 B · TC2」（输出模式、最大功率 LIMITED、最大电流、斜率、超温、PID、
/// 开机电源模式……），「温控器」（型号、固件、地址、波特率、自身过温阈值、PWM 频率、模式选择）。
///
/// 读的是设备此刻的值，写直接写设备（TecClient 写完会拿回显核对，不符就抛）；
/// 超温上下限 / 最大电流三项写完顺手同步回台面配置——不然下次「连接」ApplyProtection
/// 又按台面配置写回去，面板上改的就白改了。
///
/// 自整定：这里发的是**温控器自己的 AUTOPID=1**——控温跑的是温控器内部 PID（KP/KI/KD），
/// 它整定完直接改写这三个寄存器，改的就是真正在用的那套。上位机那套继电器法整定
/// （Rd105Tuning / HostControlLoop）算出来的是主机侧回路的增益，当前控温不走主机回路，
/// 所以面板上不摆它，免得整了半天数落在没人用的地方。
/// </summary>
public sealed class Rd105Settings : IDeviceSettings
{
    public const string GroupStatus = "st";
    public const string GroupTc1 = "tc1";
    public const string GroupTc2 = "tc2";
    public const string GroupSys = "sys";

    // 实时状态的键（前缀 tc1./tc2.）
    public const string KTemp = "temp", KTarget = "target", KDuty = "duty", KCurrent = "current",
                        KEnable = "enable", KAutoPid = "autopid";
    public const string KSysTemp = "sys.temp", KFault = "sys.fault";

    // 通道参数的键
    public const string KMode = "mode", KPol = "pidpol", KLimited = "limited", KSetCurrent = "setcurrent",
                        KSpeed = "speed", KOverUp = "overup", KOverLow = "overlow",
                        KKp = "kp", KKi = "ki", KKd = "kd", KPowerMode = "powermode",
                        KStartupDelay = "startupdelay", KFdeadV = "fdeadv", KBdeadV = "bdeadv", KOnSensor = "onsensor";

    // 温控器参数的键
    public const string KModel = "model", KFirmware = "firmware", KAddress = "address",
                        KBaudTtl = "baudttl", KBaud485 = "baud485",
                        KOverTvpt = "overtvpt", KOverTTemp = "overttemp", KContMode = "contmode", KFpwm = "fpwm";

    public const string ActTune1 = "tune1", ActTune2 = "tune2", ActTuneStop1 = "tunestop1", ActTuneStop2 = "tunestop2";

    public static readonly string[] ModeOptions = { "双向（制冷 + 加热）", "只制冷", "只加热", "通信设定输出百分比" };
    public static readonly string[] PolOptions = { "正向", "反向" };
    public static readonly string[] PowerModeOptions = { "跟随断电前状态", "上电即输出", "上电关闭输出" };
    public static readonly string[] OnSensorOptions = { "不处理", "传感器断线 / 短路时关输出" };
    public static readonly string[] OverTTempOptions = { "越限时输出继续", "越限时关闭输出" };
    public static readonly string[] ContModeOptions =
    {
        "各通道独立", "TC1 目标 = TC2 实测 + TC1 设定", "TC2 输出跟随 TC1", "TC1 串 TC2 且 TC2 输出跟随 TC1"
    };
    public static readonly string[] FpwmOptions = { "0.5 Hz", "1 Hz", "10 Hz", "100 Hz" };
    private static readonly string[] BaudNames = { "4800", "9600", "19200", "38400", "57600", "115200", "230400", "460800" };

    private readonly TecClient _client;
    private readonly TecController _ctl;
    private readonly ParameterSet _config;
    private readonly Action<string, string>? _log;

    public Rd105Settings(Rd105Link link, ParameterSet config, Action<string, string>? log)
    {
        _client = link.Client;
        _ctl = link.Controller;
        _config = config;
        _log = log;
        Groups = new[]
        {
            new SettingsGroup(GroupStatus, "实时状态", StatusSchema())
            {
                Live = true,
                Tip = "每两秒从温控器读一遍。输出是 PWMDUTY（±100 %）：正的走加热 PWM 引脚、负的走制冷 PWM 引脚——" +
                      "切到电加热那一侧时负输出那条路上没有负载，电流会接近 0。电流是 CURRENT 实测值。" +
                      "带斜率的升温，温控器内部的目标是从原来的目标慢慢爬上去的：头一两分钟夹套若略高于爬坡起点，" +
                      "会先出一小段负输出，属正常；两分钟后还是负的、夹套不往上走，查输出极性 / 输出模式 / 接线。" +
                      "自整定状态是 AUTOPID：1 = 温控器正在整定，整定完它自己回 0 并改写 KP/KI/KD"
            },
            new SettingsGroup(GroupTc1, "工位 A · TC1", ChannelSchema()) { Tip = ChannelTip },
            new SettingsGroup(GroupTc2, "工位 B · TC2", ChannelSchema()) { Tip = ChannelTip },
            new SettingsGroup(GroupSys, "温控器", SysSchema())
            {
                Tip = "地址与波特率只显示不改：改了这条链路当场就断，要改在台面属性栏的连接参数里一起改。" +
                      "自身过温阈值（OVERTVPT）：温控器自己到这个温度就开始降功率"
            }
        };
        Actions = new[]
        {
            new SettingsAction(ActTune1, "TC1 自整定") { Group = GroupTc1, Confirm = true, Tip = TuneTip },
            new SettingsAction(ActTuneStop1, "停止 TC1 自整定") { Group = GroupTc1, Tip = "AUTOPID 写回 0" },
            new SettingsAction(ActTune2, "TC2 自整定") { Group = GroupTc2, Confirm = true, Tip = TuneTip },
            new SettingsAction(ActTuneStop2, "停止 TC2 自整定") { Group = GroupTc2, Tip = "AUTOPID 写回 0" }
        };
    }

    private const string ChannelTip =
        "「最大输出占空比」（LIMITED）就是这一路 TEC 的最大功率——输出电压不超过供电的这个百分比，协议上限 90。" +
        "PID 三个系数是温控器内部单位（协议没给物理量纲，出厂 3000 / 150 / 0），自整定完它自己改写。" +
        "超温上下限与最大电流写进温控器的保护寄存器，断了通信照样生效；在这里改了会同步到台面配置。" +
        "改完点「写入」：只写改过的项，温控器回显核对，不符会报出来";

    private const string TuneTip =
        "温控器自己的 PID 自整定（AUTOPID=1）：会在当前目标温度附近激起振荡，要有人在场；" +
        "整定完它自动回 0 并改写这一路的 KP/KI/KD——完了点「读取」看新值";

    public IReadOnlyList<SettingsGroup> Groups { get; }
    public IReadOnlyList<SettingsAction> Actions { get; }

    // ── schema ──────────────────────────────────────────────────────────

    private static ParameterSchema StatusSchema()
    {
        var f = new List<FieldSpec>();
        foreach (var tc in new[] { 1, 2 })
        {
            var w = tc == 1 ? "A" : "B";
            f.Add(Ro(Field.Num($"tc{tc}.{KTemp}", $"工位 {w} 夹套温度", 0, "℃", step: 0.01)));
            f.Add(Ro(Field.Num($"tc{tc}.{KTarget}", $"工位 {w} 目标", 0, "℃", step: 0.01)));
            f.Add(Ro(Field.Num($"tc{tc}.{KDuty}", $"工位 {w} 输出", 0, "%", step: 0.1)));
            f.Add(Ro(Field.Num($"tc{tc}.{KCurrent}", $"工位 {w} 电流", 0, "A", step: 0.001)));
            f.Add(Ro(Field.Text($"tc{tc}.{KEnable}", $"工位 {w} 输出使能", "")));
            // 输出为负、温度却该往上走的时候，先看这三样：模式、极性、斜率——现场问「输出是负的正常吗」就是这一眼
            f.Add(Ro(Field.Text($"tc{tc}.{KMode}", $"工位 {w} 输出模式", "")));
            f.Add(Ro(Field.Text($"tc{tc}.{KPol}", $"工位 {w} 输出极性", "")));
            f.Add(Ro(Field.Num($"tc{tc}.{KSpeed}", $"工位 {w} 变温斜率", 0, "℃/min", step: 0.01)));
            f.Add(Ro(Field.Text($"tc{tc}.{KAutoPid}", $"工位 {w} 自整定", "")));
        }
        f.Add(Ro(Field.Num(KSysTemp, "温控器自身温度", 0, "℃", step: 1)));
        f.Add(Ro(Field.Text(KFault, "告警", "")));
        return new ParameterSchema(f);
    }

    private static ParameterSchema ChannelSchema() => new(new[]
    {
        Field.Sel(KMode, "输出模式", ModeOptions, ModeOptions[0]) with { Tip = "MODE：0 双向、1 只制冷、2 只加热、3 由通信直接给输出百分比" },
        Field.Sel(KPol, "输出极性", PolOptions, PolOptions[0]) with { Tip = "PIDPOL：TEC 接反了改这个，别改线" },
        Field.Num(KLimited, "最大输出占空比", 30, "%", 0, 90, 1) with { Tip = "LIMITED：这一路的最大功率，输出电压不超过供电的这个百分比" },
        Field.Num(KSetCurrent, "最大输出电流", 5, "A", 0.5, 15, 0.1) with { Tip = "SETCURRENT：到了就限流（告警字里能看到「限流中」）。同步到台面配置的「最大电流」" },
        Field.Num(KSpeed, "温度变化斜率", 0, "℃/s", 0, 10, 0.001) with { Tip = "SPEED：0 = 不限。配方的升温速率下发时会覆盖它" },
        Field.Num(KOverUp, "超温上限", 180, "℃", -80, 300, 1) with { Tip = "OVERTEMPUP：传感器温度越过它按「传感器过温保护模式」处理。同步到台面配置" },
        Field.Num(KOverLow, "超温下限", -40, "℃", -80, 300, 1) with { Tip = "OVERTEMPLOWER。同步到台面配置" },
        Field.Num(KKp, "PID · P", 3000, "", 0, 9_000_000, 1) with { Tip = "KP：温控器内部单位，出厂 3000" },
        Field.Num(KKi, "PID · I", 150, "", 0, 9_000_000, 1) with { Tip = "KI：出厂 150" },
        Field.Num(KKd, "PID · D", 0, "", 0, 9_000_000, 1) with { Tip = "KD：出厂 0" },
        Field.Sel(KPowerMode, "开机电源模式", PowerModeOptions, PowerModeOptions[0]) with { Tip = "POWERMODE：上位机控温建议「上电关闭输出」——断电重启后没人管着别自己带旧输出复活" },
        Field.Num(KStartupDelay, "开机延迟", 3, "s", 3, 180, 1) with { Tip = "STARTUPDELAY：断电前在输出的话，下次开机延迟几秒再启动" },
        Field.Num(KFdeadV, "正向启动电压", 0, "%", 0, 2, 0.005) with { Tip = "FDEADV：正向输出的初始电压百分比" },
        Field.Num(KBdeadV, "反向启动电压", 0, "%", 0, 2, 0.005) with { Tip = "BDEADV：反向输出的初始电压百分比" },
        Field.Sel(KOnSensor, "传感器保护", OnSensorOptions, OnSensorOptions[1]) with { Tip = "ONSENSOR：没接传感器或传感器短路时要不要停输出" }
    });

    private static ParameterSchema SysSchema() => new(new[]
    {
        Ro(Field.Text(KModel, "型号", "")),
        Ro(Field.Text(KFirmware, "固件版本", "")),
        Ro(Field.Num(KAddress, "485 地址", 1, "", step: 1)),
        Ro(Field.Text(KBaudTtl, "TTL 口波特率", "")),
        Ro(Field.Text(KBaud485, "RS485 口波特率", "")),
        Field.Num(KOverTvpt, "自身过温阈值", 70, "℃", 40, 100, 1) with { Tip = "OVERTVPT：温控器自己到这个温度开始逐步降功率，出厂 70" },
        Field.Sel(KOverTTemp, "传感器过温保护模式", OverTTempOptions, OverTTempOptions[1]) with { Tip = "OVERTTEMP：传感器温度越过超温上下限时，输出继续还是关闭。出厂关闭" },
        Field.Sel(KContMode, "温控器模式选择", ContModeOptions, ContModeOptions[0]) with { Tip = "CONTMODE：两路独立还是串起来。本机两路各管一个工位，用「各通道独立」" },
        Field.Sel(KFpwm, "PWM 输出频率", FpwmOptions, FpwmOptions[2]) with { Tip = "FPWM：电流脉动越小 TEC 效率越高，低温工况建议高频档；出厂 10 Hz" }
    });

    private static FieldSpec Ro(FieldSpec f) => f with { ReadOnly = true };

    // ── 读 ──────────────────────────────────────────────────────────────

    public async Task<ParameterSet> ReadAsync(string groupId, CancellationToken ct)
    {
        var p = new ParameterSet();
        switch (groupId)
        {
            case GroupStatus:
            {
                var snap = await _ctl.ReadSnapshotAsync(ct).ConfigureAwait(false);
                foreach (var tc in new[] { 1, 2 })
                {
                    p[$"tc{tc}.{KTemp}"] = tc == 1 ? snap.Temp1C : snap.Temp2C;
                    p[$"tc{tc}.{KTarget}"] = TecScale.TempFromRaw(await Q(tc, TecCmd.Target, ct).ConfigureAwait(false));
                    p[$"tc{tc}.{KDuty}"] = TecScale.DutyPercentFromRaw(await Q(tc, TecCmd.PwmDuty, ct).ConfigureAwait(false));
                    p[$"tc{tc}.{KCurrent}"] = TecScale.CurrentFromRaw(await Q(tc, TecCmd.Current, ct).ConfigureAwait(false));
                    p[$"tc{tc}.{KEnable}"] = await Q(tc, TecCmd.Enable, ct).ConfigureAwait(false) != 0 ? "开" : "关";
                    p[$"tc{tc}.{KMode}"] = Opt(ModeOptions, await Q(tc, TecCmd.Mode, ct).ConfigureAwait(false));
                    p[$"tc{tc}.{KPol}"] = Opt(PolOptions, await Q(tc, TecCmd.OutputPolarity, ct).ConfigureAwait(false));
                    // SPEED 是 ℃/s，面板上按 ℃/min 说——配方和 HMI 都是 ℃/min
                    p[$"tc{tc}.{KSpeed}"] = TecScale.SpeedFromRaw(await Q(tc, TecCmd.Speed, ct).ConfigureAwait(false)) * 60;
                    var ap = await Q(tc, TecCmd.AutoPid, ct).ConfigureAwait(false);
                    p[$"tc{tc}.{KAutoPid}"] = ap switch { 1 => "进行中（AUTOPID=1）", 2 => "实时自动优化（AUTOPID=2）", _ => "未整定 / 已完成（AUTOPID=0）" };
                }
                p[KSysTemp] = snap.InternalTempC;
                var code = await _ctl.ReadErrorCodeAsync(ct).ConfigureAwait(false);
                var texts = code.Describe();
                p[KFault] = texts.Count == 0 ? $"无（ERRORCODE=0）" : $"{string.Join("；", texts)}（ERRORCODE={(ushort)code}）";
                break;
            }
            case GroupTc1 or GroupTc2:
            {
                var tc = groupId == GroupTc1 ? 1 : 2;
                p[KMode] = Opt(ModeOptions, await Q(tc, TecCmd.Mode, ct).ConfigureAwait(false));
                p[KPol] = Opt(PolOptions, await Q(tc, TecCmd.OutputPolarity, ct).ConfigureAwait(false));
                p[KLimited] = (double)await Q(tc, TecCmd.MaxDuty, ct).ConfigureAwait(false);
                p[KSetCurrent] = TecScale.MaxCurrentFromRaw(await Q(tc, TecCmd.SetCurrent, ct).ConfigureAwait(false));
                p[KSpeed] = TecScale.SpeedFromRaw(await Q(tc, TecCmd.Speed, ct).ConfigureAwait(false));
                p[KOverUp] = TecScale.TempFromRaw(await Q(tc, TecCmd.OverTempUp, ct).ConfigureAwait(false));
                p[KOverLow] = TecScale.TempFromRaw(await Q(tc, TecCmd.OverTempLower, ct).ConfigureAwait(false));
                p[KKp] = (double)await Q(tc, TecCmd.Kp, ct).ConfigureAwait(false);
                p[KKi] = (double)await Q(tc, TecCmd.Ki, ct).ConfigureAwait(false);
                p[KKd] = (double)await Q(tc, TecCmd.Kd, ct).ConfigureAwait(false);
                p[KPowerMode] = Opt(PowerModeOptions, await Q(tc, TecCmd.PowerMode, ct).ConfigureAwait(false));
                p[KStartupDelay] = (double)await Q(tc, "STARTUPDELAY", ct).ConfigureAwait(false);
                p[KFdeadV] = await Q(tc, "FDEADV", ct).ConfigureAwait(false) / 200.0;
                p[KBdeadV] = await Q(tc, "BDEADV", ct).ConfigureAwait(false) / 200.0;
                p[KOnSensor] = Opt(OnSensorOptions, await Q(tc, TecCmd.OnSensor, ct).ConfigureAwait(false));
                break;
            }
            case GroupSys:
            {
                var (model, firmware, _) = await _ctl.ReadDeviceInfoAsync(ct).ConfigureAwait(false);
                p[KModel] = model;
                p[KFirmware] = firmware;
                p[KAddress] = (double)await Q(null, "ADDRESS", ct).ConfigureAwait(false);
                p[KBaudTtl] = Baud(await Q(null, "BOUNDTABLEONE", ct).ConfigureAwait(false));
                p[KBaud485] = Baud(await Q(null, "BOUNDTABLETWO", ct).ConfigureAwait(false));
                p[KOverTvpt] = (double)await Q(null, "OVERTVPT", ct).ConfigureAwait(false);
                p[KOverTTemp] = Opt(OverTTempOptions, await Q(null, "OVERTTEMP", ct).ConfigureAwait(false));
                p[KContMode] = Opt(ContModeOptions, await Q(null, TecCmd.ControllerMode, ct).ConfigureAwait(false));
                p[KFpwm] = Opt(FpwmOptions, await Q(null, TecCmd.PwmFrequency, ct).ConfigureAwait(false));
                break;
            }
            default:
                throw new ArgumentException($"没有这一组：{groupId}", nameof(groupId));
        }
        return p;
    }

    // ── 写 ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> WriteAsync(string groupId, ParameterSet values, CancellationToken ct)
    {
        var notes = new List<string>();
        var group = Groups.FirstOrDefault(g => g.Id == groupId)
                    ?? throw new ArgumentException($"没有这一组：{groupId}", nameof(groupId));
        if (group.Live) { notes.Add("「实时状态」都是只读的，没有可写的项"); return notes; }

        var tc = groupId == GroupTc1 ? 1 : groupId == GroupTc2 ? 2 : (int?)null;
        foreach (var key in values.Keys.ToList())
        {
            var spec = group.Schema.Find(key);
            if (spec is null) { notes.Add($"{key}：这一组里没有这一项，没写"); continue; }
            if (spec.ReadOnly) { notes.Add($"{spec.Label}：只读，没写"); continue; }
            try
            {
                var note = await WriteOneAsync(tc, spec, values, ct).ConfigureAwait(false);
                notes.Add(note);
                _log?.Invoke("info", $"温控器参数：{note}");
            }
            catch (TecProtocolException ex)
            {
                // TecClient 写完拿回显核对，不符就抛——多半是设备把值钳到了它的范围里
                notes.Add($"{spec.Label}：温控器没照写（{ex.Message}）");
                _log?.Invoke("warn", $"温控器参数：{spec.Label} 没写成——{ex.Message}");
            }
        }
        return notes;
    }

    private async Task<string> WriteOneAsync(int? tc, FieldSpec spec, ParameterSet v, CancellationToken ct)
    {
        var who = tc is null ? "" : $"TC{tc} ";
        switch (spec.Key)
        {
            case KMode:        return await SetOpt(tc, TecCmd.Mode, ModeOptions, v.Str(spec.Key), $"{who}输出模式", ct).ConfigureAwait(false);
            case KPol:         return await SetOpt(tc, TecCmd.OutputPolarity, PolOptions, v.Str(spec.Key), $"{who}输出极性", ct).ConfigureAwait(false);
            case KPowerMode:   return await SetOpt(tc, TecCmd.PowerMode, PowerModeOptions, v.Str(spec.Key), $"{who}开机电源模式", ct).ConfigureAwait(false);
            case KOnSensor:    return await SetOpt(tc, TecCmd.OnSensor, OnSensorOptions, v.Str(spec.Key), $"{who}传感器保护", ct).ConfigureAwait(false);
            case KOverTTemp:   return await SetOpt(null, "OVERTTEMP", OverTTempOptions, v.Str(spec.Key), "传感器过温保护模式", ct).ConfigureAwait(false);
            case KContMode:    return await SetOpt(null, TecCmd.ControllerMode, ContModeOptions, v.Str(spec.Key), "温控器模式选择", ct).ConfigureAwait(false);
            case KFpwm:        return await SetOpt(null, TecCmd.PwmFrequency, FpwmOptions, v.Str(spec.Key), "PWM 输出频率", ct).ConfigureAwait(false);

            case KLimited:
            {
                var pct = (int)Math.Clamp(Math.Round(v.Num(spec.Key)), 0, 90);
                await _client.SetAsync(tc, TecCmd.MaxDuty, pct, ct).ConfigureAwait(false);
                return $"{who}最大输出占空比 = {pct} %（LIMITED={pct}）";
            }
            case KSetCurrent:
            {
                var amps = Math.Clamp(v.Num(spec.Key), 0.5, 15);
                await _ctl.SetMaxCurrentAsync(tc!.Value, amps, ct).ConfigureAwait(false);
                _config[Rd105TecDriver.FieldMaxCurrent] = amps;
                return $"{who}最大输出电流 = {amps:0.0} A（SETCURRENT={TecScale.MaxCurrentToRaw(amps)}，已同步到台面配置）";
            }
            case KSpeed:
            {
                var s = Math.Clamp(v.Num(spec.Key), 0, 10);
                await _ctl.SetSpeedAsync(tc!.Value, s, ct).ConfigureAwait(false);
                return $"{who}温度变化斜率 = {s:0.###} ℃/s（SPEED={TecScale.SpeedToRaw(s)}）";
            }
            case KOverUp:
            {
                var c = v.Num(spec.Key);
                await _client.SetAsync(tc, TecCmd.OverTempUp, TecScale.TempToRaw(c), ct).ConfigureAwait(false);
                _config[Rd105TecDriver.FieldOverUp] = c;
                return $"{who}超温上限 = {c:0.0} ℃（已同步到台面配置）";
            }
            case KOverLow:
            {
                var c = v.Num(spec.Key);
                await _client.SetAsync(tc, TecCmd.OverTempLower, TecScale.TempToRaw(c), ct).ConfigureAwait(false);
                _config[Rd105TecDriver.FieldOverLow] = c;
                return $"{who}超温下限 = {c:0.0} ℃（已同步到台面配置）";
            }
            case KKp or KKi or KKd:
            {
                var name = spec.Key switch { KKp => TecCmd.Kp, KKi => TecCmd.Ki, _ => TecCmd.Kd };
                var raw = (long)Math.Clamp(Math.Round(v.Num(spec.Key)), 0, 9_000_000);
                await _client.SetAsync(tc, name, raw, ct).ConfigureAwait(false);
                return $"{who}{name} = {raw}";
            }
            case KStartupDelay:
            {
                var s = (long)Math.Clamp(Math.Round(v.Num(spec.Key)), 3, 180);
                await _client.SetAsync(tc, "STARTUPDELAY", s, ct).ConfigureAwait(false);
                return $"{who}开机延迟 = {s} s";
            }
            case KFdeadV or KBdeadV:
            {
                var name = spec.Key == KFdeadV ? "FDEADV" : "BDEADV";
                var raw = (long)Math.Clamp(Math.Round(v.Num(spec.Key) * 200), 0, 400);
                await _client.SetAsync(tc, name, raw, ct).ConfigureAwait(false);
                return $"{who}{(spec.Key == KFdeadV ? "正向" : "反向")}启动电压 = {raw / 200.0:0.###} %（{name}={raw}）";
            }
            case KOverTvpt:
            {
                var c = (long)Math.Clamp(Math.Round(v.Num(spec.Key)), 40, 100);
                await _client.SetAsync(null, "OVERTVPT", c, ct).ConfigureAwait(false);
                return $"自身过温阈值 = {c} ℃（OVERTVPT={c}）";
            }
            default:
                return $"{spec.Label}：不认识这一项，没写";
        }
    }

    private async Task<string> SetOpt(int? tc, string name, string[] options, string text, string label, CancellationToken ct)
    {
        var idx = Array.IndexOf(options, text);
        if (idx < 0)
            // 老台面 / 手填的数字也认
            idx = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 && n < options.Length ? n : -1;
        if (idx < 0) return $"{label}：「{text}」不在选项里，没写";
        await _client.SetAsync(tc, name, idx, ct).ConfigureAwait(false);
        return $"{label} = {options[idx]}（{name}={idx}）";
    }

    // ── 动作 ────────────────────────────────────────────────────────────

    public async Task<string> RunAsync(string actionId, CancellationToken ct)
    {
        switch (actionId)
        {
            case ActTune1 or ActTune2:
            {
                var tc = actionId == ActTune1 ? 1 : 2;
                var enabled = await Q(tc, TecCmd.Enable, ct).ConfigureAwait(false) != 0;
                if (!enabled)
                    return $"TC{tc} 输出是关的（ENABLE=0）——自整定要在控温跑着的时候做：先在 HMI 或配方里把这一路的目标温度下发、输出打开，再来";
                await _ctl.StartAutoTuneAsync(tc, ct).ConfigureAwait(false);
                _log?.Invoke("warn", $"温控器 TC{tc} 自整定已启动（AUTOPID=1），由操作人在参数面板发起");
                return $"TC{tc} 自整定已启动（AUTOPID=1）。温控器会在目标温度附近激起振荡，整定完自动回 0 并改写 KP/KI/KD——" +
                       "「实时状态」里看进度，完了到这一组点「读取」看新参数";
            }
            case ActTuneStop1 or ActTuneStop2:
            {
                var tc = actionId == ActTuneStop1 ? 1 : 2;
                await _client.SetAsync(tc, TecCmd.AutoPid, 0, ct).ConfigureAwait(false);
                _log?.Invoke("info", $"温控器 TC{tc} 自整定已停止（AUTOPID=0）");
                return $"TC{tc} 自整定已停止（AUTOPID=0）";
            }
            default:
                throw new ArgumentException($"没有这个动作：{actionId}", nameof(actionId));
        }
    }

    // ── 小工具 ──────────────────────────────────────────────────────────

    private Task<long> Q(int? tc, string name, CancellationToken ct) => _client.QueryAsync(tc, name, ct);

    private static string Opt(string[] options, long raw)
        => raw >= 0 && raw < options.Length ? options[(int)raw] : $"{raw}（协议外的值）";

    private static string Baud(long code)
        => code >= 0 && code < BaudNames.Length ? $"{BaudNames[code]}（表 {code}）" : $"表 {code}（协议外）";
}
