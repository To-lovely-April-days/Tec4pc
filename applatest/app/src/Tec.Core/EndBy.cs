using Tec.Core.Catalog;
using Tec.Core.Records;
using Tec.Driver.Abi;

namespace Tec.Core;

/// <summary>
/// 「结束原因」列的措辞（原型 ENDBY / endByOf）。结束原因由指令语义决定，
/// 不是按 EndReason 一刀切：升温到点叫「到达目标温度」，设转速叫「到达设定」。
/// 运行页的执行记录与导出预览共用这一份（§13.1：两个视图对不上就是错的）。
/// </summary>
public static class EndBy
{
    private static readonly Dictionary<string, string> ByName = new()
    {
        ["升温至"] = "到达目标温度", ["降温至"] = "到达目标温度", ["梯度控温"] = "到达目标温度",
        ["曲线升温"] = "曲线走完", ["自然冷却"] = "到达目标温度", ["恒温保持"] = "计时到",
        ["设定转速"] = "到达设定", ["转速梯度"] = "梯度走完", ["停止搅拌"] = "到达设定",
        ["恒速加料"] = "加完设定体积", ["定量加料"] = "加完设定体积", ["分段加料"] = "加完全部分段",
        ["pH 反馈加料"] = "pH 达到设定", ["pH 调节"] = "pH 达到设定",
        ["等待"] = "计时到", ["等待条件"] = "条件满足"
    };

    /// <summary>指令名没列进表时按模块兜底（原型 endByOf 的 grp 分支）。</summary>
    private static string ByModule(string module) => module switch
    {
        "温度模块" => "到达目标温度",
        "加料" => "加料完成",
        "搅拌" => "到达设定",
        "pH 控制" => "条件满足",
        "在线分析" => "采集完成",
        _ => "执行完成"
    };

    /// <summary>正常结束用原型措辞；异常结束（中止 / 超时 / 报警…）如实显示。</summary>
    public static string Text(StepRecord step, CommandCatalog catalog)
    {
        switch (step.Reason)
        {
            case null: return "—";
            case EndReason.Timeout: return "超时";
            case EndReason.Alarm: return "报警";
            case EndReason.Aborted: return "中止";
            case EndReason.Skipped: return "跳过";
            case EndReason.Failed: return "失败";
            case EndReason.OperatorConfirmed: return "操作人确认";
        }

        var name = catalog.TryGet(step.CommandId, out var d) ? d.DisplayName : step.CommandId;
        return ByName.TryGetValue(name, out var t)
            ? t
            : ByModule(catalog.TryGet(step.CommandId, out var m) ? m.Module : "通用");
    }
}
