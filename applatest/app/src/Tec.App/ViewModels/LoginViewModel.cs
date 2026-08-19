using System.Collections.ObjectModel;
using Tec.App.Services;
using Tec.Core.Records;
using Tec.Core.Users;

namespace Tec.App.ViewModels;

/// <summary>语言菜单里的一项。</summary>
public sealed class LangOption : ViewModelBase
{
    public LangOption(LoginText text) => Text = text;
    public LoginText Text { get; }
    public string Name => Text.Name;
    public string Sub => Text.Sub;

    private bool _on;
    public bool IsCurrent { get => _on; set => Set(ref _on, value); }
}

/// <summary>
/// 登录页（照 gbglogin 设计稿）。开机只开这一扇 840 的窗，登录成功点
/// 「进入工作站」才开主窗口；注销把主窗口收起来、再开回这扇窗。
/// 两个台面：表单 → 登录成功卡（会话信息都是真的：上次登录来自账号库，
/// 工作站是机器名）。
///
/// 设计稿里那段四步进度动画没搬——程序在登录前就加载完了，没有对应的
/// 真实过程，转假圈违背不伪造原则。
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
        BackToForm = new RelayCommand(DoSignOut);
        PickLang = new RelayCommand(p => { if (p is LangOption o) SetLang(o.Text.Code); });

        _text = LoginText.Of(ws.Users.Language);
        foreach (var t in LoginText.All) Languages.Add(new LangOption(t) { IsCurrent = t.Code == _text.Code });

        // 记住的那一对：名字 + 解得开的密码。解不开（换了机器）就只回填名字
        _user = ws.Users.RememberedName;
        _pass = _user.Length > 0 ? ws.Users.RecallPassword() : "";
        _remember = _user.Length > 0 && _pass.Length > 0;

        ArtImage = LoadArt();
    }

    /// <summary>登录成功、点「进入工作站」之后干什么（外壳收起这层）。</summary>
    public Action Entered { get; }

    public RelayCommand Submit { get; }
    public RelayCommand Cancel { get; }
    public RelayCommand ToggleReset { get; }
    public RelayCommand Enter { get; }
    public RelayCommand BackToForm { get; }
    public RelayCommand PickLang { get; }

    /// <summary>点了「进入工作站」。外壳靠它换窗口：开主窗、关登录窗。</summary>
    public event EventHandler? EnteredWorkstation;

    /// <summary>在登录成功卡上点了「注销」。外壳靠它收起主窗口。</summary>
    public event EventHandler? SignedOut;

    // ── 语言 ─────────────────────────────────────────────────────────

    private LoginText _text;
    /// <summary>当前这套文案。界面上所有字都绑到它身上（T.XXX）。</summary>
    public LoginText T => _text;

    public ObservableCollection<LangOption> Languages { get; } = new();

    public void SetLang(string code)
    {
        if (code == _text.Code) return;
        _text = LoginText.Of(code);
        foreach (var o in Languages) o.IsCurrent = o.Text.Code == code;
        _ws.Users.Language = code;
        _ws.Users.Save();
        // 文案全换，连带那几条按文案拼出来的（错误、成功卡、占位框）
        RaiseAll(nameof(T), nameof(Error), nameof(SeedNote), nameof(VersionLine), nameof(ArtHint),
                 nameof(DoneTitle), nameof(DoneSub), nameof(DonePrev), nameof(LangName));
    }

    public string LangName => _text.Name;

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

    /// <summary>错误用「哪一条 + 参数」记着，换语言时能重新拼一遍。</summary>
    private Func<LoginText, string>? _err;
    public string Error => _err?.Invoke(_text) ?? "";
    public bool HasError => _err is not null;

    private void Fail(Func<LoginText, string> pick)
    {
        _err = pick;
        RaiseAll(nameof(Error), nameof(HasError));
    }

    private void ClearError()
    {
        if (_err is null) return;
        _err = null;
        RaiseAll(nameof(Error), nameof(HasError));
    }

    /// <summary>首次开机的提示：默认管理员是公开的初始密码，登录后请尽快改。</summary>
    public bool ShowSeedNote => _ws.Users.SeededDefault;
    public string SeedNote => string.Format(_text.SeedNoteFmt, UserStore.DefaultAdmin, UserStore.DefaultPassword);

    public string VersionLine => string.Format(_text.VersionFmt,
        typeof(Workspace).Assembly.GetName().Version?.ToString() ?? "", Environment.MachineName);

    // ── 左侧图位 ─────────────────────────────────────────────────────

    /// <summary>现场资源目录：程序运行目录下的 Resources\（exe 旁边，见其中的说明.txt）。</summary>
    public static string ResourceDir => Path.Combine(AppContext.BaseDirectory, "Resources");

    /// <summary>
    /// 左侧图位（原型 .lg-art 252×176）：一张位图，不是 SVG。**只认一个地方**——
    /// 程序运行目录下的 Resources\login-visual.png（也认 .jpg）。换图不用重新出包，
    /// 换掉文件重启即可。没有这个文件时显示虚线占位，把完整路径写在占位里。
    /// </summary>
    public Avalonia.Media.Imaging.Bitmap? ArtImage { get; }
    public bool HasArt => ArtImage is not null;
    public string ArtHint => string.Format(_text.ArtHintFmt, ResourceDir);

    private static Avalonia.Media.Imaging.Bitmap? LoadArt()
    {
        foreach (var name in new[] { "login-visual.png", "login-visual.jpg" })
        {
            var path = Path.Combine(ResourceDir, name);
            if (!File.Exists(path)) continue;
            try { return new Avalonia.Media.Imaging.Bitmap(path); }
            catch { /* 图坏了当没有，显示占位框，别让登录页开不出来 */ }
        }
        return null;
    }

    // ── 登录成功卡（都是真数据） ─────────────────────────────────────

    private UserAccount? _doneUser;
    private DateTimeOffset? _donePrevAt;

    public string DoneTitle => _doneUser is null ? "" : string.Format(_text.OkTitleFmt, _doneUser.Display);
    public string DoneSub => _doneUser is null ? "" : string.Format(_text.OkSubFmt, RoleName(_doneUser), Environment.MachineName);
    public string DoneSession { get; private set; } = "";
    public string DoneStation => Environment.MachineName;
    public string DonePrev => _donePrevAt is { } t ? t.ToString("MM-dd HH:mm") : _text.PrevNever;

    private string RoleName(UserAccount u) => u.Role == UserRole.Admin ? _text.RoleAdmin : _text.RoleOperator;

    // ── 动作 ─────────────────────────────────────────────────────────

    private void DoSubmit()
    {
        var name = User.Trim();
        if (name.Length == 0) { Fail(t => t.ErrUser); return; }
        if (Pass.Length == 0) { Fail(t => t.ErrPass); return; }

        var r = _ws.Users.TryLogin(name, Pass);
        var station = Environment.MachineName;
        switch (r.Outcome)
        {
            case LoginOutcome.Ok:
                // 勾了就把这一对记下来（密码绑本机加密）；没勾就连旧的一起清
                if (Remember) _ws.Users.Remember(r.User!.Name, Pass);
                else _ws.Users.ForgetRemembered();

                _ws.SignIn(r.User!);
                _doneUser = r.User;
                _donePrevAt = r.User!.LastLoginAt;
                DoneSession = _ws.LoginAt?.ToString("HH:mm:ss") ?? "";
                Pass = "";
                _done = true;
                RaiseAll(nameof(IsForm), nameof(IsDone), nameof(DoneTitle), nameof(DoneSub),
                         nameof(DoneSession), nameof(DonePrev));
                break;

            case LoginOutcome.Locked:
                _ws.Log.Write("登录", $"账号已锁定（{name} · 工作站 {station}）", name, LogLevel.Warn);
                Pass = "";        // 先清密码再挂错误：Pass 的 setter 会清错误条，倒过来就白挂了
                Fail(t => t.ErrLocked);
                break;

            case LoginOutcome.Disabled:
                _ws.Log.Write("登录", $"停用账号尝试登录（{name} · 工作站 {station}）", name, LogLevel.Warn);
                Pass = "";
                Fail(t => t.ErrDisabled);
                break;

            default:
                _ws.Log.Write("登录", $"登录失败（{name} · 工作站 {station}）", name, LogLevel.Warn);
                Pass = "";
                var left = r.TriesLeft;
                Fail(t => left > 0 ? string.Format(t.ErrCredFmt, left) : t.ErrCredNoCount);
                break;
        }
    }

    private void DoClear()
    {
        User = "";
        Pass = "";
        ClearError();
        RaiseAll(nameof(User), nameof(Pass));
    }

    /// <summary>进入工作站：收起登录层，通知外壳换窗口。</summary>
    private void DoEnter()
    {
        Showing = false;
        Entered();
        EnteredWorkstation?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>登录成功卡上的「注销」：还没进过工作站，退回表单就完了。</summary>
    private void DoSignOut()
    {
        _ws.SignOut();
        ShowForm();
        SignedOut?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>回到表单台面。外壳注销时也调它。</summary>
    public void ShowForm()
    {
        _done = false;
        _doneUser = null;
        Pass = "";
        ClearError();
        Reveal = false;
        ShowReset = false;
        // 勾着记住就把那一对再填回来，好让下一次直接按登录
        if (Remember && _ws.Users.RememberedName.Length > 0)
        {
            _user = _ws.Users.RememberedName;
            _pass = _ws.Users.RecallPassword();
        }
        else _user = "";
        Showing = true;
        RaiseAll(nameof(IsForm), nameof(IsDone), nameof(User), nameof(Pass),
                 nameof(ShowSeedNote), nameof(ShowReset));
    }
}
