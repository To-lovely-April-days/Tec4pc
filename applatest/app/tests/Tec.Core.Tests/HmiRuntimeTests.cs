using Tec.Core.Persistence;
using Tec.Driver.Abi;
using Tec.Hmi.Runtime;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 设备端精简工作台（Tec.Hmi.Runtime）。盯三件事：**开机不空手**（没有台面
/// 文件写出缺省的、读坏了从空台面起而不是黑屏）、**台面文件格式就是工作站
/// 的 BenchDoc**（工作站摆好拷过来能用）、**通道真能跑**（仿真台面开起来
/// 能力齐全、一步真跑完、归档真落盘）。
/// </summary>
public sealed class HmiRuntimeTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tec-hmi-rt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task 开机没有台面文件_写出缺省真机台面给现场改()
    {
        var dir = TempDir();
        await using var rt = new HmiRuntime(dir);
        rt.Boot();

        Assert.True(rt.SeededDefaultBench);
        Assert.True(File.Exists(rt.BenchPath));
        // 缺省是真机三件：双工位主机 + 两支宇电探头，都不是模拟
        Assert.Equal(3, rt.Bench.Devices.Count);
        Assert.All(rt.Bench.Devices, d => Assert.False(d.Simulated));
        Assert.Contains(rt.Bench.Devices, d => d.DriverId == Tec.Drivers.DualStation.DualStationDriver.DriverId);
        // 探头各绑两条通道（A/B 工位）
        Assert.Equal(4, rt.Bench.Bindings.Count);
    }

    [Fact]
    public async Task 台面文件读坏了_从空台面启动而不是起不来()
    {
        var dir = TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir, "bench.json"), "{ 这不是合法的 JSON …");
        await using var rt = new HmiRuntime(dir);
        rt.Boot();
        await rt.StartAsync();

        Assert.Empty(rt.Bench.Devices);
        Assert.Empty(rt.Channels);      // 空台面 = 没有通道，面板照实显示，不假装
    }

    [Fact]
    public async Task 仿真台面_通道就绪能力齐全_一步真跑完_归档真落盘()
    {
        var dir = TempDir();
        // 台面文件就是工作站的 BenchDoc：仿真双工位反应器一台
        TecFiles.SaveBench(Path.Combine(dir, "bench.json"), new BenchDoc
        {
            Name = "验收台面",
            Devices =
            {
                new DeviceDoc
                {
                    DriverId = Tec.Drivers.Simulator.Rd105ReactorDriver.DriverId,
                    InstanceId = "R1", Simulated = true
                }
            }
        });

        await using var rt = new HmiRuntime(dir);
        rt.Boot(timeScale: 600);
        await rt.StartAsync();

        Assert.Equal(2, rt.Channels.Count);
        var ch = rt.ChannelOf(1);
        Assert.NotNull(ch);
        Assert.NotNull(ch!.Capabilities.Get<ITemperatureControl>());
        Assert.NotNull(ch.Capabilities.Get<IRefluxControl>());
        // 安全层按设备 Limits 立了限值
        Assert.Contains(rt.Engine.Safety.Limits, l => l.Channel == 1 && l.FromDeviceLimits);

        // 一步恒温保持真跑完（面板序列走的就是这台引擎）
        rt.BeginBatch();
        var r = new Tec.Core.Recipes.Recipe { Name = "验收" };
        r.Steps.Add(new Tec.Core.Recipes.Step
        {
            CommandId = CommandSpecs.Hold,
            Parameters = ParameterSet.Of(("dur", 1d))
        });
        rt.Engine.StartChannel(1, r, rt.Operator);
        await rt.Engine.Runner(1)!.Completion.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(Tec.Core.Records.ChannelRunState.Completed, rt.Engine.Record.Of(1)!.State);
        // 归档真落盘：批次目录写出来了
        Assert.True(rt.SyncArchive() >= 0);
        Assert.True(Directory.Exists(Path.Combine(dir, "Runs")));
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(dir, "Runs")));
    }
}
