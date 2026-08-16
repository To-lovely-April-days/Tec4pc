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

    public Rd105Link(ISerialTransport transport)
    {
        _transport = transport;
        Client = new TecClient(transport);
        Controller = new TecController(Client);
    }

    public TecClient Client { get; }
    public TecController Controller { get; }

    public bool IsOpen => _transport.IsOpen;

    public void Open()
    {
        if (!_transport.IsOpen) _transport.Open();
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
    /// </summary>
    public static Rd105Link Serial(ParameterSet cn)
        => new(new SerialPortTransport(
            cn.Str(Rd105TecDriver.FieldPort, "COM3"),
            (int)cn.Num(Rd105TecDriver.FieldBaud, 38400)));
}
