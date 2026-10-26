using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 冷水机主板（《主机触模屏通讯协议2023》）：Modbus-RTU，9600 8N1，站号 1。冷却水联动只用到三样：
/// 开机 / 关机（FC05 写 0x0200 / 0x0201 = FF00，协议 §2.2.1 A）、机组运行状态（0x0000，§2.2.3：
/// 0 空闲 / 1 准备启动 / 2 正常运行 / 3 延时停机 / 4 故障），外加开机时看一眼「起停方式」（0x0403，§2.3.1）：
/// 出厂 0 = 只能本地启停，远程开关机指令它不理。
///
/// 协议里读状态的功能码前后不一：表里写 04，应用实例给的是 03 的帧（CRC 对得上）。先按表发 04，
/// 从站回「非法功能码 / 地址」再换 03，换成了就一直用它。开关机同理：FC05 不认就换 FC06
/// （0x0200 写 0x0072 开机 / 0x0070 关机，§2.2.1 C）。**有的固件连异常码都不回、静默丢帧**——
/// 超时也换：03/06 两条路协议里都认，换错没有代价，不换就是永远读不到。
/// </summary>
public sealed class ChillerClient
{
    public const ushort StateAddr = 0x0000;
    public const ushort StartCoil = 0x0200;
    public const ushort StopCoil = 0x0201;
    public const ushort CommandReg = 0x0200;
    public const ushort StartValue = 0x0072;
    public const ushort StopValue = 0x0070;
    public const ushort StartStopModeAddr = 0x0403;

    public const int Idle = 0;
    public const int Fault = 4;

    private readonly ModbusRtuClient _bus;
    private volatile bool _readByHolding;
    private volatile bool _cmdByRegister;

    public ChillerClient(ModbusRtuClient bus) => _bus = bus;

    /// <summary>底下的 Modbus 主站——重试这些链路参数调它（探测时把重试关掉）。</summary>
    public ModbusRtuClient Bus => _bus;

    /// <summary>机组运行状态（0x0000）：0 空闲 / 1 准备启动 / 2 正常运行 / 3 延时停机 / 4 故障。</summary>
    public async Task<int> ReadStateAsync(CancellationToken ct = default)
    {
        if (!_readByHolding)
        {
            try { return (await _bus.ReadInputRegistersAsync(StateAddr, 1, ct).ConfigureAwait(false))[0]; }
            // 回异常码是「我不认这个功能码」；静默丢帧（连异常码都不回）同样是——
            // 03 协议应用实例里就认，换过去没有代价
            catch (ModbusException ex) when (ex.Code is 0x01 or 0x02) { _readByHolding = true; }
            catch (TimeoutException) { _readByHolding = true; }
        }
        return (await _bus.ReadHoldingRegistersAsync(StateAddr, 1, ct).ConfigureAwait(false))[0];
    }

    /// <summary>起停方式（0x0403）：0 本地 / 1 本地+远程 / 2 远程。0 时远程开关机不生效。</summary>
    public async Task<int> ReadStartStopModeAsync(CancellationToken ct = default)
        => (await _bus.ReadHoldingRegistersAsync(StartStopModeAddr, 1, ct).ConfigureAwait(false))[0];

    public Task StartAsync(CancellationToken ct = default) => CommandAsync(true, ct);
    public Task StopAsync(CancellationToken ct = default) => CommandAsync(false, ct);

    private async Task CommandAsync(bool start, CancellationToken ct)
    {
        if (!_cmdByRegister)
        {
            try
            {
                await _bus.WriteCoilAsync(start ? StartCoil : StopCoil, true, ct).ConfigureAwait(false);
                return;
            }
            // 同上：回异常码或静默丢帧都换 FC06 写命令字（0x0072 开机 / 0x0070 关机）
            catch (ModbusException ex) when (ex.Code is 0x01 or 0x02) { _cmdByRegister = true; }
            catch (TimeoutException) { _cmdByRegister = true; }
        }
        await _bus.WriteRegisterAsync(CommandReg, start ? StartValue : StopValue, ct).ConfigureAwait(false);
    }

    /// <summary>冷水机在转（泵在走水）：准备启动 / 正常运行。</summary>
    public static bool Running(int? state) => state is 1 or 2;

    public static string StateName(int? state) => state switch
    {
        null => "读不到",
        0 => "空闲",
        1 => "准备启动",
        2 => "正常运行",
        3 => "延时停机",
        4 => "故障",
        _ => $"未知状态 {state}"
    };
}
