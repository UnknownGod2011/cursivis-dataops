using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Cursivis.Contracts;

namespace Cursivis.Infrastructure.BrowserBridge;

public static class BrowserPipeNames
{
    public static string ForCurrentUser()
    {
        string identity = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user identity is unavailable.");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"cursivis.browser.v{ProtocolVersions.BrowserBridge}.{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }
}
