using TecControl.Core.Comm;
using Tec.Drivers.Rd105.Modbus;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 艾莫迅 JY-MODBUS-IO8R：8 路继电器 DO（常开触点，每点 2A、4 点合计 8A）+
/// 8 路光耦 DI。在双工位反应主机里它管热源切换：DO 把 RD105 的输出通路从 TEC
/// 切到电加热（断开 = TEC，闭合 = 电加热——断电/总线复位时继电器落回断开位，
/// 落回的必须是安全侧），DI 接电加热接触器的辅助触点做切换反馈（若现场接了）。
///
/// 一个部署前提在这层代码里做不了、只能写在部署清单里（需求 §7）：
/// 模块的「总线错误模式」出厂默认是【保持】——断线时继电器停在原位，
/// 电加热会一直烧。必须用厂家配置工具改成【复位】。按过模块上的
/// Reset 键会恢复出厂参数，改完要复查。
/// </summary>
public sealed class Io8rClient
{
    public const int Points = 8;

    private readonly ModbusRtuClient _bus;

    public Io8rClient(ModbusRtuClient bus) => _bus = bus;

    /// <summary>读回 8 路继电器的当前位置（FC 01）。跟自己发过的命令核对用——
    /// 模块面板上有按键，人手也能扳继电器，读回来对不上要报出来。</summary>
    public Task<bool[]> ReadRelaysAsync(CancellationToken ct = default)
        => _bus.ReadCoilsAsync(0, Points, ct);

    /// <summary>读 8 路光耦 DI（FC 02）。</summary>
    public Task<bool[]> ReadInputsAsync(CancellationToken ct = default)
        => _bus.ReadDiscreteInputsAsync(0, Points, ct);

    /// <summary>切一路继电器（FC 05）。closed = true 吸合。</summary>
    public Task SetRelayAsync(int index, bool closed, CancellationToken ct = default)
    {
        if (index is < 0 or >= Points)
            throw new ArgumentOutOfRangeException(nameof(index), $"IO8R 只有 DO0~DO7，要切的是 DO{index}");
        return _bus.WriteCoilAsync((ushort)index, closed, ct);
    }

    /// <summary>八路全断，一帧完成（FC 0F）。走人时用它——
    /// 全断 = 断电落回的位置，且一帧比八帧少七次「中途断线切了一半」的机会。</summary>
    public Task AllOffAsync(CancellationToken ct = default)
        => _bus.WriteCoilsAsync(0, new bool[Points], ct);

    /// <summary>八路一帧写成给定的样子（FC 0F）。开机复位用它：加热棒继电器全断、
    /// TEC 功率线接通，一帧落定，不会停在「切了一半」。</summary>
    public Task SetAllAsync(bool[] closed, CancellationToken ct = default)
    {
        if (closed.Length != Points)
            throw new ArgumentException($"IO8R 有 {Points} 路 DO，给了 {closed.Length} 个值", nameof(closed));
        return _bus.WriteCoilsAsync(0, closed, ct);
    }
}

/// <summary>
/// 一条到 IO8R 的链路：串口 → Modbus 主站 → IO8R 客户端。
/// 与 Rd105Link 同一个模式：换掉 ISerialTransport 就能整链回归测试。
/// </summary>
public sealed class Io8rLink : IDisposable
{
    private readonly ISerialTransport _transport;

    public Io8rLink(ISerialTransport transport, byte station, int timeoutMs = 500)
    {
        _transport = transport;
        Bus = new ModbusRtuClient(transport, station, timeoutMs);
        Io = new Io8rClient(Bus);
    }

    public ModbusRtuClient Bus { get; }
    public Io8rClient Io { get; }

    public bool IsOpen => _transport.IsOpen;

    public void Open()
    {
        if (!_transport.IsOpen) _transport.Open();
    }

    public void Dispose() => _transport.Dispose();

    /// <summary>
    /// 真串口链路。帧格式固定 8N1——IO8R 就是 8N1 不可改，SerialPortTransport
    /// 也是这么写死的，正好。波特率/站号看模块上的拨码（出厂 9600 / 1 号）。
    /// </summary>
    public static Io8rLink Serial(string port, int baud, byte station)
        => new(new SerialPortTransport(port, baud), station);
}
