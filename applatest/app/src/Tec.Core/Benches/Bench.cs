using Tec.Driver.Abi;

namespace Tec.Core.Benches;

public readonly record struct Point(double X, double Y);

/// <summary>泵只有 2 套却有 4 个通道——共享必须在模型里记下来（§5.1）。</summary>
public enum BindingMode { Exclusive, Shared }

public sealed record Binding(string DeviceId, int ChannelNumber, BindingMode Mode = BindingMode.Exclusive)
{
    /// <summary>一台设备有多个孔/多路时，绑的是哪一路。</summary>
    public int Port { get; init; }
}

/// <summary>2+2 拆分：工位编组（FR-2.2）。</summary>
public sealed class Station
{
    public required string Name { get; init; }
    public List<int> Channels { get; } = new();
}

/// <summary>
/// 设备停靠在宿主（反应器）的哪一侧。探头从上方插进釜口，加料 / 取样 / 液相
/// 走侧面接口——这是台面上真实的装配关系，不只是画得好看。
/// </summary>
public enum DockSide { None, Top, Left, Right }

public sealed class DeviceInstance
{
    /// <summary>停靠在哪台设备上（宿主的 InstanceId）。空 = 自由摆放。</summary>
    public string? DockHostId { get; set; }
    public DockSide Dock { get; set; } = DockSide.None;
    /// <summary>顶部停靠时插在第几个孔位（0 = A 孔，1 = B 孔）。侧面停靠时无意义。</summary>
    public int DockSlot { get; set; }
    /// <summary>接在宿主的哪个具名接口上（T1a / R2 / BUS…）。一个接口只接一台设备。</summary>
    public string? DockAnchor { get; set; }
    /// <summary>侧接时停在左还是右（L / R），决定走线用哪个插头。</summary>
    public string? DockSideTag { get; set; }

    public required string DriverId { get; init; }
    public required string InstanceId { get; init; }
    public string? Label { get; set; }
    public ParameterSet Connection { get; init; } = new();
    public ParameterSet Config { get; init; } = new();
    public Point Position { get; set; }
    /// <summary>用仿真会话而不是真硬件。仿真数据全部带 Quality.Simulated。</summary>
    public bool Simulated { get; set; } = true;

    public string Display => string.IsNullOrWhiteSpace(Label) ? InstanceId : Label!;
}

public sealed class Bench
{
    public const int CurrentSchemaVersion = 1;

    public string Name { get; set; } = "默认台面";
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public List<DeviceInstance> Devices { get; } = new();
    public List<Binding> Bindings { get; } = new();
    public List<Station> Stations { get; } = new();

    public DeviceInstance? Device(string instanceId)
    {
        foreach (var d in Devices) if (d.InstanceId == instanceId) return d;
        return null;
    }

    /// <summary>绑到某个通道上的所有非宿主设备（探头、泵）。</summary>
    public IEnumerable<Binding> BindingsOf(int channel)
        => Bindings.Where(b => b.ChannelNumber == channel);

    /// <summary>被多个通道共享的设备——配方校验要拿它做资源冲突检测（§10.4）。</summary>
    public IEnumerable<string> SharedDeviceIds()
        => Bindings.Where(b => b.Mode == BindingMode.Shared)
                   .Select(b => b.DeviceId)
                   .Distinct(StringComparer.Ordinal);
}
