using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => FocusUser();
        // 注销回到登录页时重新给焦点
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true) FocusUser();
        };
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
