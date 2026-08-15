using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Tec.App.ViewModels;

/// <summary>
/// 手写的 MVVM 基类。刻意不引 MVVM 框架：这一层没有复杂到需要源生成器，
/// 少一个依赖就少一处版本纠纷。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    /// <summary>整块刷新：状态一多，逐个 Raise 反而容易漏。</summary>
    protected void RaiseAll(params string[] names)
    {
        foreach (var n in names) Raise(n);
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute(parameter);
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
