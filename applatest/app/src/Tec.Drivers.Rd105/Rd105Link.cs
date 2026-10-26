using TecControl.Core;
using TecControl.Core.Comm;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 一条到温控器的链路：串口 → TecClient → TecController。
/// 单独拎出来是为了能换成假串口——回归测试不需要插硬件，
/// 换掉 ISerialTransport 就能把整条链路跑一遍。
/// </summary>
public sealed class Rd105Link : IDisposable
{
    private readonly ISerialTransport _transport;

    /// <summary>
    /// 通信异常的应答整帧丢掉、清缓冲、重发同一条，直到拿到校验通过的——最多这么久（ms，用户定的）。
    /// 线真断了（拔线 / 掉电）也就卡这么久，之后照常报错、走断链处理。
    /// </summary>
    public const int RetryBudgetMs = 2000;

    public Rd105Link(ISerialTransport transport)
    {
        _transport = transport;
        Client = new TecClient(transport) { RetryBudgetMs = RetryBudgetMs };
        Controller = new TecController(Client);
        // Modbus-RTU 口：坏帧在桥下那层就按帧重发（只重发坏的那一帧，不把整条指令重来）；
        // 期限从同一时刻起算，桥下用完了上面 TecClient 也不会再叠一轮
        if (Bridge is { } b) b.Modbus.RetryBudgetMs = RetryBudgetMs;
    }

    public TecClient Client { get; }
    public TecController Controller { get; }

    /// <summary>串口上套的 Modbus 桥（协议选 Modbus-RTU 才有）；ASCII 直连时是 null。</summary>
    public Rd105ModbusBridge? Bridge => _transport as Rd105ModbusBridge;

    /// <summary>
    /// 探测前把两层链路的自动重发都关掉（桥下的 Modbus 层 + 上面的 TecClient 层）：
    /// 探测本身就是「换着试」的那层重试，底下再叠两层，每个不通的组合都要白等
    /// 好几倍超时——点「连接」半天没反应的体感就是它。会话照常留着重试不动。
    /// </summary>
    public void DisableRetriesForProbe()
    {
        Client.TimeoutRetries = 0;
        Client.CorruptRetries = 0;
        Client.RetryBudgetMs = 0;           // 按时长重试也关：探测一试一报
        if (Bridge is { } b)
        {
            b.Modbus.TimeoutRetries = 0;
            b.Modbus.CorruptRetries = 0;
            b.Modbus.RetryBudgetMs = 0;
        }
    }

    public bool IsOpen => _transport.IsOpen;

    public void Open()
    {
        if (!_transport.IsOpen) _transport.Open();
    }

    /// <summary>口子中途掉了之后重开（Rd105Session 轮询连着报错时经 LinkRecovery 调）。
    /// Modbus 桥套着的是里面那条真串口；假链路（测试）关了再开。</summary>
    public void Reopen()
    {
        switch (_transport)
        {
            case SerialPortTransport sp: sp.Reopen(); break;
            case Rd105ModbusBridge b: b.Reopen(); break;
            default: try { _transport.Close(); } catch { } _transport.Open(); break;
        }
    }

    public void Dispose()
    {
        Controller.Dispose();
        Client.Dispose();
        _transport.Dispose();
    }

    /// <summary>
    /// 按连接参数开一条真串口链路。参数名与 ConnectionSchema 一一对应。
    /// 帧格式固定 8N1——SerialPortTransport 就是这么写死的，
    /// 所以连接表单里不给校验位这一项，免得摆一个改了也不生效的下拉。
    /// 「协议」选 Modbus-RTU 时串口上套一层 Rd105ModbusBridge，链路其余部分不变。
    /// </summary>
    public static Rd105Link Serial(ParameterSet cn)
        => new(Rd105Protocol.Transport(
            cn.Str(Rd105TecDriver.FieldPort, Rd105TecDriver.DefaultPort),
            (int)cn.Num(Rd105TecDriver.FieldBaud, Rd105TecDriver.DefaultBaud),
            cn.Str(Rd105TecDriver.FieldProtocol, Rd105TecDriver.DefaultProtocol),
            cn.Int(Rd105TecDriver.FieldAddress, Rd105TecDriver.DefaultAddress)));
}
