using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class LoginView : UserControl
{
    private readonly DispatcherTimer _clock;

    public LoginView()
    {
        InitializeComponent();
        // 桌面右下角那只钟。走墙上时间——它是桌面陈设，不掺进仿真时标
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _clock.Tick += (_, _) => Tick();
        AttachedToVisualTree += (_, _) => { Tick(); _clock.Start(); FocusUser(); };
        DetachedFromVisualTree += (_, _) => _clock.Stop();
        IsVisibleChanged();
    }

    private void IsVisibleChanged()
        => PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true) FocusUser();
        };

    private void Tick()
    {
        var d = DateTime.Now;
        ClockTime.Text = d.ToString("HH:mm");
        ClockDate.Text = d.ToString("yyyy年M月d日 dddd", new System.Globalization.CultureInfo("zh-CN"));
    }

    private void FocusUser()
        => Dispatcher.UIThread.Post(() =>
        {
            var user = this.FindControl<TextBox>("UserBox");
            if (user is null) return;
            if (user.Text is { Length: > 0 })
                this.FindControl<TextBox>("PassBox")?.Focus();
            else user.Focus();
        }, DispatcherPriority.Loaded);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not LoginViewModel vm) return;
        if (vm.IsForm) { vm.Submit.Execute(null); e.Handled = true; }
        else { vm.Enter.Execute(null); e.Handled = true; }
    }
}
