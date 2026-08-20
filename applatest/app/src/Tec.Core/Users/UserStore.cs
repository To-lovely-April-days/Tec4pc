using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Tec.Core.Persistence;

namespace Tec.Core.Users;

public enum UserRole
{
    /// <summary>管理员：多一层用户管理（建账号、解锁、重置密码、停用、改角色）。</summary>
    Admin,
    /// <summary>主管：能做实验，也能复核签字；账号的事仍归管理员。</summary>
    Supervisor,
    /// <summary>操作员：日常做实验的那一层。</summary>
    Operator
}

/// <summary>
/// 一个账号。密码只存 PBKDF2 散列 + 盐，明文在校验完的那一刻就丢掉——
/// users.json 拿去哪儿都翻不出密码来。
/// </summary>
public sealed class UserAccount
{
    public string Name { get; set; } = "";          // 登录名（唯一，忽略大小写）
    public string Display { get; set; } = "";       // 署名用的名字，记录和报告里出现的是它
    public UserRole Role { get; set; } = UserRole.Operator;
    public string Hash { get; set; } = "";          // Base64(PBKDF2-SHA256, 32B)
    public string Salt { get; set; } = "";          // Base64(16B)
    public int Iterations { get; set; } = UserStore.Pbkdf2Iterations;
    public bool Disabled { get; set; }
    public bool Locked { get; set; }
    public int FailedTries { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset PasswordSetAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    /// <summary>
    /// 下次登录必须改密码。新建账号、管理员重置密码之后默认打开——
    /// 初始密码是别人定的，本人没改过之前，审计上「这次操作是谁做的」就站不住。
    /// </summary>
    public bool MustChangePassword { get; set; }

    [JsonIgnore]
    public string RoleName => Role switch
    {
        UserRole.Admin => "管理员",
        UserRole.Supervisor => "主管",
        _ => "操作员"
    };

    [JsonIgnore]
    public string StateName => Disabled ? "已停用" : Locked ? "已锁定" : "正常";

    /// <summary>
    /// 密码还有几天到期。null = 不适用（账号停用了，到期与否没意义）。
    /// 从 PasswordSetAt 算，不单独存——存下来的数会跟改密码的动作脱节。
    /// </summary>
    public int? ExpiresInDays(DateTimeOffset now)
    {
        if (Disabled) return null;
        var used = (now - PasswordSetAt).TotalDays;
        return (int)Math.Ceiling(Math.Max(0, UserStore.PasswordMaxAgeDays - used));
    }
}

/// <summary>
/// 密码规则。**一处定义，三处用**：改密码对话框边打字边勾、
/// 新增用户和重置密码落库前拦一道。规则文案也在这儿，界面不另抄一份。
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    public sealed record Rule(string Text, Func<string, string, bool> Ok);

    /// <summary>参数是（新密码, 旧密码）。「两次一致」由界面自己比，不进这张表。</summary>
    public static readonly IReadOnlyList<Rule> Rules = new[]
    {
        new Rule($"至少 {MinLength} 位", (n, _) => n.Length >= MinLength),
        new Rule("同时包含字母和数字", (n, _) => n.Any(char.IsLetter) && n.Any(char.IsDigit)),
        new Rule("与旧密码不同", (n, o) => n.Length > 0 && n != o)
    };

    /// <summary>不合规就返回第一条没过的规则；合规返回 null。</summary>
    public static string? FirstProblem(string next, string current = "")
        => Rules.FirstOrDefault(r => !r.Ok(next, current))?.Text;

    /// <summary>
    /// 生成一个能念得出来的随机密码（管理员重置时用）。
    /// 去掉 0/O/1/l/I 这些看混的字符——密码要靠嘴念给本人听。
    /// </summary>
    public static string Generate(int length = 10)
    {
        const string cs = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[Math.Max(MinLength, length)];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = cs[RandomNumberGenerator.GetInt32(cs.Length)];
        // 保证同时有字母和数字：末两位一个字母一个数字
        chars[^2] = "abcdefghjkmnpqrstuvwxyz"[RandomNumberGenerator.GetInt32(23)];
        chars[^1] = "23456789"[RandomNumberGenerator.GetInt32(8)];
        return new string(chars);
    }
}

public enum LoginOutcome { Ok, BadCredentials, Locked, Disabled }

/// <summary>
/// TriesLeft &lt; 0 表示「不知道还剩几次」——登录名本身就不存在时不给次数，
/// 免得靠错误信息探出哪些账号是真的。
/// </summary>
public sealed record LoginResult(LoginOutcome Outcome, UserAccount? User, int TriesLeft);

/// <summary>
/// 账号库。一台工作站一份 users.json（跟 tecstudio.db 同目录），
/// 改一次写一次盘——账号这种东西不能停留在内存里等着崩溃丢掉。
///
/// 锁定策略照登录页设计稿：连错 3 次锁账号，自助重置按策略禁用，
/// 解锁 / 重置只有管理员能做。每一件事由调用方写审计（谁做的只有上层知道）。
/// </summary>
public sealed class UserStore
{
    public const int Pbkdf2Iterations = 100_000;
    public const int MaxTries = 3;

    /// <summary>密码用多久算到期。到期不锁账号，界面上标红提示去改。</summary>
    public const int PasswordMaxAgeDays = 90;

    /// <summary>首次开机自动建的管理员账号与初始密码。首次登录界面上明说，不藏。</summary>
    public const string DefaultAdmin = "admin";
    public const string DefaultPassword = "admin";

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private List<UserAccount> _users = new();

    public UserStore(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.Now);
        Load();
    }

    /// <summary>这次 Load 是不是刚播的种（库文件不存在，建了默认管理员）。上层据此提示改密码。</summary>
    public bool SeededDefault { get; private set; }

    /// <summary>记住的登录名。</summary>
    public string RememberedName { get; set; } = "";

    /// <summary>登录页的语言（"zh" / "en"）。这是工作站的偏好，跟账号无关。</summary>
    public string Language { get; set; } = "zh";

    /// <summary>
    /// 记住的密码，加密后存。
    ///
    /// **说清楚这层保护有多厚**：密钥是从本机机器名 + 当前 Windows 账户推出来的，
    /// 所以 users.json 被拷到别的机器上解不开；但在这台机器上、用这个账户跑的程序
    /// 解得开——它挡的是「把文件翻出来直接看见密码」，不是「有心人拿不到」。
    /// 共用工作站上勾这一项，等于承认坐到这台机器前的人都能用这个账号登录。
    /// 不勾就一个字节都不存（勾掉时连旧的一起清）。
    /// </summary>
    public string RememberedSecret { get; set; } = "";

    /// <summary>取出记住的密码；没记、或解不开（换了机器 / 换了账户）都返回空。</summary>
    public string RecallPassword()
    {
        lock (_gate)
        {
            if (RememberedSecret.Length == 0) return "";
            try { return LocalSecret.Unprotect(RememberedSecret); }
            catch { return ""; }
        }
    }

    /// <summary>记住 / 忘掉这一对。password 为空 = 忘掉。</summary>
    public void Remember(string name, string password)
    {
        lock (_gate)
        {
            RememberedName = name;
            RememberedSecret = name.Length > 0 && password.Length > 0
                ? LocalSecret.Protect(password) : "";
            Save();
        }
    }

    public void ForgetRemembered()
    {
        lock (_gate)
        {
            RememberedName = "";
            RememberedSecret = "";
            Save();
        }
    }

    public IReadOnlyList<UserAccount> All
    {
        get { lock (_gate) return _users.Select(Clone).ToList(); }
    }

    public UserAccount? Find(string name)
    {
        lock (_gate) return FindRaw(name) is { } u ? Clone(u) : null;
    }

    private UserAccount? FindRaw(string name)
        => _users.FirstOrDefault(u => string.Equals(u.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    // ── 登录 ─────────────────────────────────────────────────────────

    public LoginResult TryLogin(string name, string password)
    {
        lock (_gate)
        {
            var u = FindRaw(name);
            // 不存在的登录名和密码错走同一句话、不给剩余次数——
            // 错误信息不能变成「探号器」
            if (u is null) return new LoginResult(LoginOutcome.BadCredentials, null, -1);
            if (u.Disabled) return new LoginResult(LoginOutcome.Disabled, Clone(u), 0);
            if (u.Locked) return new LoginResult(LoginOutcome.Locked, Clone(u), 0);

            if (!Verify(u, password))
            {
                u.FailedTries++;
                if (u.FailedTries >= MaxTries) u.Locked = true;
                Save();
                return u.Locked
                    ? new LoginResult(LoginOutcome.Locked, Clone(u), 0)
                    : new LoginResult(LoginOutcome.BadCredentials, Clone(u), MaxTries - u.FailedTries);
            }

            var previous = u.LastLoginAt;
            u.FailedTries = 0;
            u.LastLoginAt = _now();
            Save();
            var ok = Clone(u);
            ok.LastLoginAt = previous;      // 给界面看「上次登录」，这次的时刻它自己知道
            return new LoginResult(LoginOutcome.Ok, ok, MaxTries);
        }
    }

    /// <summary>校验现密码（改密码前问一遍旧的）。</summary>
    public bool Check(string name, string password)
    {
        lock (_gate) return FindRaw(name) is { } u && Verify(u, password);
    }

    // ── 管理 ─────────────────────────────────────────────────────────

    /// <summary>建账号。重名返回 false。</summary>
    public bool Add(string name, string display, UserRole role, string password,
                    bool mustChangePassword = true)
    {
        lock (_gate)
        {
            name = name.Trim();
            if (name.Length == 0 || FindRaw(name) is not null) return false;
            var u = new UserAccount
            {
                Name = name,
                Display = string.IsNullOrWhiteSpace(display) ? name : display.Trim(),
                Role = role,
                CreatedAt = _now(),
                PasswordSetAt = _now(),
                MustChangePassword = mustChangePassword
            };
            SetHash(u, password);
            _users.Add(u);
            Save();
            return true;
        }
    }

    /// <summary>
    /// 重置 / 修改密码。
    ///
    /// unlock 默认真：重置多半就是为了让人重新进来。但管理员可以只换密码
    /// 不解锁——「先弄清楚这个账号为什么连错三次」也是一种处置，
    /// 所以这一条留给调用方决定，不在这儿替它想。
    /// </summary>
    public bool SetPassword(string name, string password, bool mustChangeNext = false,
                            bool unlock = true)
        => Mutate(name, u =>
        {
            SetHash(u, password);
            u.PasswordSetAt = _now();
            u.MustChangePassword = mustChangeNext;
            if (unlock) { u.Locked = false; u.FailedTries = 0; }
        });

    /// <summary>改角色。管理员在用户管理里改，写审计由调用方负责。</summary>
    public bool SetRole(string name, UserRole role) => Mutate(name, u => u.Role = role);

    public bool Unlock(string name)
        => Mutate(name, u => { u.Locked = false; u.FailedTries = 0; });

    /// <summary>
    /// 停用而不是删除：这个名字已经写进历史记录，删掉账号会让老记录
    /// 指向一个不存在的人（GLP 审计连不回去）。
    /// </summary>
    public bool SetDisabled(string name, bool disabled)
        => Mutate(name, u => u.Disabled = disabled);

    private bool Mutate(string name, Action<UserAccount> change)
    {
        lock (_gate)
        {
            if (FindRaw(name) is not { } u) return false;
            change(u);
            Save();
            return true;
        }
    }

    // ── 散列 ─────────────────────────────────────────────────────────

    private static void SetHash(UserAccount u, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        u.Salt = Convert.ToBase64String(salt);
        u.Iterations = Pbkdf2Iterations;
        u.Hash = Convert.ToBase64String(
            Rfc2898DeriveBytes.Pbkdf2(password, salt, u.Iterations, HashAlgorithmName.SHA256, 32));
    }

    private static bool Verify(UserAccount u, string password)
    {
        if (u.Hash.Length == 0 || u.Salt.Length == 0) return false;
        var got = Rfc2898DeriveBytes.Pbkdf2(
            password, Convert.FromBase64String(u.Salt),
            u.Iterations > 0 ? u.Iterations : Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(got, Convert.FromBase64String(u.Hash));
    }

    // ── 存盘 ─────────────────────────────────────────────────────────

    private sealed class Doc
    {
        public int Schema { get; set; } = 1;
        public string RememberedName { get; set; } = "";
        public string RememberedSecret { get; set; } = "";
        public string Language { get; set; } = "zh";
        public List<UserAccount> Users { get; set; } = new();
    }

    private void Load()
    {
        if (File.Exists(_path))
        {
            try
            {
                var doc = TecJson.Read<Doc>(File.ReadAllText(_path));
                _users = doc.Users;
                RememberedName = doc.RememberedName;
                RememberedSecret = doc.RememberedSecret;
                Language = doc.Language.Length > 0 ? doc.Language : "zh";
                return;
            }
            catch
            {
                // 读坏了按空库处理，但把坏文件挪开留证据，不覆盖
                try { File.Move(_path, _path + ".bad-" + _now().ToString("yyyyMMddHHmmss")); }
                catch { /* 挪不动也只能重建 */ }
            }
        }

        // 首次开机：建默认管理员。密码是公开的初始值，登录页上明说、提示尽快改
        var admin = new UserAccount
        {
            Name = DefaultAdmin,
            Display = "管理员",
            Role = UserRole.Admin,
            CreatedAt = _now(),
            PasswordSetAt = _now()
        };
        SetHash(admin, DefaultPassword);
        _users = new List<UserAccount> { admin };
        SeededDefault = true;
        Save();
    }

    /// <summary>写盘（临时文件 + 原子替换，写一半断电不会留下半份账号库）。</summary>
    public void Save()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var text = TecJson.Write(new Doc
            {
                RememberedName = RememberedName,
                RememberedSecret = RememberedSecret,
                Language = Language,
                Users = _users
            });
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private static UserAccount Clone(UserAccount u) => new()
    {
        Name = u.Name, Display = u.Display, Role = u.Role,
        Hash = u.Hash, Salt = u.Salt, Iterations = u.Iterations,
        Disabled = u.Disabled, Locked = u.Locked, FailedTries = u.FailedTries,
        CreatedAt = u.CreatedAt, PasswordSetAt = u.PasswordSetAt, LastLoginAt = u.LastLoginAt,
        MustChangePassword = u.MustChangePassword
    };
}
