using Tec.Core;
using Tec.Core.Benches;
using Tec.Core.Data;
using Tec.Core.Execution;
using Tec.Core.Records;
using Tec.Driver.Abi;
using Tec.DriverHost;

namespace Tec.Hmi.Ui;

/// <summary>
/// HMI 面板对宿主的全部要求。两个宿主各自实现：
/// 工作站的 Workspace（面板弹成一扇窗，跟台面共用引擎），和设备端
/// Tec.Hmi.Runtime 的精简工作台（面板就是整台机器的界面）。
/// 面板只认识这一张口子——它不知道也不关心背后是完整工作站还是设备端精简版。
/// </summary>
public interface IHmiHost
{
    /// <summary>统一的钟。仿真加速时是虚拟钟，真机恒等于墙钟。</summary>
    VirtualClock Clock { get; }

    /// <summary>执行引擎：面板序列启动、报警确认、运行记录、安全层都从它拿。</summary>
    RunEngine Engine { get; }

    /// <summary>数据管线：面板上显示的每一路实测都从这儿现读。</summary>
    DataPipeline Pipeline { get; }

    /// <summary>系统日志。面板操作也是操作，记一笔。</summary>
    SystemLog Log { get; }

    /// <summary>批次归档：数据导出页的历史批次从这儿来。</summary>
    RunArchive Archive { get; }

    /// <summary>驱动目录：系统页显示驱动版本用。</summary>
    DriverCatalog Drivers { get; }

    /// <summary>台面：设备实例（连接参数、模拟标记）从这儿查。</summary>
    Bench Bench { get; }

    /// <summary>当前操作人。面板操作的署名。</summary>
    string Operator { get; }

    /// <summary>数据目录（面板序列、快照、导出都写在它下面）。</summary>
    string DataDir { get; }

    Channel? ChannelOf(int number);

    /// <summary>这台设备开着的会话；没开（打不开 / 台面上没有）就是 null。头卡上的「已连接」按它说。</summary>
    IDeviceSession? Session(string instanceId);

    /// <summary>这台设备上一次没打开的原因（驱动报的原话）；开着的 / 没试过的是 null。</summary>
    string? OpenFailure(string instanceId);

    /// <summary>该开新批次就开（幂等）。面板启动序列前调。</summary>
    bool BeginBatch();
}
