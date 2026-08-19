using Tec.Core.Users;
using Xunit;

namespace Tec.Core.Tests;

public class UserStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tec-users-" + Guid.NewGuid().ToString("N")[..8]);
    private string PathOf(string f = "users.json") => Path.Combine(_dir, f);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void 首次开机播默认管理员_密码是公开初始值()
    {
        var s = new UserStore(PathOf());
        Assert.True(s.SeededDefault);
        var r = s.TryLogin(UserStore.DefaultAdmin, UserStore.DefaultPassword);
        Assert.Equal(LoginOutcome.Ok, r.Outcome);
        Assert.Equal(UserRole.Admin, r.User!.Role);
        Assert.Null(r.User.LastLoginAt);          // 首次登录，没有「上次」
    }

    [Fact]
    public void 密码不落盘_文件里翻不出明文()
    {
        var s = new UserStore(PathOf());
        s.Add("zhang", "张三", UserRole.Operator, "秘密口令123");
        var text = File.ReadAllText(PathOf());
        Assert.DoesNotContain("秘密口令123", text);
        Assert.DoesNotContain(UserStore.DefaultPassword + "\"", text.Replace("\"admin\"", ""));
    }

    [Fact]
    public void 连错三次锁定_对了也进不来_解锁后恢复()
    {
        var s = new UserStore(PathOf());
        s.Add("zhang", "张三", UserRole.Operator, "correct1");

        Assert.Equal(2, s.TryLogin("zhang", "wrong").TriesLeft);
        Assert.Equal(1, s.TryLogin("zhang", "wrong").TriesLeft);
        Assert.Equal(LoginOutcome.Locked, s.TryLogin("zhang", "wrong").Outcome);
        // 锁上以后密码对了也不放行——锁定是给人处理的，不是给密码试出来的
        Assert.Equal(LoginOutcome.Locked, s.TryLogin("zhang", "correct1").Outcome);

        Assert.True(s.Unlock("zhang"));
        Assert.Equal(LoginOutcome.Ok, s.TryLogin("zhang", "correct1").Outcome);
    }

    [Fact]
    public void 登对一次清空失败计数()
    {
        var s = new UserStore(PathOf());
        s.Add("zhang", "张三", UserRole.Operator, "correct1");
        s.TryLogin("zhang", "wrong");
        s.TryLogin("zhang", "wrong");
        Assert.Equal(LoginOutcome.Ok, s.TryLogin("zhang", "correct1").Outcome);
        // 计数清了：又能错满三次才锁
        Assert.Equal(2, s.TryLogin("zhang", "wrong").TriesLeft);
    }

    [Fact]
    public void 不存在的登录名不给剩余次数()
    {
        var s = new UserStore(PathOf());
        var r = s.TryLogin("nobody", "whatever");
        Assert.Equal(LoginOutcome.BadCredentials, r.Outcome);
        Assert.True(r.TriesLeft < 0);             // 错误信息不能变成探号器
    }

    [Fact]
    public void 停用的账号密码对了也拒绝()
    {
        var s = new UserStore(PathOf());
        s.Add("zhang", "张三", UserRole.Operator, "correct1");
        s.SetDisabled("zhang", true);
        Assert.Equal(LoginOutcome.Disabled, s.TryLogin("zhang", "correct1").Outcome);
        s.SetDisabled("zhang", false);
        Assert.Equal(LoginOutcome.Ok, s.TryLogin("zhang", "correct1").Outcome);
    }

    [Fact]
    public void 重置密码顺带解锁()
    {
        var s = new UserStore(PathOf());
        s.Add("zhang", "张三", UserRole.Operator, "old-pass");
        s.TryLogin("zhang", "x"); s.TryLogin("zhang", "x"); s.TryLogin("zhang", "x");
        Assert.Equal(LoginOutcome.Locked, s.TryLogin("zhang", "old-pass").Outcome);

        Assert.True(s.SetPassword("zhang", "new-pass"));
        Assert.Equal(LoginOutcome.BadCredentials, s.TryLogin("zhang", "old-pass").Outcome);
        Assert.Equal(LoginOutcome.Ok, s.TryLogin("zhang", "new-pass").Outcome);
    }

    [Fact]
    public void 重名不让建_忽略大小写()
    {
        var s = new UserStore(PathOf());
        Assert.True(s.Add("zhang", "张三", UserRole.Operator, "p1"));
        Assert.False(s.Add("Zhang", "李四", UserRole.Operator, "p2"));
    }

    [Fact]
    public void 重开程序状态原样回来()
    {
        var s1 = new UserStore(PathOf());
        s1.Add("zhang", "张三", UserRole.Operator, "correct1");
        s1.TryLogin("zhang", "wrong");
        s1.RememberedName = "zhang";
        s1.Save();

        var s2 = new UserStore(PathOf());
        Assert.False(s2.SeededDefault);           // 不是首次，不重播种
        Assert.Equal("zhang", s2.RememberedName);
        Assert.Equal("张三", s2.Find("zhang")!.Display);
        // 失败计数也带回来：重启不能把锁定进度洗掉
        Assert.Equal(1, s2.TryLogin("zhang", "wrong").TriesLeft);
    }

    [Fact]
    public void 上次登录给的是前一次的时刻()
    {
        var t = new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.FromHours(8));
        var s = new UserStore(PathOf(), () => t);
        s.Add("zhang", "张三", UserRole.Operator, "correct1");

        Assert.Null(s.TryLogin("zhang", "correct1").User!.LastLoginAt);   // 第一次：无
        t = t.AddHours(3);
        var second = s.TryLogin("zhang", "correct1");
        Assert.Equal(9, second.User!.LastLoginAt!.Value.Hour);            // 第二次：给 9 点那次
    }

    [Fact]
    public void 记住密码_下次开程序取得回来()
    {
        var s1 = new UserStore(PathOf());
        s1.Add("zhang", "张三", UserRole.Operator, "correct1");
        s1.Remember("zhang", "correct1");

        var s2 = new UserStore(PathOf());
        Assert.Equal("zhang", s2.RememberedName);
        Assert.Equal("correct1", s2.RecallPassword());
        // 取回来的那一份真能登进去
        Assert.Equal(LoginOutcome.Ok, s2.TryLogin(s2.RememberedName, s2.RecallPassword()).Outcome);
    }

    [Fact]
    public void 记住的密码不是明文躺在文件里()
    {
        var s = new UserStore(PathOf());
        s.Add("zhang", "张三", UserRole.Operator, "秘密口令123");
        s.Remember("zhang", "秘密口令123");
        Assert.DoesNotContain("秘密口令123", File.ReadAllText(PathOf()));
    }

    [Fact]
    public void 取消记住会把旧的一起清掉()
    {
        var s1 = new UserStore(PathOf());
        s1.Add("zhang", "张三", UserRole.Operator, "correct1");
        s1.Remember("zhang", "correct1");
        s1.ForgetRemembered();

        var s2 = new UserStore(PathOf());
        Assert.Equal("", s2.RememberedName);
        Assert.Equal("", s2.RecallPassword());
    }

    [Fact]
    public void 密文被改过就当没记过_不返回乱码()
    {
        var s = new UserStore(PathOf());
        s.Add("zhang", "张三", UserRole.Operator, "correct1");
        s.Remember("zhang", "correct1");

        var blob = Convert.FromBase64String(s.RememberedSecret);
        blob[^1] ^= 0xFF;                       // 动一个字节
        s.RememberedSecret = Convert.ToBase64String(blob);
        Assert.Equal("", s.RecallPassword());   // 认证不过 = 没记过，不是一段乱码
    }

    [Fact]
    public void 语言选择跟着存盘()
    {
        var s1 = new UserStore(PathOf());
        s1.Language = "en";
        s1.Save();
        Assert.Equal("en", new UserStore(PathOf()).Language);
    }
}
