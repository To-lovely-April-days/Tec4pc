using System.Security.Cryptography;
using System.Text;

namespace Tec.Core.Users;

/// <summary>
/// 绑本机的小型加解密，只给「记住密码」这一件事用。
///
/// **这是遮挡，不是保险柜。** 密钥从机器名 + 当前系统账户推出来，程序自己也算得出来——
/// 它挡的是「有人把 users.json 翻出来、或者拷到别的机器上，直接看见密码」；
/// 挡不住在这台机器上、用这个账户跑起来的程序。共用工作站上勾「记住密码」，
/// 就等于承认坐到这台机器前的人都能用这个账号登录。调用方（登录页）
/// 把这一点写在界面上，不让操作人以为这里有更强的保护。
///
/// 用 AES-256-CBC + HMAC-SHA256（先加密后认证）：不加认证的话，
/// 文件被改一个字节，解出来的是一段乱码而不是「解不开」，上层就分不清
/// 「没记过」和「被动过」。
/// </summary>
public static class LocalSecret
{
    private const int Iterations = 20_000;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("TecStudio.LocalSecret.v1");

    private static (byte[] Enc, byte[] Mac) Keys()
    {
        var material = $"{Environment.MachineName}{Environment.UserName}";
        var bytes = Rfc2898DeriveBytes.Pbkdf2(material, Salt, Iterations, HashAlgorithmName.SHA256, 64);
        return (bytes[..32], bytes[32..]);
    }

    public static string Protect(string plain)
    {
        var (enc, mac) = Keys();
        using var aes = Aes.Create();
        aes.Key = enc;
        aes.GenerateIV();
        var body = aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), aes.IV);

        // 认证的是 IV + 密文，两段都不许被换
        var signed = new byte[aes.IV.Length + body.Length];
        aes.IV.CopyTo(signed, 0);
        body.CopyTo(signed, aes.IV.Length);
        var tag = HMACSHA256.HashData(mac, signed);

        var all = new byte[signed.Length + tag.Length];
        signed.CopyTo(all, 0);
        tag.CopyTo(all, signed.Length);
        return Convert.ToBase64String(all);
    }

    /// <summary>解不开就抛——换了机器、换了账户、或者文件被动过，都走这一条。</summary>
    public static string Unprotect(string blob)
    {
        var all = Convert.FromBase64String(blob);
        if (all.Length <= 16 + 32) throw new CryptographicException("密文太短。");

        var (enc, mac) = Keys();
        var signed = all[..^32];
        var tag = all[^32..];
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(mac, signed), tag))
            throw new CryptographicException("校验不过：不是这台机器/这个账户记下的，或者文件被改过。");

        using var aes = Aes.Create();
        aes.Key = enc;
        return Encoding.UTF8.GetString(aes.DecryptCbc(signed[16..], signed[..16]));
    }
}
