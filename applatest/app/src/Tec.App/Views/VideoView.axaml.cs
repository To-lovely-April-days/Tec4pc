using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class VideoView : UserControl
{
    public VideoView() => InitializeComponent();

    private VideoViewModel? Vm => DataContext as VideoViewModel;

    private void OnCategoryPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: VideoCategoryVm cat } && Vm is { } vm)
            vm.PickCategory(cat);
    }

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: VideoLessonVm lesson } && Vm is { } vm)
            vm.PickLesson(lesson);
    }
}
