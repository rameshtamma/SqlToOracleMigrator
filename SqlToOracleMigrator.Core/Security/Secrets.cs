using System.Security.Cryptography;
using System.Text;

namespace SqlToOracleMigrator.Core;

public interface ISecretProtector
{
    string ProtectToBase64(string plainText);
    string UnprotectFromBase64(string protectedBase64);
}

public sealed class DpapiSecretProtector : ISecretProtector
{
    public string ProtectToBase64(string plainText)
    {
        if (plainText is null) throw new ArgumentNullException(nameof(plainText));
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string UnprotectFromBase64(string protectedBase64)
    {
        if (protectedBase64 is null) throw new ArgumentNullException(nameof(protectedBase64));
        var protectedBytes = Convert.FromBase64String(protectedBase64);
        var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
