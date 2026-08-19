using Tec.App.Services;
using Tec.Core.Records;
using Tec.Core.Users;

namespace Tec.App.ViewModels;

/// <summary>
/// 登录页（照 gbglogin 设计稿）。盖在整个程序前面，登录成功才放行；
/// 注销回到这里。两个台面：表单 → 登录成功卡（会话信息都是真的：
/// 上次登录来自账号库，工作站是机器名）。设计稿里那段四步进度动画没搬——
/// 程序在登录前就加载完了，没有对应的真实过程，转假圈违背不伪造原则。
///
/// 设计稿的「记住密码」落成「记住用户名」：共用工作站上把密码存在盘里，
/// 审计追踪就没法说清「这次登录真是这个人」。语言切换也没搬——整个程序
/// 只有中文，登录页单独切英文是个假开关。
/// </summary>
public sealed class LoginViewModel : ViewModelBase
{
    private readonly Workspace _ws;

    public LoginViewModel(Workspace ws, Action onEntered)
    {
        _ws = ws;
        Entered = onEntered;
        Submit = new RelayCommand(DoSubmit);
        Cancel = new RelayCommand(DoClear);
        ToggleReset = new RelayCommand(() => { ShowReset = !ShowReset; Raise(nameof(ShowReset)); });
        Enter = new RelayCommand(DoEnter);
        BackToForm = new RelayCommand(() => { _ws.SignOut(); ShowForm(); });

        _user = ws.Users.RememberedName;
        _remember = _user.Length > 0;
        ArtImage = LoadArt();
    }

    /// <summary>
    /// 左侧图位（原型 .lg-art 252×176）：一张位图，不是 SVG。按顺序找
    /// 程序目录、数据目录下的 login-visual.png / .jpg，找到哪张用哪张；
    /// 一张都没有就显示原型那种虚线占位，把该放哪写在占位里。
    /// </summary>
    public Avalonia.Media.Imaging.Bitmap? ArtImage { get; }
    public bool HasArt => ArtImage is not null;
    public string ArtHint =>
        "login-visual.png\n252 × 176\n放进程序目录或数据目录即显示";

    private static Avalonia.Media.Imaging.Bitmap? LoadArt()
    {
        // 现场放的文件优先于内置资源：换图不用重新出包
        foreach (var dir in new[] { AppContext.BaseDirectory, ExperimentStore.DataDir })
        foreach (var name in new[] { "login-visual.png", "login-visual.jpg" })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try { return new Avalonia.Media.Imaging.Bitmap(path); }
            catch { /* 图坏了当没有，走下一级 */ }
        }
        try
        {
            var uri = new Uri("avares://Tec.App/Assets/login-visual.png");
            if (Avalonia.Platform.AssetLoader.Exists(uri))
                return new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(uri));
        }
        catch { }
        return null;
    }

    /// <summary>点了「进入工作站」。外壳靠它换窗口：关登录窗、开主窗。</summary>
    public event EventHandler? EnteredWorkstation;

    /// <summary>登录成功、点「进入工作站」之后干什么（外壳收起这层）。</summary>
    public Action Entered { get; }

    public RelayCommand Submit { get; }
    public RelayCommand Cancel { get; }
    public RelayCommand ToggleReset { get; }
    public RelayCommand Enter { get; }
    public RelayCommand BackToForm { get; }

    // ── 状态 ─────────────────────────────────────────────────────────

    private bool _showing = true;
    public bool Showing { get => _showing; private set => Set(ref _showing, value); }

    /// <summary>form / done 两个台面。</summary>
    private bool _done;
    public bool IsForm => !_done;
    public bool IsDone => _done;

    private string _user = "";
    public string User
    {
        get => _user;
        set { if (Set(ref _user, value)) ClearError(); }
    }

    private string _pass = "";
    public string Pass
    {
        get => _pass;
        set { if (Set(ref _pass, value)) ClearError(); }
    }

    private bool _reveal;
    public bool Reveal { get => _reveal; set => Set(ref _reveal, value); }

    private bool _remember;
    public bool Remember { get => _remember; set => Set(ref _remember, value); }

    public bool ShowReset { get; private set; }

    private string _error = "";
    public string Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;

    private void ClearError() { if (HasError) Error = ""; }

    /// <summary>首次开机的提示：默认管理员是公开的初始密码，登录后请尽快改。</summary>
    public bool ShowSeedNote => _ws.Users.SeededDefault;
    public string SeedNote => $"首次启动：已创建管理员账号 {UserStore.DefaultAdmin}（初始密码 {UserStore.DefaultPassword}），登录后请立即在「用户管理」里修改。";

    public string VersionLine
    {
        get
        {
            var ver = typeof(Workspace).Assembly.GetName().Version?.ToString() ?? "";
            return $"版本 {ver} · 工作站 {Environment.MachineName}";
        }
    }

    public string FootNote =>
        "所有登录尝试（无论成功与否）均连同用户、工作站与时间戳写入审计追踪。禁止未经授权访问本系统。";

    public string ResetNote =>
        "按策略已禁用自助重置密码。解锁或重置账号请联系系统管理员。";

    // ── 登录成功卡（都是真数据） ─────────────────────────────────────

    public string DoneTitle { get; private set; } = "";
    public string DoneSub { get; private set; } = "";
    public string DoneSession { get; private set; } = "";
    public string DoneStation { get; private set; } = Environment.MachineName;
    public string DonePrev { get; private set; } = "";

    // ── 动作 ─────────────────────────────────────────────────────────

    private void DoSubmit()
    {
        var name = User.Trim();
        if (name.Length == 0) { Error = "请输入用户名。"; return; }
        if (Pass.Length == 0) { Error = "请输入密码。"; return; }

        var r = _ws.Users.TryLogin(name, Pass);
        var station = Environment.MachineName;
        switch (r.Outcome)
        {
            case LoginOutcome.Ok:
                _ws.Users.RememberedName = Remember ? r.User!.Name : "";
                _ws.Users.Save();
                _ws.SignIn(r.User!);
                Pass = "";
                DoneTitle = $"{r.User!.Display}，登录成功";
                DoneSub = $"角色：{r.User.RoleName} · 工作站 {station}";
                DoneSession = _ws.LoginAt?.ToString("HH:mm:ss") ?? "";
                DonePrev = r.User.LastLoginAt is { } prev ? prev.ToString("MM-dd HH:mm") : "首次登录";
                _done = true;
                RaiseAll(nameof(IsForm), nameof(IsDone), nameof(DoneTitle), nameof(DoneSub),
                         nameof(DoneSession), nameof(DonePrev));
                break;

            case LoginOutcome.Locked:
                _ws.Log.Write("登录", $"账号已锁定（{name} · 工作站 {station}）", name, LogLevel.Warn);
                Pass = "";        // 先清密码再挂错误：Pass 的 setter 会清错误条，倒过来就白挂了
                Error = "账号已锁定。失败次数过多，请联系系统管理员解锁。本次锁定已写入审计追踪。";
                break;

            case LoginOutcome.Disabled:
                _ws.Log.Write("登录", $"停用账号尝试登录（{name} · 工作站 {station}）", name, LogLevel.Warn);
                Pass = "";
                Error = "该账号已停用，请联系系统管理员。";
                break;

            default:
                _ws.Log.Write("登录", $"登录失败（{name} · 工作站 {station}）", name, LogLevel.Warn);
                Pass = "";
                Error = r.TriesLeft > 0
                    ? $"用户名或密码错误，还剩 {r.TriesLeft} 次机会，之后账号将被锁定。"
                    : "用户名或密码错误。";
                break;
        }
    }

    private void DoClear()
    {
        User = "";
        Pass = "";
        Error = "";
        RaiseAll(nameof(User), nameof(Pass));
    }

    /// <summary>进入工作站：收起登录层，通知外壳换窗口。</summary>
    private void DoEnter()
    {
        Showing = false;
        Entered();
        EnteredWorkstation?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>注销后（或外壳要求重新登录时）回到表单台面。</summary>
    public void ShowForm()
    {
        _done = false;
        Pass = "";
        Error = "";
        Reveal = false;
        if (!Remember) User = "";
        Showing = true;
        RaiseAll(nameof(IsForm), nameof(IsDone), nameof(User), nameof(Pass), nameof(ShowSeedNote));
    }
}
