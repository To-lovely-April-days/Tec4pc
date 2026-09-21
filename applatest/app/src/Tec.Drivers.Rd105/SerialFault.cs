using System.IO.Ports;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 串口开不了 / 半路掉了时，把 .NET 抛出来的那句翻成站在机器前的人能照着做的话。
/// 原话是 Win32 的错误文案加个口名（「函数不正确。: 'COM8'」），谁看了都不知道该拔哪根线。
///
/// 现场那次：宇电模块断电重上电，USB 转串（CH344）跟着掉了一下，程序里的口子还开着——
/// 再开 COM8 就报 Win32 错误 1（ERROR_INVALID_FUNCTION）。这是 USB 转串驱动在口子开着时被拔插过
/// 的典型症状，把转串重新拔插一下就好；程序这边先扔掉旧的 SerialPort 换个新的再试一次
/// （SerialPortTransport.Open），还不行才把这句话报出来。
/// </summary>
public static class SerialFault
{
    /// <summary>Win32 ERROR_INVALID_FUNCTION（1）包成 HRESULT 的样子。</summary>
    public const int HrInvalidFunction = unchecked((int)0x80070001);
    /// <summary>Win32 ERROR_ACCESS_DENIED（5）。</summary>
    public const int HrAccessDenied = unchecked((int)0x80070005);
    /// <summary>Win32 ERROR_FILE_NOT_FOUND（2）。</summary>
    public const int HrFileNotFound = unchecked((int)0x80070002);

    /// <summary>就是「函数不正确」那一种（拔插过 USB 转串）。</summary>
    public static bool IsInvalidFunction(Exception ex)
        => ex is IOException && (ex.HResult == HrInvalidFunction || ex.Message.Contains("函数不正确") || ex.Message.Contains("Incorrect function"));

    /// <summary>
    /// 「串口 COM8 打不开：驱动报「函数不正确」……」——口名 + 原话 + 该怎么办。
    /// 不认识的异常只带口名和原话，不编原因。
    /// </summary>
    public static string Explain(Exception ex, string? port)
    {
        var where = port is null ? "串口" : $"串口 {port}";
        var raw = ex.Message.TrimEnd('\n', '\r', ' ', '。', '.');
        if (IsInvalidFunction(ex))
            return $"{where} 打不开：驱动报「函数不正确」（Win32 错误 1）——多半是 USB 转串在口子开着的时候被拔插过、" +
                   "或者跟模块一起断过电。把 USB 转串重新拔插一下，再点「连接」；还不行就重启本程序";
        if (ex is UnauthorizedAccessException || ex.HResult == HrAccessDenied)
            return $"{where} 打不开：被别的程序占着（{raw}）——关掉串口调试助手、另一份本程序或 HMI，再点「连接」";
        if (ex is FileNotFoundException || ex.HResult == HrFileNotFound || raw.Contains("does not exist") || raw.Contains("不存在"))
            return $"{where} 打不开：这台机器上现在没有这个口（{raw}）——USB 转串没插、或口号变了，到下拉里重新选";
        if (ex is IOException)
            return $"{where} 出错：{raw}——线是不是半路拔了？点「连接」重开";
        return $"{where} 打不开：{raw}";
    }
}
