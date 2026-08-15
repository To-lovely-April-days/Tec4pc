# TecStudio · Avalonia 上位机

四通道平行合成反应工作站的操作端。这是**新开的工程**，与仓库根目录的
`TecStudio.sln`（两通道温控测试程序）完全隔离——两者不共享解决方案、
不共享 `Directory.Build.props`、不互相引用。那份测试程序后续会被包装成
一个 `Tec.Driver.*` 驱动接进来，而不是被改造成主程序。

设计依据：`docs/核心架构.md`、`docs/设备与操作清单.md`、
`docs/台面与配方界面设计说明.md`，以及原型 `tecstudio.html`。

---

## 编译与运行

```bash
cd app
dotnet restore
dotnet build
dotnet test tests/Tec.Core.Tests/Tec.Core.Tests.csproj
dotnet run --project src/Tec.App/Tec.App.csproj
```

需要 .NET 8 SDK。首次 `restore` 会拉 Avalonia 11.2.3 与 xunit。

> **这份代码在交付时没有被编译过。** 生成它的环境里没有 .NET SDK，
> 出站网络也拦掉了 `builds.dotnet.microsoft.com` 与 NuGet，装不上工具链。
> 所以第一次 `dotnet build` 出现的编译错误请直接告诉我，我来改；
> 我不会假装它已经跑通过。

启动后默认是**仿真 60×**：一条计划 1 小时的配方 1 分钟跑完，
曲线、记录、偏差都按虚拟钟走，所以"实际时长"与"计划时长"仍然可比。
接真机时把 `Workspace.Boot()` 里的 `TimeScale = 60` 改回 `1`。
仿真采样一律带 `Quality.Simulated`，导出时每行都标着来源，不会和实测混进一份报告。

---

## 工程结构

```
app/
  Tec.Studio.sln
  src/
    Tec.Driver.Abi/          契约包：能力接口 + 指令模型 + 参数 schema + 数据契约
    Tec.Core/                领域核心：台面 · 配方 · 排期 · 执行引擎 · 安全 · 记录 · 导出
    Tec.DriverHost/          驱动发现 · ALC 隔离 · ABI 校验 · 故障隔离
    Tec.Drivers.Simulator/   RD-105 反应器 · 加料泵 · pH · 浊度（全部带仿真动态）
    Tec.App/                 Avalonia MVVM 界面
  tests/
    Tec.Core.Tests/          排期 · 执行 · 偏差 · 导出 · 校验 · GLP 记录
```

依赖方向只能向下。**`Tec.Core` 里不允许出现 `Rd105`、`Raman` 这样的名字**，
它只认识 `ITemperatureControl`、`IScalarSensor` 这类能力契约。
驱动只引用 `Tec.Driver.Abi`，引用到 `Tec.Core` 就是架构被破坏了。

---

## 已经实装的部分

| 架构条目 | 落在哪里 |
|---|---|
| 能力而非类型 | `Tec.Driver.Abi/Capabilities.cs`，`Channel.Capabilities` 只按接口取 |
| 驱动包 + ABI 版本 + ALC 隔离 | `Tec.DriverHost/DriverCatalog.cs` |
| 指令由驱动声明、配方存 CommandId | `Rd105Commands` / `DosingCommands` / `ProbeCommands` |
| 参数 schema 驱动表单 | `SchemaFormViewModel` + `Views/SchemaFormView.axaml`，零手写表单 |
| 结束条件定死在指令上 | `TerminationKind`，含原型验证过的 `Immediate` |
| 排期只有一个来源 | `Scheduling/Schedule.cs`，卡片 / 甘特 / 记录同读一份 |
| 通道各自启动 | `RunEngine.StartChannel(int, ...)`，没有"整机启动" |
| 基线启动时冻结 | `ChannelRun.Baseline`，热改只动 `_live` |
| 两种偏差分开 | `StepRecord.StartDeviation` / `DurationDeviation` |
| 记录在步骤开始时建行 | `ChannelRunner.ExecuteStepAsync` 第 4 步 |
| 运行中修改 + 审计 | `ChannelRunner.ProposeEdit`，写改前/改后/操作人 |
| 资源仲裁（2 泵 4 通道） | `ResourceArbiter` + `DemoBench.ResourceOf`，等待写进事件 |
| 安全层独立于配方 | `Safety/SafetyMonitor.cs`，信号失效本身就是触发条件 |
| GLP 只追加 + 链式摘要 | `Records/RecordStore.cs`，改一行之后全部对不上 |
| 导出：墙钟 / 通道两种基准 | `Export/RecordExporter.cs`，通道基准下宽表按通道分块 |
| 设备线稿 | `Assets/devices/*.svg` + `Controls/SvgArt.cs`，与原型同一份图 |

## 还没做的部分

- **手动控制面板**（不走配方直接下发温度/转速/加料）
- **自动测定向导**：溶解度曲线、介稳区（FR-5.15）——判据用的是浊度标量，接口已经齐了
- **学习中心**（FR-5.10 / FR-5.11）
- **身份与权限**：现在操作人是硬编码的"操作员"；电子签名要等真实身份体系
- **报警中心视图**：`SafetyMonitor` 已经在发事件，界面还没有独立的报警页
- **配方 / 台面存盘**：模型都带了 `SchemaVersion`，序列化与迁移管线还没写
- **L2 谱图 / 分布查看**：接口留着（`ISpectrumSource` / `IDistributionSource`），
  按 §9.3 要等真拿到原始数据流再做

---

## 第三方驱动怎么接

一个驱动 = 一个目录，放进程序旁边的 `drivers/`：

```
drivers/
  vendor.turbidity.acme/
    manifest.json
    Vendor.Turbidity.Acme.dll
```

`manifest.json` 见 `samples/drivers/vendor.turbidity.acme/manifest.json`。
驱动只需引用 `Tec.Driver.Abi`，实现 `IDeviceDriver` + `IDeviceSession`，
把读数做成 `IScalarSensor` 就能进趋势、判据、反馈、记录、导出——
上层一行都不用改。**驱动不提供界面**，只给 schema。

主版本不匹配 ABI 的包会在设备库里灰掉并写明原因，不会静默消失。
