using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace Tec.LimitTest;

public partial class MainView : UserControl
{
    public MainView() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.ConnectAsync();
    }

    private async void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选台面文件（工作站另存的 .tecbench、设备 HMI 的 bench.json 或实验文件 .tec）",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("台面 / 实验文件") { Patterns = new[] { "*.tecbench", "*.json", "*.tec" } }
            }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is not null) await vm.ImportBenchAsync(path);
    }

    private void Start_Click(object? sender, RoutedEventArgs e) => Vm?.Start();
    private void Stop_Click(object? sender, RoutedEventArgs e) => Vm?.Stop();
    private void Skip_Click(object? sender, RoutedEventArgs e) => Vm?.Skip();
    private void Estop_Click(object? sender, RoutedEventArgs e) => Vm?.EmergencyStop();
    private void Continue_Click(object? sender, RoutedEventArgs e) => Vm?.Continue();

    private void OpenOut_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        try
        {
            Directory.CreateDirectory(vm.OutDir);
            Process.Start(new ProcessStartInfo(vm.OutDir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            vm.StatusText = "打不开输出目录：" + ex.Message;
        }
    }
}
