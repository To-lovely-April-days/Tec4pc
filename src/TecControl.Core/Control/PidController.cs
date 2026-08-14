namespace TecControl.Core.Control;

/// <summary>
/// 上位机位置式 PID 控制器（自研控温算法核心）。
///
/// 工程单位约定：输入为温度（℃），输出为 TEC 占空比（%，-100~+100，正值加热方向）。
///   Kp：%/℃          比例增益
///   Ki：%/(℃·s)      积分增益
///   Kd：%·s/℃        微分增益
///
/// 算法特性：
///   1. 微分作用于测量值而非误差（改设定值时无微分踢）；
///   2. 微分项带一阶低通滤波，抑制温度量化噪声；
///   3. 条件积分 + 积分限幅两重抗积分饱和；
///   4. 输出限幅。
/// </summary>
public sealed class PidController
{
    private readonly object _lock = new();

    private double _integral;
    private double _lastPv;
    private double _dFiltered;
    private bool _first = true;

    public double Kp { get; private set; }
    public double Ki { get; private set; }
    public double Kd { get; private set; }

    /// <summary>输出下限/上限（%）。</summary>
    public double OutputMin { get; private set; } = -100;
    public double OutputMax { get; private set; } = 100;

    /// <summary>微分低通滤波时间常数（秒）。越大越平滑、越迟钝。</summary>
    public double DerivativeFilterSeconds { get; private set; } = 2.0;

    /// <summary>最近一次计算的各项贡献（用于界面调参观察）。</summary>
    public double LastP { get; private set; }
    public double LastI { get; private set; }
    public double LastD { get; private set; }

    public void Configure(double kp, double ki, double kd,
        double outputMin = -100, double outputMax = 100, double derivativeFilterSeconds = 2.0)
    {
        if (outputMax <= outputMin)
            throw new ArgumentException("输出上限必须大于下限");
        lock (_lock)
        {
            Kp = kp;
            Ki = ki;
            Kd = kd;
            OutputMin = outputMin;
            OutputMax = outputMax;
            DerivativeFilterSeconds = Math.Max(0, derivativeFilterSeconds);
            _integral = Math.Clamp(_integral, outputMin, outputMax);
        }
    }

    /// <summary>
    /// 只更新三个增益，不动限幅与内部状态。用于增益调度按设定值连续调整参数：
    /// 本实现的积分项存的是"输出贡献"而非累积误差，改 Ki 不会引起跳变；
    /// 改 Kp 仅影响当前比例项，稳态误差很小时扰动可忽略。
    /// </summary>
    public void SetGains(double kp, double ki, double kd)
    {
        lock (_lock)
        {
            Kp = kp;
            Ki = ki;
            Kd = kd;
        }
    }

    /// <summary>
    /// 只更新输出限幅，不动增益与内部状态。用于串级外环的偏置限幅随温度调度：
    /// 积分项按新限幅截断，因此收窄限幅会立即生效，不会残留超限的历史积分。
    /// </summary>
    public void SetOutputLimits(double outputMin, double outputMax)
    {
        if (outputMax <= outputMin)
            throw new ArgumentException("输出上限必须大于下限");
        lock (_lock)
        {
            OutputMin = outputMin;
            OutputMax = outputMax;
            _integral = Math.Clamp(_integral, outputMin, outputMax);
        }
    }

    /// <summary>清零内部状态（启动控温前调用）。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _integral = 0;
            _dFiltered = 0;
            _first = true;
            LastP = LastI = LastD = 0;
        }
    }

    /// <summary>
    /// 预置积分项（启动控温时用）。稳态所需输出往往不为零，让积分从零累积会白白多花
    /// 几个时间常数；预置到稳态值附近可显著缩短整定时间而不必加大增益。
    /// </summary>
    public void PresetIntegral(double value)
    {
        lock (_lock)
        {
            _integral = Math.Clamp(value, OutputMin, OutputMax);
            LastI = _integral;
        }
    }

    /// <summary>
    /// 执行一拍 PID 计算。
    /// </summary>
    /// <param name="setpoint">目标温度（℃）</param>
    /// <param name="measurement">当前温度（℃）</param>
    /// <param name="dtSeconds">距上一拍的实际时间（秒）</param>
    /// <returns>输出占空比（%），已限幅</returns>
    /// <param name="freezeIntegral">
    /// 串级控制中，内环输出饱和时应冻结外环积分，否则外环会持续累积无法执行的需求。
    /// </param>
    public double Update(double setpoint, double measurement, double dtSeconds, bool freezeIntegral = false)
    {
        lock (_lock)
        {
            if (dtSeconds <= 0 || double.IsNaN(dtSeconds)) dtSeconds = 1e-3;

            var error = setpoint - measurement;

            if (_first)
            {
                _lastPv = measurement;
                _dFiltered = 0;
                _first = false;
            }

            // 微分作用于测量值，一阶低通滤波
            var rawDerivative = (measurement - _lastPv) / dtSeconds;
            var alpha = DerivativeFilterSeconds <= 0
                ? 1.0
                : dtSeconds / (DerivativeFilterSeconds + dtSeconds);
            _dFiltered += alpha * (rawDerivative - _dFiltered);
            _lastPv = measurement;

            var p = Kp * error;
            var d = -Kd * _dFiltered;

            // 条件积分：输出已饱和且误差会加剧饱和时暂停积分
            var unsaturated = p + _integral + d;
            var pushingHigh = unsaturated >= OutputMax && error > 0;
            var pushingLow = unsaturated <= OutputMin && error < 0;
            if (!freezeIntegral && !pushingHigh && !pushingLow)
                _integral = Math.Clamp(_integral + Ki * error * dtSeconds, OutputMin, OutputMax);

            var output = Math.Clamp(p + _integral + d, OutputMin, OutputMax);

            LastP = p;
            LastI = _integral;
            LastD = d;
            return output;
        }
    }
}
