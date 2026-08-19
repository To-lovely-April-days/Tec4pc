namespace Tec.App.ViewModels;

/// <summary>
/// 登录页的一套文案（照原型 gbglogin 的 I18N 表）。**只管登录页**——
/// 程序其它页面目前只有中文，切到 English 也只有这一页会变。这一点写在
/// 语言菜单的副标题里，免得操作人以为整机都能切。
///
/// 加一种语言 = 在 <see cref="All"/> 里加一条，别处不用动。
/// </summary>
public sealed record LoginText(
    string Code,
    string Name,          // 语言菜单里显示的名字
    string Sub,           // 语言菜单里那行小字：这套文案管到哪
    string WindowTitle,
    string BrandSub,
    string H1,
    string Lead,
    string User, string UserPh,
    string Pass, string ShowPass,
    string Remember, string RememberTip,
    string Forgot, string ResetNote,
    string Cancel, string SignIn,
    string Foot,
    string ErrUser, string ErrPass,
    string ErrCredFmt,    // {0} = 还剩几次
    string ErrCredNoCount,
    string ErrLocked, string ErrDisabled,
    string SeedNoteFmt,   // {0} = 账号，{1} = 初始密码
    string OkTitleFmt,    // {0} = 署名
    string OkSubFmt,      // {0} = 角色，{1} = 工作站
    string RoleAdmin, string RoleOperator,
    string KSession, string KStation, string KPrev, string PrevNever,
    string SignOut, string OpenApp,
    string VersionFmt,    // {0} = 版本号，{1} = 工作站
    string ArtHintFmt,    // {0} = 完整路径
    string Minimize, string Close, string LangMenu)
{
    public static readonly LoginText Zh = new(
        Code: "zh", Name: "中文", Sub: "简体 · 仅登录页",
        WindowTitle: "登录 - TecStudio 平行合成工作站",
        BrandSub: "平行合成工作站 · PSW-4",
        H1: "登录",
        Lead: "请输入账号与密码以打开工作站。",
        User: "用户名", UserPh: "例如 zhang",
        Pass: "密码", ShowPass: "显示密码",
        Remember: "记住密码",
        RememberTip: "密码加密后存在本机，换台机器解不开。共用工作站请不要勾。",
        Forgot: "忘记密码？",
        ResetNote: "按策略已禁用自助重置密码。解锁或重置账号请联系系统管理员。",
        Cancel: "取消", SignIn: "登录",
        Foot: "所有登录尝试（无论成功与否）均连同用户、工作站与时间戳写入审计追踪。禁止未经授权访问本系统。",
        ErrUser: "请输入用户名。", ErrPass: "请输入密码。",
        ErrCredFmt: "用户名或密码错误，还剩 {0} 次机会，之后账号将被锁定。",
        ErrCredNoCount: "用户名或密码错误。",
        ErrLocked: "账号已锁定。失败次数过多，请联系系统管理员解锁。本次锁定已写入审计追踪。",
        ErrDisabled: "该账号已停用，请联系系统管理员。",
        SeedNoteFmt: "首次启动：已创建管理员账号 {0}（初始密码 {1}），登录后请立即在「用户管理」里修改。",
        OkTitleFmt: "{0}，登录成功",
        OkSubFmt: "角色：{0} · 工作站 {1}",
        RoleAdmin: "管理员", RoleOperator: "操作员",
        KSession: "会话开始", KStation: "工作站", KPrev: "上次登录", PrevNever: "首次登录",
        SignOut: "注销", OpenApp: "进入工作站",
        VersionFmt: "版本 {0} · 工作站 {1}",
        ArtHintFmt: "login-visual.png · 252 × 176\n放进程序目录的 Resources 文件夹：\n{0}",
        Minimize: "最小化", Close: "关闭", LangMenu: "语言");

    public static readonly LoginText En = new(
        Code: "en", Name: "English", Sub: "UK · sign-in page only",
        WindowTitle: "Sign in - TecStudio Parallel Synthesis Workstation",
        BrandSub: "Parallel Synthesis Workstation · PSW-4",
        H1: "Sign in",
        Lead: "Enter your credentials to open the workstation.",
        User: "User name", UserPh: "e.g. zhang",
        Pass: "Password", ShowPass: "Show password",
        Remember: "Remember my password",
        RememberTip: "Stored encrypted on this machine only; it cannot be read on another. Do not tick on a shared workstation.",
        Forgot: "Forgot your password?",
        ResetNote: "Self-service password reset is disabled by policy. Contact the system administrator to unlock or reset an account.",
        Cancel: "Cancel", SignIn: "Sign in",
        Foot: "All sign-in attempts, successful or not, are written to the audit trail with user, workstation and timestamp. Unauthorised access to this system is prohibited.",
        ErrUser: "Enter a user name.", ErrPass: "Enter a password.",
        ErrCredFmt: "Invalid user name or password. {0} attempt(s) remaining before the account is locked.",
        ErrCredNoCount: "Invalid user name or password.",
        ErrLocked: "Account locked. Too many failed attempts. Contact the system administrator to unlock this account. The lockout has been written to the audit trail.",
        ErrDisabled: "This account is disabled. Contact the system administrator.",
        SeedNoteFmt: "First start: administrator account {0} was created (initial password {1}). Change it in User management as soon as you sign in.",
        OkTitleFmt: "Signed in as {0}",
        OkSubFmt: "Role: {0} · Workstation {1}",
        RoleAdmin: "Administrator", RoleOperator: "Operator",
        KSession: "Session started", KStation: "Workstation", KPrev: "Previous sign-in", PrevNever: "First sign-in",
        SignOut: "Sign out", OpenApp: "Open workstation",
        VersionFmt: "Version {0} · workstation {1}",
        ArtHintFmt: "login-visual.png · 252 × 176\nPut it in the Resources folder next to the program:\n{0}",
        Minimize: "Minimise", Close: "Close", LangMenu: "Language");

    public static readonly IReadOnlyList<LoginText> All = new[] { Zh, En };

    public static LoginText Of(string code)
        => All.FirstOrDefault(t => t.Code == code) ?? Zh;
}
