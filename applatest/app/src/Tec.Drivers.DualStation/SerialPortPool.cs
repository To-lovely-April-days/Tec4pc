using TecControl.Core.Comm;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 一条被多台设备共用的串口：传输 + 总线锁 + 归还回执。
/// 宇电两台拼在同一段导轨上时 485 自动并联（手册 §3.3），两支 Tr 探头共用
/// 一台采集模块时更是天然同口——设备各自拿着自己的连接参数，同名口子在
/// 池里合并成一条链路。测试时直接构造假的（Transport 换假从站/假总线）。
/// </summary>
public sealed class SharedSerial : IDisposable
{
    private readonly Action? _release;

    public SharedSerial(ISerialTransport transport, SemaphoreSlim busLock, Action? release = null)
    {
        Transport = transport;
        BusLock = busLock;
        _release = release;
    }

    public ISerialTransport Transport { get; }

    /// <summary>半双工总线锁：共口的所有客户端一次只许一问。</summary>
    public SemaphoreSlim BusLock { get; }

    public void Open()
    {
        if (!Transport.IsOpen) Transport.Open();
    }

    /// <summary>归还（不是关口子）：引用数落零才真正关串口。</summary>
    public void Dispose() => _release?.Invoke();
}

/// <summary>
/// 按口名合并串口的池子。每台设备的串口都写在自己的属性里（用户定的），
/// 两台填了同一个口名就共用同一条链路——第一个来的定波特率，后来的必须
/// 一致，不一致开机就拒绝；全部归还后串口才真正关闭。
/// </summary>
public static class SerialPortPool
{
    private sealed class Entry
    {
        public required SerialPortTransport Transport;
        public required SemaphoreSlim BusLock;
        public required int Baud;
        public int Refs;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Ports = new(StringComparer.OrdinalIgnoreCase);

    public static SharedSerial Rent(string portName, int baud)
    {
        var key = portName.Trim();
        lock (Gate)
        {
            if (!Ports.TryGetValue(key, out var e))
            {
                e = new Entry
                {
                    Transport = new SerialPortTransport(key, baud),
                    BusLock = new SemaphoreSlim(1, 1),
                    Baud = baud,
                    Refs = 0
                };
                Ports[key] = e;
            }
            else if (e.Baud != baud)
            {
                throw new InvalidOperationException(
                    $"串口 {key} 已按 {e.Baud} 波特率开着，另一台设备要按 {baud} 用它——" +
                    "一条串口只有一个波特率，共口的设备波特率必须一致");
            }

            e.Refs++;
            return new SharedSerial(e.Transport, e.BusLock, () => Release(key));
        }
    }

    private static void Release(string key)
    {
        lock (Gate)
        {
            if (!Ports.TryGetValue(key, out var e)) return;
            if (--e.Refs > 0) return;
            Ports.Remove(key);
            try { e.Transport.Dispose(); } catch { }
        }
    }
}
