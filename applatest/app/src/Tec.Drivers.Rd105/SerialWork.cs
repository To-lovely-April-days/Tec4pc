namespace Tec.Drivers.Rd105;

/// <summary>
/// 【本地改动】串口上的一问一答放到**专用线程**上做，不占公共线程池。
///
/// 一问一答是同步阻塞的（发帧、等应答、超时、退避重发，全程 Thread.Sleep / 阻塞读）。从前用 Task.Run 丢进线程池：
/// 设备通着时一拍几十毫秒，看不出毛病；一台设备不通时每拍要卡将近 2 s 才认输（500 ms × 3 次 + 两次 200 ms 退避），
/// 周期又只有 1 s——等于长期占住一个线程池线程。线程池的下限是 CPU 核数（工控机 2~4 核），几台同时不通就占满，
/// 别的设备的指令、定时器、界面的异步续接全在后面排队，表现就是「别的模块通信异常时，搅拌都关不掉」（用户 2026-11-10）。
/// 线程池每秒只肯多开一两个线程，排队几秒起步。
///
/// 专用线程（LongRunning）每笔事务现开一个，开线程约 0.1 ms，全机一秒十来笔事务，这点开销换来的是：
/// 谁不通只卡自己那一个线程，跟别的模块、跟界面都不相干。
/// </summary>
public static class SerialWork
{
    public static Task Run(Action work, CancellationToken ct = default)
        => Task.Factory.StartNew(work, ct, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    /// <summary>
    /// 兜底：把线程池下限抬到至少 32。串口事务已经不在池里了，但安全层落动作、驱动里个别同步等待仍会短暂占池线程，
    /// 下限抬高以后它们撞在一起也不用等线程池慢慢加线程。程序启动时调一次。
    /// </summary>
    public static void RaiseThreadPoolFloor(int floor = 32)
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        if (worker < floor || io < floor) ThreadPool.SetMinThreads(Math.Max(worker, floor), Math.Max(io, floor));
    }
}
