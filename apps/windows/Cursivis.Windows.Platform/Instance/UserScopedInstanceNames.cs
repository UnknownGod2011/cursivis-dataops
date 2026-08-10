using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Cursivis.Windows.Platform.Instance;

public sealed record UserScopedInstanceNames(string MutexName, string ActivationPipeName)
{
    public static UserScopedInstanceNames ForCurrentUser(string applicationId)
    {
        EnsureWindows();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var sessionId = Process.GetCurrentProcess().SessionId;
        return Create(applicationId, userSid, sessionId);
    }

    public static UserScopedInstanceNames Create(
        string applicationId,
        string userSid,
        int sessionId)
    {
        ValidateApplicationId(applicationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (sessionId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionId));
        }

        var applicationToken = HashToken(applicationId);
        var userToken = HashToken(userSid);
        return new UserScopedInstanceNames(
            $"Local\\Cursivis.{applicationToken}.{userToken}.s{sessionId}.Singleton",
            $"cursivis-{applicationToken}-{userToken}-s{sessionId}-activation");
    }

    private static string HashToken(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            var digest = SHA256.HashData(bytes);
            return Convert.ToHexString(digest.AsSpan(0, 12)).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateApplicationId(string applicationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > 96 || applicationId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("The application identifier is invalid.", nameof(applicationId));
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("User-scoped instance names require Windows.");
        }
    }
}
