using Tec.Core.Users;

namespace Tec.Demo;

/// <summary>
/// 【本地改动】演示账号：管理员 / 主管 / 操作员各一个，登录页预填操作员。
///
/// WHY：登录页截图不该停在「首次启动，默认管理员 admin / admin」那句提醒上；用户管理页也要有几行账号可看。
/// 密码只活在演示数据目录里（每次启动重建），不是任何真实账号。
/// </summary>
public static class DemoUsers
{
    public const string OperatorName = "lig", OperatorPassword = "Tec@2026demo";

    public static void Seed(UserStore users)
    {
        // 缺省管理员是 UserStore 自己播的（admin），这里补两个，并把它们的「首次须改密码」关掉——演示里没人去改
        users.Add("wangg", "王工", UserRole.Supervisor, "Tec@2026demo", mustChangePassword: false);
        users.Add(OperatorName, "李工", UserRole.Operator, OperatorPassword, mustChangePassword: false);
        users.SetPassword(UserStore.DefaultAdmin, UserStore.DefaultPassword, mustChangeNext: false);
        users.Remember(OperatorName, OperatorPassword);
        users.Save();
    }
}
