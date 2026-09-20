using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tec.Driver.Abi;

namespace Tec.App.Services;

/// <summary>
/// 这台机器上现在有哪些串口。
///
/// 从前设备属性里的「串口」是写死的 COM1~COM6——插着八口 USB 转串的机器
/// （COM3 / 7 / 8 / 10 / 15~18）一个都选不着，只能干看着。现在照实去问系统。
///
/// 端口号之外还要**设备名**：八个口在设备管理器里叫
/// 「USB-Enhanced-SERIAL-A/B/C/D CH344」，光一个 COM 号分不出哪一路接的是
/// 温控器、哪一路是 IO 模块。名字只有 Windows 给得出（SetupAPI 的 FriendlyName），
/// 拿不到就只列端口号——**不编一个名字出来**。
/// </summary>
public static class SerialPortScan
{
    /// <summary>
    /// 扫一遍。摆成下拉项的规矩（排序、不在线标注）在 SerialPortChoices 里，
    /// 那部分是纯逻辑、有回归；这里只负责问系统要。
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> List()
    {
        string[] names;
        try { names = SerialPort.GetPortNames(); }
        catch { return Array.Empty<SerialPortInfo>(); }

        var friendly = FriendlyNames();
        return names.Select(n => new SerialPortInfo(n, friendly.GetValueOrDefault(n))).ToList();
    }

    /// <summary>建表单用的下拉项。current = 这台设备已经存着的口，拔了也要留在下拉里。</summary>
    public static IReadOnlyList<ChoiceOption> Options(string? current)
        => SerialPortChoices.Build(List(), current);

    // ── Windows：SetupAPI 取设备管理器里那串名字 ─────────────────────
    //
    // 形如「USB-Enhanced-SERIAL-A CH344 (COM16)」——端口号在括号里，
    // 括号前面那截才是人认得出的名字。
    //
    // 不走 WMI / 注册表：那两条都要另外引包（System.Management、
    // Microsoft.Win32.Registry），而这台机器上装不了新包；SetupAPI 是系统 DLL，
    // 一个 DllImport 就够。整段包在 try 里：取不到名字不是错，退回只列端口号。

    private static Dictionary<string, string> FriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return map;

        var dev = IntPtr.Zero;
        try
        {
            var ports = GuidDevClassPorts;
            dev = SetupDiGetClassDevsW(ref ports, IntPtr.Zero, IntPtr.Zero, DigcfPresent);
            if (dev == IntPtr.Zero || dev == new IntPtr(-1)) return map;

            var info = new SpDevinfoData();
            info.cbSize = (uint)Marshal.SizeOf<SpDevinfoData>();
            var buf = new byte[512];

            for (uint i = 0; SetupDiEnumDeviceInfo(dev, i, ref info); i++)
            {
                if (!SetupDiGetDeviceRegistryPropertyW(dev, ref info, SpdrpFriendlyName,
                                                       out _, buf, (uint)buf.Length, out var used))
                    continue;
                var text = Encoding(buf, used);
                if (text.Length == 0) continue;

                // 「名字 (COMn)」：括号里的是端口，前面的是名字
                var m = Regex.Match(text, @"^(?<name>.*?)\s*\((?<port>COM\d+)\)\s*$",
                                    RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var port = m.Groups["port"].Value;
                var name = m.Groups["name"].Value.Trim();
                if (name.Length > 0) map[port] = name;
            }
        }
        catch
        {
            // 系统 DLL 不在、签名对不上、权限不够……名字是锦上添花，拿不到就算了
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (dev != IntPtr.Zero && dev != new IntPtr(-1)) SetupDiDestroyDeviceInfoList(dev);
        }
        return map;
    }

    private static string Encoding(byte[] buf, uint usedBytes)
    {
        var n = (int)Math.Min(usedBytes, (uint)buf.Length);
        if (n <= 0) return "";
        var s = System.Text.Encoding.Unicode.GetString(buf, 0, n);
        var z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Trim();
    }

    /// <summary>GUID_DEVCLASS_PORTS：设备管理器里「端口 (COM 和 LPT)」那一类。</summary>
    private static Guid GuidDevClassPorts => new("4D36E978-E325-11CE-BFC1-08002BE10318");

    private const uint DigcfPresent = 0x00000002;   // 只要**现在插着**的
    private const uint SpdrpFriendlyName = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
                                                      IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr devInfo, uint index, ref SpDevinfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr devInfo, ref SpDevinfoData data, uint property,
        out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);
}
