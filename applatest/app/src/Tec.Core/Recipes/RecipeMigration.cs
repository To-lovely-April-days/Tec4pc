using Tec.Driver.Abi;

namespace Tec.Core.Recipes;

/// <summary>
/// 老配方文件里的旧指令 Id 翻译成现在这套。
///
/// 指令库从 23 条精简到 12 条时，被合并掉的那些 Id 还躺在已经存过盘的配方里。
/// 不翻译的话，打开旧配方满屏「引用了未安装的指令」——文件没坏，是程序换了说法。
///
/// **只做无损翻译。** 换算得出来的（转速梯度的目标转速、定量加料的流量）照算，
/// 换算不出来的（结晶模式、pH 上下限报警）一律不动，留给校验器报「未安装的指令」——
/// 猜一个默认值填进去，等于替操作人改了工艺参数，那比打不开严重得多。
/// </summary>
public static class RecipeMigration
{
    /// <summary>
    /// 就地翻译，返回改动说明（每条一句人话，界面直接显示）。没有改动就是空表。
    /// </summary>
    public static IReadOnlyList<string> Apply(Recipe recipe)
    {
        var notes = new List<string>();

        for (var i = 0; i < recipe.Steps.Count; i++)
        {
            var step = recipe.Steps[i];
            var moved = Translate(step, out var how);
            if (moved is null) continue;

            recipe.Steps[i] = moved;
            notes.Add($"第 {i + 1} 步：{how}");
        }

        return notes;
    }

    /// <summary>返回翻译后的步骤；这一步不需要翻译就返回 null。</summary>
    private static Step? Translate(Step step, out string how)
    {
        how = "";
        var p = step.Parameters.Clone();

        switch (step.CommandId)
        {
            // 升温至 / 降温至 → 控温。参数键完全一致，方向由目标温度决定
            case "tec.temp.rampUp":
            case "tec.temp.rampDown":
                how = $"「{(step.CommandId.EndsWith("Up", StringComparison.Ordinal) ? "升温至" : "降温至")}」→「控温」";
                return With(step, CommandSpecs.Control, p);

            // 釜内控温 Tr / 夹套控温 Tj → 控温 + 控温对象。
            // 老指令的「维持时长」在新指令里没有对应字段——它本来就该是后面一条「恒温保持」。
            // 这里不擅自补那一步：少了一条保持是看得见的，悄悄多出一条不是。
            case "tec.temp.reactor":
            case "tec.temp.jacket":
                var isJacket = step.CommandId.EndsWith("jacket", StringComparison.Ordinal);
                p["obj"] = isJacket ? "夹套 Tj" : "釜内 Tr";
                if (!p.Has("rate")) p["rate"] = 2d;
                var dur = p.Has("dur") ? p.Num("dur") : 0;
                p.Remove("dur");
                p.Remove("limit");
                how = $"「{(isJacket ? "夹套控温 Tj" : "釜内控温 Tr")}」→「控温」（控温对象 = {(isJacket ? "夹套 Tj" : "釜内 Tr")}）"
                      + (dur > 0 ? $"；原来的维持时长 {Fmt.Num(dur)} min 需要另加一条「恒温保持」" : "");
                return With(step, CommandSpecs.Control, p);

            // 转速梯度 → 搅拌：目标转速 + 到达用时（分 → 秒）
            case "tec.stir.ramp":
                var to = p.Has("to") ? p.Num("to") : 0;
                var minutes = p.Has("dur") ? p.Num("dur") : 0;
                p.Remove("from");
                p.Remove("to");
                p.Remove("dur");
                p["rpm"] = to;
                p["ramp"] = minutes * 60;
                how = $"「转速梯度」→「搅拌」（{Fmt.Num(to)} rpm，{Fmt.Num(minutes * 60)} s 内到达）";
                return With(step, CommandSpecs.Stir, p);

            // 停止搅拌 → 搅拌，转速 0
            case "tec.stir.stop":
                p["rpm"] = 0d;
                if (!p.Has("ramp")) p["ramp"] = 5d;
                how = "「停止搅拌」→「搅拌」（转速 0）";
                return With(step, CommandSpecs.Stir, p);

            // 恒速加料 → 加料，参数键一致
            case "tec.dose.rate":
                how = "「恒速加料」→「加料」";
                return With(step, CommandSpecs.Dose, p);

            // 定量加料 → 加料：完成时间换算成流量
            case "tec.dose.volume":
                var vol = p.Has("vol") ? p.Num("vol") : 0;
                var mins = p.Has("dur") ? p.Num("dur") : 0;
                p.Remove("dur");
                if (vol > 0 && mins > 0) p["rate"] = vol / mins;
                how = mins > 0
                    ? $"「定量加料」→「加料」（{Fmt.Num(vol)} mL ÷ {Fmt.Num(mins)} min = {Fmt.Num(vol / mins)} mL/min）"
                    : "「定量加料」→「加料」";
                return With(step, CommandSpecs.Dose, p);

            // pH 反馈加料（加料模块）→ pH 反馈加料（pH 模块）。参数键一致
            case "tec.dose.ph":
                how = "「pH 反馈加料」并入 pH 模块";
                return With(step, CommandSpecs.PhHold, p);

            // 条件等待 → 等待（等待方式 = 按条件）。cond / timeout / onTimeout 键原样通用
            case Catalog.BuiltinCommands.WaitUntil:
                p["by"] = "按条件";
                how = "「条件等待」→「等待」（等待方式 = 按条件）";
                return With(step, Catalog.BuiltinCommands.Wait, p);

            default:
                return null;
        }
    }

    private static Step With(Step step, string commandId, ParameterSet parameters) => new()
    {
        StepId = step.StepId,           // 保住 StepId：老记录要对得上
        CommandId = commandId,
        Parameters = parameters,
        Rows = step.Rows?.Select(r => r.Clone()).ToList(),
        Enabled = step.Enabled,
        Comment = step.Comment,
        // 这两项从前抄漏了：翻译一步，操作人关掉的「失败时暂停」自己又开回默认、
        // 标好的工艺阶段消失。翻译只换说法，不改这一步的其它任何设置
        PauseOnFault = step.PauseOnFault,
        Phase = step.Phase
    };
}
