using System.Security.Cryptography;
using System.Text;

namespace SqlDesk.Core.Connections;

public interface IPasswordProtector
{
    string Protect(string plain);

    string Unprotect(string protectedValue);
}

/// <summary>DPAPI com escopo do usuário atual. Saída em Base64.</summary>
public sealed class DpapiPasswordProtector(string purpose = "SqlDesk.connections.v1") : IPasswordProtector
{
    private readonly byte[] Entropy = Encoding.UTF8.GetBytes(purpose);

    public string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedValue) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
}
