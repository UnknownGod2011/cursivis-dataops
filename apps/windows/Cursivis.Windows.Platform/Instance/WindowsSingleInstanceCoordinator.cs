using System.Security.AccessControl;
using System.Security.Principal;

namespace Cursivis.Windows.Platform.Instance;

public interface ISingleInstanceLease : IDisposable
{
    bool IsPrimaryInstance { get; }

    UserScopedInstanceNames Names { get; }
}

public interface ISingleInstanceCoordinator
{
    ISingleInstanceLease Acquire();
}

public sealed class WindowsSingleInstanceCoordinator : ISingleInstanceCoordinator
{
    private readonly UserScopedInstanceNames _names;

    public WindowsSingleInstanceCoordinator(string applicationId)
        : this(UserScopedInstanceNames.ForCurrentUser(applicationId))
    {
    }

    public WindowsSingleInstanceCoordinator(UserScopedInstanceNames names)
    {
        _names = names ?? throw new ArgumentNullException(nameof(names));
    }

    public ISingleInstanceLease Acquire()
    {
        EnsureWindows();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var userSid = identity.User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");

        var security = new MutexSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(userSid);
        security.AddAccessRule(new MutexAccessRule(
            userSid,
            MutexRights.FullControl,
            AccessControlType.Allow));

        var mutex = MutexAcl.Create(
            initiallyOwned: false,
            _names.MutexName,
            out var createdNew,
            security);

        return new SingleInstanceLease(mutex, createdNew, _names);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Single-instance coordination requires Windows.");
        }
    }

    private sealed class SingleInstanceLease(
        Mutex mutex,
        bool isPrimaryInstance,
        UserScopedInstanceNames names) : ISingleInstanceLease
    {
        private Mutex? _mutex = mutex;

        public bool IsPrimaryInstance { get; } = isPrimaryInstance;

        public UserScopedInstanceNames Names { get; } = names;

        public void Dispose()
        {
            Interlocked.Exchange(ref _mutex, null)?.Dispose();
        }
    }
}
