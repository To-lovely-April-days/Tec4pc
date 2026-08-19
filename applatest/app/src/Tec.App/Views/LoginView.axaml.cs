using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();

        // 回车 = 登录，**走隧道**（Tunnel）而不是冒泡。
        //
        // 冒泡版有个很坑的毛病：焦点落在「记住密码」上时，CheckBox 的基类
        // （Button）会把回车当成「点我一下」——勾被取消掉，事件也标成已处理，
        // 冒不到这里，于是既没登录、勾还没了。用户报的「记住密码没作用」
        // 就是这么来的：勾上、回车、勾自己没了，登进去自然什么也没记。
        // 隧道阶段先到根上，谁有焦点都一样先提交。
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        AttachedToVisualTree += (_, _) => FocusFirstEmpty();
        // 注销回到登录页时重新给焦点
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true) FocusFirstEmpty();
        };
    }

    /// <summary>光标落在第一个还空着的框上：记住了用户名就直接进密码框。</summary>
    private void FocusFirstEmpty()
        => Dispatcher.UIThread.Post(() =>
        {
            var user = this.FindControl<TextBox>("UserBox");
            if (user is null) return;
            if (user.Text is { Length: > 0 })
                this.FindControl<TextBox>("PassBox")?.Focus();
            else user.Focus();
        }, DispatcherPriority.Loaded);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not LoginViewModel vm) return;
        if (vm.IsForm) vm.Submit.Execute(null);
        else vm.Enter.Execute(null);
        e.Handled = true;
    }
}
