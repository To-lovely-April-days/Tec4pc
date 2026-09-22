namespace Tec.Drivers.Rd105;

/// <summary>
/// 串口中途掉了之后的自愈（现场：降温跑到 48 min，宇电那条口子突然读不出来，之后再也没回来——
/// TEC 满功率时 USB 转串被抖了一下，旧句柄死了，每一拍都报错，没人去重开口子）。
///
/// 规矩：
/// · IO 类异常（句柄死了：IOException / 没权限 / 「端口未打开」）**立刻**关掉重开一次，5 s 内不重复；
/// · 协议类异常（超时 / CRC / 应答不对）连着 10 拍才重开一次，30 s 内不重复——那多半是模块或线的事，
///   重开口子不一定有用，但试一下不亏。
/// 重开成功与否都记日志；恢复出数由调用方记。
/// </summary>
public sealed class LinkRecovery
{
    private readonly string _who;
    private readonly Action _reopen;
    private readonly Action<string, string>? _log;
    private readonly Func<long> _clockMs;
    private int _fails;
    private long _lastTry = long.MinValue / 2;

    public LinkRecovery(string who, Action reopen, Action<string, string>? log, Func<long>? clockMs = null)
    {
        _who = who;
        _reopen = reopen;
        _log = log;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    /// <summary>连续失败了几拍（成功一拍归零）。</summary>
    public int Fails => _fails;

    /// <summary>重开过几次（测试与日志用）。</summary>
    public int Reopens { get; private set; }

    public static bool IsLinkDead(Exception ex)
        => ex is IOException or UnauthorizedAccessException or InvalidOperationException;

    /// <summary>读失败了一拍。返回这一拍有没有去重开口子。</summary>
    public bool Failed(Exception ex)
    {
        _fails++;
        var dead = IsLinkDead(ex);
        var now = _clockMs();
        var due = dead
            ? now - _lastTry >= 5_000
            : _fails % 10 == 0 && now - _lastTry >= 30_000;
        if (!due) return false;

        _lastTry = now;
        Reopens++;
        try
        {
            _reopen();
            _log?.Invoke("warn", $"{_who} 串口已关掉重开（{(dead ? "口子报错" : $"连着 {_fails} 拍读不出来")}：{Brief(ex)}）——看下一拍出不出数");
        }
        catch (Exception rex)
        {
            _log?.Invoke("error", $"{_who} 串口重开失败：{Brief(rex)}——USB 转串可能还没回来，过几秒再试");
        }
        return true;
    }

    /// <summary>读成功了一拍。</summary>
    public void Ok() => _fails = 0;

    /// <summary>报错里带的原始字节那一截（「｜发 … ｜收 …」）日志里已经有过，这里只要前半句。</summary>
    private static string Brief(Exception ex)
    {
        var m = ex.Message;
        var cut = m.IndexOf('｜');
        return (cut > 0 ? m[..cut] : m).Trim();
    }
}
