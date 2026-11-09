using System.IO.Ports;

namespace TecControl.Core.Comm;

/// <summary>基于 System.IO.Ports.SerialPort 的传输实现。默认参数 38400,N,8,1（RD105 出厂 TTL 参数）。</summary>
public sealed class SerialPortTransport : ISerialTransport
{
    private SerialPort _port;
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly Parity _parity;
    private readonly StopBits _stopBits;

    /// <summary>
    /// 【本地改动】校验位 / 停止位从前写死 8N1——RD105、IO8R、宇电、冷水机全是 8N1，所以一直没露出来。
    /// 搅拌电机驱动器（爱控 AQMD2408BLS-M，2026-11-09）出厂是 9600 **偶校验**，现场串口工具实测
    /// 用 2 停止位才通，8N1 根本对不上话。缺省值不变，老设备一行不用改。
    /// </summary>
    public SerialPortTransport(string portName, int baudRate = 38400,
                               Parity parity = Parity.None, StopBits stopBits = StopBits.One)
    {
        _portName = portName;
        _baudRate = baudRate;
        _parity = parity;
        _stopBits = stopBits;
        _port = Create();
    }

    /// <summary>【本地改动】「8E2」这种写法——报错和日志里指着说这条口子按什么帧格式开的。</summary>
    public static string FrameName(Parity parity, StopBits stopBits)
        => "8" + parity switch { Parity.Even => "E", Parity.Odd => "O", Parity.Mark => "M", Parity.Space => "S", _ => "N" }
             + stopBits switch { StopBits.Two => "2", StopBits.OnePointFive => "1.5", _ => "1" };

    private SerialPort Create() => new(_portName, _baudRate, _parity, 8, _stopBits)
    {
        Encoding = System.Text.Encoding.ASCII,
        WriteTimeout = 500,
    };

    public static string[] GetPortNames() => SerialPort.GetPortNames();

    /// <summary>【本地改动】口名——报错时指着说是哪个口。</summary>
    public string PortName => _portName;

    public bool IsOpen => _port.IsOpen;

    /// <summary>
    /// 【本地改动】USB 转串在口子开着时被拔插过，再开常报 Win32 错误 1「函数不正确」；
    /// 把这个 SerialPort 扔了、换个新的再试一次，多数时候第二次就开了。还不行原样抛出去，
    /// 由上层翻成人话（Tec.Drivers.Rd105.SerialFault）。
    /// </summary>
    public void Open()
    {
        try
        {
            _port.Open();
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070001)
                                     || ex.Message.Contains("函数不正确") || ex.Message.Contains("Incorrect function"))
        {
            try { _port.Dispose(); } catch { }
            Thread.Sleep(300);
            _port = Create();
            _port.Open();
        }
    }

    public void Close()
    {
        if (_port.IsOpen) _port.Close();
    }

    /// <summary>【本地改动】口子中途掉了（USB 转串被拔插 / 供电抖了一下）之后的自愈：把旧的 SerialPort
    /// 扔掉换个新的再开——旧句柄已经死了，Close 多半会抛，抛了也照扔。Tec.Drivers.Rd105.LinkRecovery 调。</summary>
    public void Reopen()
    {
        try { _port.Dispose(); } catch { }
        _port = Create();
        _port.Open();
    }

    public void DiscardInput()
    {
        if (_port.IsOpen) _port.DiscardInBuffer();
    }

    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        _port.ReadTimeout = timeoutMs;
        try
        {
            return _port.Read(buffer, offset, count);
        }
        catch (TimeoutException)
        {
            return 0;
        }
    }

    /// <summary>【本地改动】口子已经掉了的时候 Close 会抛（Flush 失败）——照样把 SerialPort 放掉，别把句柄留到 GC。</summary>
    public void Dispose()
    {
        try { Close(); } catch { }
        try { _port.Dispose(); } catch { }
    }
}
