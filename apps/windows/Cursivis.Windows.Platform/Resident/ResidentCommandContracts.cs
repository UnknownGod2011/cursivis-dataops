namespace Cursivis.Windows.Platform.Resident;

public enum ResidentCommand
{
    ContextTrigger,
    RealtimeLiveMode,
    DirectTakeAction,
    SmartDictation,
    CustomQuickTask,
    Cancel,
    OpenSettings,
    Exit,
}

public enum ResidentCommandSourceKind
{
    GlobalHotkey,
    NotificationArea,
    SecondaryInstance,
}

public sealed record ResidentCommandInvocation(
    ResidentCommand Command,
    ResidentCommandSourceKind Source,
    DateTimeOffset InvokedAtUtc,
    string CorrelationId);

public interface IResidentCommandDispatcher
{
    ValueTask DispatchAsync(
        ResidentCommandInvocation invocation,
        CancellationToken cancellationToken = default);
}

public interface IResidentCommandSource : IAsyncDisposable
{
    Task RunAsync(
        IResidentCommandDispatcher dispatcher,
        CancellationToken cancellationToken);
}

public interface IGlobalHotkeyCommandSource : IResidentCommandSource;

public interface INotificationAreaCommandSource : IResidentCommandSource;
