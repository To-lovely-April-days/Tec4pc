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

            // 「安全联锁」改成「改限值」时，触发动作换成了引擎真做得出的五档。
            // Id 和其余参数键都没动，只有旧动作值要翻：「停止实验」本来就是
            // 中止这一路的意思；「暂停」引擎没有这一档，最接近本意的是报警等人。
            // 「改限值」从「监测量 + 条件 + 阈值」四个字段改成手册 §6 的参数表
            // （Tr max / Tj min / T diff max …）。老配方那一行搬进表里：
            // 监测量 + 条件 → 参数名。认不出的（浊度那类已经没有信号的）不硬翻，
            // 那一条照实丢掉——留一条盯不到信号的限值比没有更糟（按传感器失效触发）。
            // 旧动作值（安全联锁那一版的「停止实验 / 暂停实验」）同一趟翻掉
            case Catalog.BuiltinCommands.Interlock when p.Has("src"):
                var par = ParamOf(p.Str("src"), p.Str("op", ">"));
                var rows = step.Rows?.Select(r => r.Clone()).ToList() ?? new List<ParameterSet>();
                var act = ActOf(p.Str("act", "中止本通道"));
                if (par is not null)
                    rows.Insert(0, ParameterSet.Of(("par", par), ("val", p.Num("val")), ("act", act)));
                how = par is null
                    ? $"「改限值」的监测量「{p.Str("src")}」已无对应信号，那一条限值未保留"
                    : $"「改限值」{p.Str("src")} {p.Str("op")} {Fmt.Num(p.Num("val"))} → 限值表「{par}」";
                foreach (var k in new[] { "src", "op", "val", "act" }) p.Remove(k);
                return With(step, Catalog.BuiltinCommands.Interlock, p, rows);

            // 没有 src 却带着旧动作值的（极少见：改过一半的文件）也翻一下
            case Catalog.BuiltinCommands.Interlock when p.Str("act") is "停止实验" or "暂停实验":
                var oldAct = p.Str("act");
                p["act"] = ActOf(oldAct);
                how = $"「安全联锁」的触发动作「{oldAct}」→「改限值」的「{p.Str("act")}」";
                return With(step, Catalog.BuiltinCommands.Interlock, p);

            // 起始步骤的限值表同样换了列名（src/op → par），指令 Id 没变，
            // 所以单独认一次：表里有 src 这一列的就是老文件
            case Catalog.BuiltinCommands.FirstFill when step.Rows is { Count: > 0 } old
                                                        && old.Any(r => r.Has("src")):
                var kept = new List<ParameterSet>();
                var dropped = 0;
                foreach (var r in old)
                {
                    if (!r.Has("src")) { kept.Add(r.Clone()); continue; }
                    var pp = ParamOf(r.Str("src"), r.Str("op", ">"));
                    if (pp is null) { dropped++; continue; }
                    kept.Add(ParameterSet.Of(("par", pp), ("val", r.Num("val")),
                                             ("act", r.Str("act", "中止本通道"))));
                }
                how = $"起始限值表换成手册 §6 的参数名（{kept.Count} 条）"
                      + (dropped > 0 ? $"；其中 {dropped} 条盯的信号已不存在，未保留" : "");
                return With(step, Catalog.BuiltinCommands.FirstFill, p, kept);

            default:
                return null;
        }
    }

    /// <summary>「安全联锁」那一版的触发动作 → 引擎真做得出的五档。</summary>
    private static string ActOf(string act) => act switch
    {
        "停止实验" => "中止本通道",
        "暂停实验" => "仅报警",     // 引擎没有「暂停」这一档，最接近本意的是报警等人
        _ => act
    };

    /// <summary>老的监测量 + 条件 → 手册 §6 的参数名。认不出返回 null。</summary>
    private static string? ParamOf(string src, string op) => (src, op) switch
    {
        ("釜内 Tr", "<") => "Tr min",
        ("釜内 Tr", _) => "Tr max",
        ("夹套 Tj", "<") => "Tj min",
        ("夹套 Tj", _) => "Tj max",
        ("pH", "<") => "pH min",
        ("pH", _) => "pH max",
        _ => null                       // 浊度这类已经没有的信号，不硬翻
    };

    private static Step With(Step step, string commandId, ParameterSet parameters,
                             List<ParameterSet>? rows = null) => new()
    {
        StepId = step.StepId,           // 保住 StepId：老记录要对得上
        CommandId = commandId,
        Parameters = parameters,
        Rows = rows ?? step.Rows?.Select(r => r.Clone()).ToList(),
        Enabled = step.Enabled,
        Comment = step.Comment,
        // 这两项从前抄漏了：翻译一步，操作人关掉的「失败时暂停」自己又开回默认、
        // 标好的工艺阶段消失。翻译只换说法，不改这一步的其它任何设置
        PauseOnFault = step.PauseOnFault,
        Phase = step.Phase,
        Guard = step.Guard?.Clone(),
        Parallel = step.Parallel
    };
}
