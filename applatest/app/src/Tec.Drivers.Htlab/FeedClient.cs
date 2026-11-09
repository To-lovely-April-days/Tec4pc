using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.Htlab;

/// <summary>一拍全量快照（寄存器 0~48）。</summary>
public sealed record FeedSnapshot(
    double MfcSetpoint, double MfcFlow, double MfcTotal,
    double Pump1Setpoint, double Pump1Flow, double Pump1Total, double Pump1Pressure,
    double Pump2Setpoint, double Pump2Flow, double Pump2Total, double Pump2Pressure,
    double MfcRangeHigh, double MfcRangeLow, double MfcCorrection, double MfcDpt,
    double Pump1PressureHigh, double Pump1PressureLow, double Pump2PressureHigh, double Pump2PressureLow,
    bool MfcOn, bool Pump1On, bool Pump2On,
    bool Pump1PressureAlarm, bool Pump2PressureAlarm, bool MfcCommAlarm, bool Pump1CommAlarm, bool Pump2CommAlarm,
    bool AlarmActive)
{
    public bool AnyAlarm => AlarmActive || Pump1PressureAlarm || Pump2PressureAlarm || MfcCommAlarm || Pump1CommAlarm || Pump2CommAlarm;

    /// <summary>「泵 1 压力上限报警、泵 2 通信故障」——报警位挨个点名；没有就是空串。</summary>
    public string AlarmText()
    {
        var parts = new List<string>();
        if (Pump1PressureAlarm) parts.Add(FeedRegisters.AlarmName(FeedRegisters.Pump1PressureAlarm));
        if (Pump2PressureAlarm) parts.Add(FeedRegisters.AlarmName(FeedRegisters.Pump2PressureAlarm));
        if (MfcCommAlarm) parts.Add(FeedRegisters.AlarmName(FeedRegisters.MfcCommAlarm));
        if (Pump1CommAlarm) parts.Add(FeedRegisters.AlarmName(FeedRegisters.Pump1CommAlarm));
        if (Pump2CommAlarm) parts.Add(FeedRegisters.AlarmName(FeedRegisters.Pump2CommAlarm));
        if (parts.Count == 0 && AlarmActive) parts.Add("报警状态位为 1（具体哪一路没点名）");
        return string.Join("、", parts);
    }

    public double PumpSetpoint(int pump) => pump == 2 ? Pump2Setpoint : Pump1Setpoint;
    public double PumpFlow(int pump) => pump == 2 ? Pump2Flow : Pump1Flow;
    public double PumpTotal(int pump) => pump == 2 ? Pump2Total : Pump1Total;
    public double PumpPressure(int pump) => pump == 2 ? Pump2Pressure : Pump1Pressure;
    public bool PumpOn(int pump) => pump == 2 ? Pump2On : Pump1On;
}

/// <summary>进料系统的访问层：一帧读全 + 单点写（Float 走 0x10 两寄存器、Int 走 0x06）。假从站一插就能整链回归。</summary>
public sealed class FeedClient
{
    private readonly ModbusRtuClient _mb;
    public FeedClient(ModbusRtuClient mb) => _mb = mb;
    public ModbusRtuClient Modbus => _mb;

    public async Task<FeedSnapshot> ReadAllAsync(CancellationToken ct)
    {
        var r = await _mb.ReadHoldingRegistersAsync(0, FeedRegisters.AllCount, ct).ConfigureAwait(false);
        double F(ushort reg) => FeedRegisters.FloatOf(r[reg], r[reg + 1]);
        bool B(ushort reg) => r[reg] == 1;
        return new FeedSnapshot(
            F(FeedRegisters.MfcSetpoint), F(FeedRegisters.MfcFlow), F(FeedRegisters.MfcTotal),
            F(FeedRegisters.Pump1Setpoint), F(FeedRegisters.Pump1Flow), F(FeedRegisters.Pump1Total), F(FeedRegisters.Pump1Pressure),
            F(FeedRegisters.Pump2Setpoint), F(FeedRegisters.Pump2Flow), F(FeedRegisters.Pump2Total), F(FeedRegisters.Pump2Pressure),
            F(FeedRegisters.MfcRangeHigh), F(FeedRegisters.MfcRangeLow), F(FeedRegisters.MfcCorrection), F(FeedRegisters.MfcDpt),
            F(FeedRegisters.Pump1PressureHigh), F(FeedRegisters.Pump1PressureLow), F(FeedRegisters.Pump2PressureHigh), F(FeedRegisters.Pump2PressureLow),
            MfcOn: r[FeedRegisters.MfcOnOff] == 0,               // MFC 0 = 运行
            Pump1On: B(FeedRegisters.Pump1OnOff), Pump2On: B(FeedRegisters.Pump2OnOff),
            B(FeedRegisters.Pump1PressureAlarm), B(FeedRegisters.Pump2PressureAlarm), B(FeedRegisters.MfcCommAlarm),
            B(FeedRegisters.Pump1CommAlarm), B(FeedRegisters.Pump2CommAlarm),
            AlarmActive: B(FeedRegisters.AlarmStatus));
    }

    public async Task<double> ReadFloatAsync(ushort reg, CancellationToken ct)
    {
        var r = await _mb.ReadHoldingRegistersAsync(reg, 2, ct).ConfigureAwait(false);
        return FeedRegisters.FloatOf(r[0], r[1]);
    }

    public async Task<ushort> ReadInt16Async(ushort reg, CancellationToken ct)
        => (await _mb.ReadHoldingRegistersAsync(reg, 1, ct).ConfigureAwait(false))[0];

    public Task WriteFloatAsync(ushort reg, double value, CancellationToken ct)
    {
        var (hi, lo) = FeedRegisters.RegsOf((float)value);
        return _mb.WriteRegistersAsync(reg, new[] { hi, lo }, ct);
    }

    public Task WriteInt16Async(ushort reg, ushort value, CancellationToken ct) => _mb.WriteRegisterAsync(reg, value, ct);

    /// <summary>泵 1 / 泵 2 开停：1 运行、0 停止。</summary>
    public Task SetPumpAsync(int pump, bool on, CancellationToken ct)
        => WriteInt16Async(pump == 2 ? FeedRegisters.Pump2OnOff : FeedRegisters.Pump1OnOff, (ushort)(on ? 1 : 0), ct);

    /// <summary>MFC 开停：**0 运行、1 停止**（跟泵反着）。</summary>
    public Task SetMfcAsync(bool on, CancellationToken ct)
        => WriteInt16Async(FeedRegisters.MfcOnOff, (ushort)(on ? 0 : 1), ct);

    public Task SetPumpSetpointAsync(int pump, double mlPerMin, CancellationToken ct)
        => WriteFloatAsync(pump == 2 ? FeedRegisters.Pump2Setpoint : FeedRegisters.Pump1Setpoint, mlPerMin, ct);

    public Task ResetAlarmsAsync(CancellationToken ct) => WriteInt16Async(FeedRegisters.AlarmReset, 1, ct);
    public Task SilenceAlarmsAsync(CancellationToken ct) => WriteInt16Async(FeedRegisters.AlarmSilence, 1, ct);
}
