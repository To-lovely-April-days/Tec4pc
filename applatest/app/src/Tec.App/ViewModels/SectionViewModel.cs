namespace Tec.App.ViewModels;

/// <summary>
/// 一个可折叠小节的开合状态。
///
/// 面板里那些「圆圈箭头 + 小标题」原来只是画上去的：点了没反应，箭头也不动。
/// 一个看着能点、点了什么都不发生的控件比没有更糟——操作人会以为程序卡了，
/// 然后接着点。这里给它一个真的状态，箭头跟着转，内容跟着收。
/// </summary>
public sealed class SectionViewModel : ViewModelBase
{
    private bool _open;

    public SectionViewModel(bool open = true)
    {
        _open = open;
        Toggle = new RelayCommand(() => Open = !Open);
    }

    public bool Open
    {
        get => _open;
        set => Set(ref _open, value);
    }

    public RelayCommand Toggle { get; }
}
