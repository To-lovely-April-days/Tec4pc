using TecControl.Core.Control;
using Tec.Drivers.Rd105;

namespace Tec.Core.Tests;

/// <summary>
/// 双向挡没整过的温度不让下发（用户定的，2026-10-29）。只验别的规矩的组合主机用例开会话前先摆一张 −40 ~ 230 ℃ 都整过的双向挡表：
/// 参数跟没有表时回路用的手动缺省那组一模一样（8 / 0.02 / 0，不带外环、不带稳态偏置），原来的用例行为不变。
/// 增益表目录还是用户目录（测试自己没设）就先换成临时目录，不往用户目录里写。
/// </summary>
internal static class BothTableSeed
{
    public static void Seed(string instanceId)
    {
        var appDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TecDrivers", "gains");
        var env = Environment.GetEnvironmentVariable("TEC_GAINS_DIR");
        if (Rd105HostControl.GainsDir == appDefault || Rd105HostControl.GainsDir == env)
            Rd105HostControl.UseGainsDir(Path.Combine(Path.GetTempPath(), "tec-both-" + Guid.NewGuid().ToString("N")));   // 测试流程里一直有效，不收回
        foreach (var tc in new[] { 1, 2 })
        {
            var path = Rd105HostControl.BothGainsPath(instanceId, tc);
            if (File.Exists(path)) continue;
            var sch = new PidGainSchedule { SegmentBoundaryC = double.NaN };
            for (var t = -40; t <= 230; t += 30)
                sch.Learn(new GainPoint(t, Rd105HostControl.FallbackInner, UltimateGainKu: 1, UltimatePeriodTuSeconds: 100));
            Rd105HostControl.Save(sch, path);
        }
    }
}
