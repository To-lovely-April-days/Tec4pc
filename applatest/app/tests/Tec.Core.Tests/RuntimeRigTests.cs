using Tec.Core.Persistence;
using Tec.Driver.Abi;
using Tec.Hmi.Runtime;
using Tec.LimitTest.Records;
using Tec.LimitTest.Runs;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// RuntimeRig：套在 HmiRuntime 上的那层——读数从管线来、目标走通道能力、急停走会话 SafeStop、
/// 安全层触发转成 Tripped。用仿真主机（只在测试里注册）+ 600 倍时标跑。
/// 再把 Runner 整个架上去跑一格最低温：真机那条路上除了串口，别的都走过一遍。
/// </summary>
public class RuntimeRigTests
{
    private static string SimBenchDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tec-rig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        TecFiles.SaveBench(Path.Combine(dir, "bench.json"), new BenchDoc
        {
            Name = "仿真",
            Devices = { new DeviceDoc { DriverId = Tec.Drivers.Simulator.Rd105ReactorDriver.DriverId, InstanceId = "R1" } }
        });
        return dir;
    }

    private static async Task WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            Assert.True(DateTime.UtcNow < deadline, "等太久");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task 仿真主机_读数下发停止急停安全层都走通()
    {
        await using var rt = new HmiRuntime(SimBenchDir());
        rt.Drivers.RegisterBuiltin(new Tec.Drivers.Simulator.Rd105ReactorDriver());
        rt.Boot(timeScale: 600);
        await rt.StartAsync();
        using var rig = new RuntimeRig(rt);

        Assert.True(rig.IsAlive(out _));
        Assert.Contains("CH1 / CH2", rig.Describe);
        Assert.Equal(90, rig.Threshold);
        Assert.Equal(2, rig.Band);
        Assert.Null(await rig.ReadLimitedAsync(0, CancellationToken.None));     // 仿真没有参数面板：留空，不编
        Assert.Equal(-40, rig.Limits(0)!.Min);

        await WaitUntil(() => rig.Read(0).Tj is not null);
        var r0 = rig.Read(0);
        Assert.Equal("TEC", r0.Source);
        Assert.Null(r0.Cur);
        Assert.Null(r0.TecPower);           // 仿真不发 tecpwr

        await rig.SetTargetAsync(0, 5, CancellationToken.None);
        await Task.Delay(rt.Clock.RealDelay(TimeSpan.FromMinutes(8)) + TimeSpan.FromMilliseconds(200));
        var r1 = rig.Read(0);
        Assert.True(r1.Tj < 20, $"下发 5 ℃ 八分钟后夹套还在 {r1.Tj}");
        Assert.True(r1.Out is { } o && o != 0);
        Assert.True(rig.Read(1).Tj > 20);   // B 没动

        await rig.StopAsync(0, CancellationToken.None);
        var did = await rig.SafeStopAsync(CancellationToken.None);
        Assert.NotEmpty(did);
        Assert.All(did, s => Assert.StartsWith("CH", s));

        // 安全层：操作人在 Tj 上收一条已经越了的限 → 去抖后触发 → Tripped
        string? trip = null;
        rig.Tripped += w => trip = w;
        rt.Engine.Safety.SetOperatorLimit(1, "Tj", null, -100, null);
        await WaitUntil(() => trip is not null, 8000);
        Assert.Contains("CH1", trip);
        Assert.Contains("Tj", trip);
    }

    [Fact]
    public async Task Runner架在仿真主机上_跑一格最低温_表里有数()
    {
        await using var rt = new HmiRuntime(SimBenchDir());
        rt.Drivers.RegisterBuiltin(new Tec.Drivers.Simulator.Rd105ReactorDriver());
        // 200 倍：1 虚拟秒 = 5 ms 真实。整个测试集并行跑时仿真的采样泵会被挤得晚几十毫秒，
        // 按真机的 15 s 过期窗（= 75 ms 真实）会把好端端的数判成 Stale——放宽到 1 min 虚拟，链路判断也放宽
        rt.Boot(timeScale: 200);
        rt.Pipeline.StaleAfter = TimeSpan.FromMinutes(1);
        await rt.StartAsync();
        using var rig = new RuntimeRig(rt);
        await WaitUntil(() => rig.Read(0).Tj is not null && rig.Read(1).Tj is not null);

        var outDir = Path.Combine(Path.GetTempPath(), "tec-rig-run-" + Guid.NewGuid().ToString("N"));
        var s = new TestSettings { MinMinutes = 10, SettleBand = 0, Operator = "仿真", Patch = "0323", LinkLossSeconds = 120 };
        var runner = new LimitTestRunner(rig, s, outDir,
            now: rt.Clock.Func,
            delay: (t, ct) => Task.Delay(rt.Clock.RealDelay(t), ct),
            log: (l, t) => rt.Log.Write("测试", t, "测试", l == "error" ? Tec.Core.Records.LogLevel.Error : Tec.Core.Records.LogLevel.Info));
        foreach (var c in runner.Plan) c.Selected = c.Kind == TestKind.MinTemp && c.Water == 20;
        var run = Task.Run(() => runner.RunAsync(CancellationToken.None));
        await WaitUntil(() => runner.State == RunnerState.WaitingWater);
        runner.ConfirmWater(20.1);
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        var cell = runner.Cell(TestKind.MinTemp, 20);
        Assert.Equal(CellState.Done, cell.State);
        using var wb = new ClosedXML.Excel.XLWorkbook(runner.BookPath!);
        using var probe = new RecordBook(Path.Combine(outDir, "probe.xlsx"), s.ToBookSpec());
        var lay = probe.Layout(TestKind.MinTemp, 20);
        var ws = wb.Worksheet(lay.Name);
        for (var i = 0; i <= 10; i++)
        {
            Assert.False(ws.Cell(lay.FirstRow + i, lay.Col[RecordBook.KATj]).Value.IsBlank, $"第 {i} 行 A 夹套没数");
            Assert.Equal("TEC", ws.Cell(lay.FirstRow + i, lay.Col[RecordBook.KASrc]).GetText());
            Assert.True(ws.Cell(lay.FirstRow + i, lay.Col[RecordBook.KACur]).Value.IsBlank);   // 仿真没有电流：留空
        }
        Assert.True(ws.Cell(lay.FirstRow + 10, lay.Col[RecordBook.KATj]).GetDouble() < ws.Cell(lay.FirstRow, lay.Col[RecordBook.KATj]).GetDouble());
        Assert.Equal("完成", ws.Cell(lay.Cond[RecordBook.CResult].Row, lay.Cond[RecordBook.CResult].Col).GetText());
        Assert.True(ws.Cell(lay.Cond[RecordBook.CLimitedA].Row, lay.Cond[RecordBook.CLimitedA].Col).Value.IsBlank);
    }
}
