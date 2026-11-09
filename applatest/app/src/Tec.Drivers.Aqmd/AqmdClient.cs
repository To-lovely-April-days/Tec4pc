using System.Text;
using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.Aqmd;

/// <summary>设备描述信息（0x0000~0x000F）。</summary>
public sealed record AqmdIdentity(ushort DeviceId, string Version, string Name, int PwmHz, double MaxOutputCurrentA)
{
    /// <summary>「AQMD2408BLS-M2 v1.3」——探测回显、开机日志都这么叫它。</summary>
    public string Display => $"{(Name.Length > 0 ? Name : $"设备 0x{DeviceId:X4}")} v{Version}";
}

/// <summary>电机控制参数（0x0060~0x007B）里用得上的几项。</summary>
public sealed record AqmdMotorParams(
    double MaxFrequencyHz, ushort MaxAccelReg, ushort MaxDecelReg,
    double RatedCurrentA, double MaxLoadCurrentA, int LoopAlgorithm,
    int Poles, double GearRatio, bool Learned, bool SetpointX10);

/// <summary>一拍实时状态（0x0020~0x0035）。FrequencyHz 有符号：负 = 反转。</summary>
public sealed record AqmdStatus(double PwmPercent, double CurrentA, double FrequencyHz, int Stall, int Fault, ushort RpmRaw, bool RpmX10);

/// <summary>系统参数（0x0080~0x009F）里探测要摆给人看的几项。</summary>
public sealed record AqmdSystemParams(int Baud, int ParityMode, int SlaveAddress, double WatchdogSeconds,
                                      double StallStopSeconds, bool AlarmsDisabled, int DefaultSpeedMode)
{
    public string FrameName => ParityMode switch { 0 => "无校验 + 2 停止位", 1 => "奇校验 + 1 停止位", 2 => "偶校验 + 1 停止位", _ => $"校验码 {ParityMode}" };
}

/// <summary>0x0040 的三种停法。搅拌停机一律「正常停止」（按 0x0053 的减速度减下来）。</summary>
public enum AqmdStopMode : ushort { Normal = 0, EmergencyBrake = 1, Coast = 2 }

/// <summary>
/// AQMD 驱动器的访问层：Modbus 一问一答 → 工程量。语义全在这里，会话只管「什么时候读、读到了干什么」。
/// 建在 ModbusRtuClient 上，假从站（FakeModbusSlave）一插就能整链回归，不插硬件。
/// </summary>
public sealed class AqmdClient
{
    private readonly ModbusRtuClient _mb;

    public AqmdClient(ModbusRtuClient modbus) => _mb = modbus;

    public ModbusRtuClient Modbus => _mb;

    public async Task<AqmdIdentity> ReadIdentityAsync(CancellationToken ct)
    {
        var r = await _mb.ReadHoldingRegistersAsync(AqmdRegisters.DeviceId, AqmdRegisters.IdentityCount, ct).ConfigureAwait(false);
        var name = new StringBuilder();
        for (var i = AqmdRegisters.NameStart; i < AqmdRegisters.NameStart + 8; i++)
        {
            var hi = (char)(r[i] >> 8); var lo = (char)(r[i] & 0xFF);
            if (hi == '\0') break; name.Append(hi);
            if (lo == '\0') break; name.Append(lo);
        }
        return new AqmdIdentity(r[0], $"{r[1] >> 8}.{r[1] & 0xFF}", name.ToString().Trim(),
                                r[AqmdRegisters.PwmHz], r[AqmdRegisters.MaxOutputCurrent] * 0.01);
    }

    public async Task<AqmdMotorParams> ReadMotorAsync(CancellationToken ct)
    {
        var r = await _mb.ReadHoldingRegistersAsync(AqmdRegisters.MotorStart, AqmdRegisters.MotorCount, ct).ConfigureAwait(false);
        ushort At(ushort addr) => r[addr - AqmdRegisters.MotorStart];
        return new AqmdMotorParams(
            MaxFrequencyHz: At(AqmdRegisters.MaxFrequency) * 0.1,
            MaxAccelReg: At(AqmdRegisters.MaxAccel),
            MaxDecelReg: At(AqmdRegisters.MaxDecel),
            RatedCurrentA: At(AqmdRegisters.RatedCurrent) * 0.01,
            MaxLoadCurrentA: At(AqmdRegisters.MaxLoadCurrent) * 0.01,
            LoopAlgorithm: At(AqmdRegisters.LoopAlgorithm),
            Poles: At(AqmdRegisters.Poles),
            GearRatio: At(AqmdRegisters.GearRatio) * 0.1,
            Learned: At(AqmdRegisters.Learned) == 1,
            SetpointX10: At(AqmdRegisters.SetpointX10) == 1);
    }

    public async Task<AqmdStatus> ReadStatusAsync(CancellationToken ct)
    {
        var r = await _mb.ReadHoldingRegistersAsync(AqmdRegisters.StatusStart, AqmdRegisters.StatusCount, ct).ConfigureAwait(false);
        ushort At(ushort addr) => r[addr - AqmdRegisters.StatusStart];
        var x10 = At(AqmdRegisters.RpmX10) == 1;
        return new AqmdStatus(
            PwmPercent: (short)At(AqmdRegisters.Pwm) * 0.1,
            CurrentA: At(AqmdRegisters.Current) * 0.01,
            FrequencyHz: AqmdRegisters.FrequencyOf(At(AqmdRegisters.Frequency), x10),
            Stall: At(AqmdRegisters.Stall),
            Fault: At(AqmdRegisters.Fault),
            RpmRaw: At(AqmdRegisters.Rpm),
            RpmX10: x10);
    }

    public async Task<AqmdSystemParams> ReadSystemAsync(CancellationToken ct)
    {
        var r = await _mb.ReadHoldingRegistersAsync(AqmdRegisters.SystemStart, AqmdRegisters.SystemCount, ct).ConfigureAwait(false);
        ushort At(ushort addr) => r[addr - AqmdRegisters.SystemStart];
        return new AqmdSystemParams(
            Baud: At(AqmdRegisters.BaudHi) << 16 | At(AqmdRegisters.BaudLo),
            ParityMode: At(AqmdRegisters.ParityMode),
            SlaveAddress: At(AqmdRegisters.SlaveAddress),
            WatchdogSeconds: At(AqmdRegisters.CommWatchdog) * 0.1,
            StallStopSeconds: At(AqmdRegisters.StallStopTime) * 0.1,
            AlarmsDisabled: At(AqmdRegisters.AlarmsDisabled) == 1,
            DefaultSpeedMode: At(AqmdRegisters.DefaultSpeedMode));
    }

    /// <summary>写速度闭环目标（0x0043，有符号）。写它本身就把驱动器切到速度闭环方式（手册 §4.5.1）。</summary>
    public Task WriteSpeedAsync(short reg, CancellationToken ct)
        => _mb.WriteRegisterAsync(AqmdRegisters.SpeedSetpoint, unchecked((ushort)reg), ct);

    public Task StopAsync(AqmdStopMode mode, CancellationToken ct)
        => _mb.WriteRegisterAsync(AqmdRegisters.StopCmd, (ushort)mode, ct);

    /// <summary>加减速加速度一帧写两个（0x0052 / 0x0053 相邻，手册写明支持 0x10）。临时值，不存。</summary>
    public Task WriteAccelAsync(ushort accelReg, ushort decelReg, CancellationToken ct)
        => _mb.WriteRegistersAsync(AqmdRegisters.Accel, new[] { accelReg, decelReg }, ct);

    public Task WritePolesAsync(ushort poles, CancellationToken ct)
        => _mb.WriteRegisterAsync(AqmdRegisters.Poles, poles, ct);

    /// <summary>额定电流与最大负载电流一起写成同一个值（手册 §3.2 第 5 步：如无要求两者相同）。</summary>
    public Task WriteRatedCurrentAsync(ushort centiAmps, CancellationToken ct)
        => _mb.WriteRegistersAsync(AqmdRegisters.RatedCurrent, new[] { centiAmps, centiAmps }, ct);

    public Task WriteWatchdogAsync(ushort tenths, CancellationToken ct)
        => _mb.WriteRegisterAsync(AqmdRegisters.CommWatchdog, tenths, ct);

    public Task WriteStallStopAsync(ushort tenths, CancellationToken ct)
        => _mb.WriteRegisterAsync(AqmdRegisters.StallStopTime, tenths, ct);

    /// <summary>06 写的参数是临时的，写 1 到存储寄存器才落盘（手册 6.3.11）。</summary>
    public Task StoreMotorParamsAsync(CancellationToken ct) => _mb.WriteRegisterAsync(AqmdRegisters.StoreMotorParams, 1, ct);
    public Task StoreSystemParamsAsync(CancellationToken ct) => _mb.WriteRegisterAsync(AqmdRegisters.StoreSystemParams, 1, ct);
}
