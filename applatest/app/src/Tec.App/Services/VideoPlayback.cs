using System.Diagnostics;

namespace Tec.App.Services;

/// <summary>
/// 视频怎么放。现在只有一种：交给系统默认播放器（Windows 的「电影和电视」、
/// Linux 的 xdg-open）。内嵌播放器（LibVLC 那类）要带一百多 MB 原生库，
/// 带不带由用户定——定了就再加一个实现挂进来，页面这边一个字不用改。
/// </summary>
public interface IVideoPlayback
{
    /// <summary>界面上告诉操作人「这次是用什么放的」。</summary>
    string Name { get; }

    /// <summary>放这个文件。放不了返回 false 并把原因写进 error——不悄悄失败。</summary>
    bool TryPlay(string path, out string? error);
}

public sealed class ExternalVideoPlayback : IVideoPlayback
{
    public string Name => "系统播放器";

    public bool TryPlay(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            error = $"文件不存在：{path}";
            return false;
        }
        try
        {
            // UseShellExecute：按文件扩展名交给系统关联的播放器，不是我们自己解码
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = $"系统没有能打开这个文件的播放器：{ex.Message}";
            return false;
        }
    }
}
