using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class RecipeLibView : UserControl
{
    public RecipeLibView()
    {
        InitializeComponent();
        // 在根上收，顺着视觉树往上找 DataContext：库列表一行、步骤表一行都走这一处，
        // 不用给每个模板各挂一个 handler（与配方页同一种写法）
        AddHandler(PointerPressedEvent, OnAnyPressed, RoutingStrategies.Tunnel);
        // 剖面不在模板里，DataContext 就是整页的 VM，上面那个按 DataContext 找的
        // 办法认不出它点中了哪一步——色带自己报序号
        Profile.StepPicked += (_, i) => Vm?.PickStepAt(i);
    }

    private RecipeLibViewModel? Vm => DataContext as RecipeLibViewModel;

    private void OnAnyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm) return;
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is not Control c) continue;
            switch (c.DataContext)
            {
                case StepViewModel s:
                    vm.SelStep = s;
                    return;
                case LibRowViewModel r:
                    vm.Selected = r;
                    return;
            }
        }
    }
}
