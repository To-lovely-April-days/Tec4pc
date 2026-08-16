using System.IO.Ports;

namespace TecControl.Core.Comm;

/// <summary>基于 System.IO.Ports.SerialPort 的传输实现。默认参数 38400,N,8,1（RD105 出厂 TTL 参数）。</summary>
public sealed class SerialPortTransport : ISerialTransport
{
    private readonly SerialPort _port;

    public SerialPortTransport(string portName, int baudRate = 38400)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Encoding = System.Text.Encoding.ASCII,
            WriteTimeout = 500,
        };
    }

    public static string[] GetPortNames() => SerialPort.GetPortNames();

    public bool IsOpen => _port.IsOpen;

    public void Open() => _port.Open();

    public void Close()
    {
        if (_port.IsOpen) _port.Close();
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

    public void Dispose()
    {
        Close();
        _port.Dispose();
    }
}
