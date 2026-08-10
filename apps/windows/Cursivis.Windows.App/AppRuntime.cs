using Cursivis.Application.Context;
using Cursivis.Application.Actions;
using Cursivis.Application.Dictation;
using Cursivis.Application.OpenAI;
using Cursivis.Application.Presentation;
using Cursivis.Application.QuickTasks;
using Cursivis.Application.Realtime;
using Cursivis.Domain.Actions;
using Cursivis.Domain.Context;
using Cursivis.Domain.Interaction;
using Cursivis.Domain.Models;
using Cursivis.Domain.QuickTasks;
using Cursivis.Domain.Settings;
using Cursivis.Contracts.Browser;
using Cursivis.Infrastructure.OpenAI;
using Cursivis.Infrastructure.BrowserBridge;
using Cursivis.Infrastructure.Storage.Persistence;
using Cursivis.Infrastructure.Storage.Settings;
using Cursivis.Windows.Platform.ClipboardServices;
using Cursivis.Windows.Platform.Audio;
using Cursivis.Windows.Platform.Capture;
using Cursivis.Windows.Platform.Foreground;
using Cursivis.Windows.Platform.Hotkeys;
using Cursivis.Windows.Platform.Overlays;
using Cursivis.Windows.Platform.Security;
using Cursivis.Windows.Platform.Selection;
using Cursivis.Windows.Platform.Startup;
using Cursivis.Windows.App.Helpers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using DomainApplicationTheme = Cursivis.Domain.Settings.ApplicationTheme;
using PlatformHotkeyChord = Cursivis.Windows.Platform.Hotkeys.HotkeyChord;
using PlatformHotkeyModifiers = Cursivis.Windows.Platform.Hotkeys.HotkeyModifiers;

namespace Cursivis.Windows.App;

public sealed class AppRuntime : IAsyncDisposable
{
    public const string ContextTriggerCommand = "context-trigger";
    public const string QuickTaskCommand = "custom-quick-task";
    public const string DirectTakeActionCommand = "direct-take-action";
    public const string SmartDictationCommand = "smart-dictation";
    public const string RealtimeLiveModeCommand = "realtime-live-mode";
    public const string CancelCommand = "cancel-emergency-stop";
    public const string OpenSettingsCommand = "open-settings";

    private readonly Win32WindowMessageHook _messageHook;
    private readonly TransactionalHotkeyRegistrar _hotkeys;
    private readonly ScopedUnmodifiedHotkeyRegistration _escapeDismissHotkey;
    private readonly ContextOrbWindow _orbWindow;
    private readonly ContextResultWindow _resultWindow;
    private readonly BrowserBridgeService _browserBridge;
    private readonly TakeActionController _takeActionController;
    private readonly LiveModeOverlayWindow _liveModeWindow;
    private readonly IRealtimeLiveModeService _liveMode;
    private readonly ISmartDictationService _smartDictation;
    private readonly ILiveModeMemoryStore _liveModeMemory;
    private readonly NativePointerMonitor _pointerMonitor;
    private readonly VersionedJsonSettingsStore<QuickTaskDefinition> _quickTaskStore;
    private readonly VersionedJsonSettingsStore<ApplicationSettings> _settingsStore;
    private readonly WindowsStartupRegistration _startupRegistration;
    private readonly NavigationGuidanceConfiguration _navigationGuidanceConfiguration;
    private readonly WindowsNavigationGuidanceInteraction _navigationGuidanceInteraction;
    private readonly IQuickTaskFinalizationService _quickTaskFinalizer;
    private readonly IReadOnlyDictionary<string, HotkeyStartupFallback> _startupHotkeyFallbacks;
    private readonly Dictionary<string, PlatformHotkeyChord> _configuredHotkeys;
    private readonly object _quickTaskGate = new();
    private readonly object _settingsGate = new();
    private readonly SemaphoreSlim _settingsWriteGate = new(1, 1);
    private CancellationTokenSource? _liveCompletionVisibilityCancellation;
    private CancellationTokenSource? _dictationCompletionVisibilityCancellation;
    private QuickTaskDefinition _currentQuickTask;
    private ApplicationSettings _currentSettings;
    private bool _disposed;

    private AppRuntime(
        WindowsOpenAiCredentialManager credentialManager,
        IResponsesGateway responsesGateway,
        Win32WindowMessageHook messageHook,
        TransactionalHotkeyRegistrar hotkeys,
        ScopedUnmodifiedHotkeyRegistration escapeDismissHotkey,
        ContextTriggerController contextTrigger,
        ContextOrbWindow orbWindow,
        ContextResultWindow resultWindow,
        BrowserBridgeService browserBridge,
        TakeActionController takeActionController,
        LiveModeOverlayWindow liveModeWindow,
        IRealtimeLiveModeService liveMode,
        ISmartDictationService smartDictation,
        ILiveModeMemoryStore liveModeMemory,
        NativePointerMonitor pointerMonitor,
        VersionedJsonSettingsStore<QuickTaskDefinition> quickTaskStore,
        VersionedJsonSettingsStore<ApplicationSettings> settingsStore,
        WindowsStartupRegistration startupRegistration,
        NavigationGuidanceConfiguration navigationGuidanceConfiguration,
        WindowsNavigationGuidanceInteraction navigationGuidanceInteraction,
        IQuickTaskFinalizationService quickTaskFinalizer,
        QuickTaskDefinition currentQuickTask,
        ApplicationSettings currentSettings,
        SettingsLoadStatus settingsLoadStatus,
        IReadOnlyDictionary<string, PlatformHotkeyChord> configuredHotkeys,
        HotkeyUpdateResult contextHotkeyStatus,
        HotkeyUpdateResult quickTaskHotkeyStatus,
        HotkeyUpdateResult directTakeActionHotkeyStatus,
        HotkeyUpdateResult smartDictationHotkeyStatus,
        HotkeyUpdateResult liveModeHotkeyStatus,
        HotkeyUpdateResult cancelHotkeyStatus,
        HotkeyUpdateResult settingsHotkeyStatus,
        IReadOnlyDictionary<string, HotkeyStartupFallback> startupHotkeyFallbacks)
    {
        CredentialManager = credentialManager;
        ResponsesGateway = responsesGateway;
        _messageHook = messageHook;
        _hotkeys = hotkeys;
        _escapeDismissHotkey = escapeDismissHotkey;
        ContextTrigger = contextTrigger;
        _orbWindow = orbWindow;
        _resultWindow = resultWindow;
        _browserBridge = browserBridge;
        _takeActionController = takeActionController;
        _liveModeWindow = liveModeWindow;
        _liveMode = liveMode;
        _smartDictation = smartDictation;
        _liveModeMemory = liveModeMemory;
        _pointerMonitor = pointerMonitor;
        _quickTaskStore = quickTaskStore;
        _settingsStore = settingsStore;
        _startupRegistration = startupRegistration;
        _navigationGuidanceConfiguration = navigationGuidanceConfiguration;
        _navigationGuidanceInteraction = navigationGuidanceInteraction;
        _quickTaskFinalizer = quickTaskFinalizer;
        _currentQuickTask = currentQuickTask;
        _currentSettings = currentSettings;
        ApplicationSettingsLoadStatus = settingsLoadStatus;
        _configuredHotkeys = new Dictionary<string, PlatformHotkeyChord>(
            configuredHotkeys,
            StringComparer.Ordinal);
        ContextHotkeyStatus = contextHotkeyStatus;
        QuickTaskHotkeyStatus = quickTaskHotkeyStatus;
        DirectTakeActionHotkeyStatus = directTakeActionHotkeyStatus;
        SmartDictationHotkeyStatus = smartDictationHotkeyStatus;
        LiveModeHotkeyStatus = liveModeHotkeyStatus;
        CancelHotkeyStatus = cancelHotkeyStatus;
        SettingsHotkeyStatus = settingsHotkeyStatus;
        _startupHotkeyFallbacks = startupHotkeyFallbacks;
        _messageHook.HotkeyPressed += OnHotkeyPressed;
        _resultWindow.SettingsRequested += OnSettingsRequested;
        _orbWindow.SettingsRequested += OnSettingsRequested;
        _orbWindow.MicrophoneRequested += OnLiveModeRequested;
        _orbWindow.CancelRequested += OnOrbCancelRequested;
        _orbWindow.LiveTranscriptVisibilityRequested += OnLiveTranscriptVisibilityRequested;
        _liveModeWindow.StopRequested += OnLiveModeRequested;
        _liveMode.SnapshotChanged += OnLiveModeSnapshotChanged;
        _smartDictation.SnapshotChanged += OnSmartDictationSnapshotChanged;
        _resultWindow.ThemeRequested += OnThemeRequested;
        _resultWindow.OverlayVisibilityChanged += OnResultVisibilityChanged;
        _pointerMonitor.PointerPressed += OnPointerPressed;
    }

    public WindowsOpenAiCredentialManager CredentialManager { get; }

    public IResponsesGateway ResponsesGateway { get; }

    public ContextTriggerController ContextTrigger { get; }

    public HotkeyUpdateResult ContextHotkeyStatus { get; }

    public HotkeyUpdateResult QuickTaskHotkeyStatus { get; }

    public HotkeyUpdateResult DirectTakeActionHotkeyStatus { get; }

    public HotkeyUpdateResult SmartDictationHotkeyStatus { get; }

    public HotkeyUpdateResult LiveModeHotkeyStatus { get; }

    public HotkeyUpdateResult CancelHotkeyStatus { get; }

    public HotkeyUpdateResult SettingsHotkeyStatus { get; }

    /// <summary>
    /// Contains the one-time, visible fallback selected only when an untouched
    /// default shortcut was already registered by another Windows application.
    /// Custom user shortcuts are never silently changed.
    /// </summary>
    public IReadOnlyDictionary<string, HotkeyStartupFallback> StartupHotkeyFallbacks =>
        _startupHotkeyFallbacks;

    public BrowserBridgeSnapshot BrowserBridgeStatus => _browserBridge.Snapshot;

    public ILiveModeMemoryStore LiveModeMemory => _liveModeMemory;

    public SettingsLoadStatus ApplicationSettingsLoadStatus { get; }

    public ApplicationSettings CurrentSettings
    {
        get
        {
            lock (_settingsGate)
            {
                return _currentSettings;
            }
        }
    }

    public bool IsLaunchAtSignInEnabled => _startupRegistration.IsEnabled;

    public string LogsDirectory => CursivisStoragePaths.ForCurrentUser().LogsDirectory;

    public IReadOnlyList<WindowsMicrophoneEndpoint> MicrophoneEndpoints =>
        WindowsAudioDeviceCatalog.GetCaptureDevices();

    public Task<BrowserFormSnapshot> TestBrowserIntegrationAsync(
        CancellationToken cancellationToken = default) =>
        _browserBridge.DiscoverFormAsync(cancellationToken);

    public QuickTaskDefinition CurrentQuickTask
    {
        get
        {
            lock (_quickTaskGate)
            {
                return _currentQuickTask;
            }
        }
    }

    public string QuickTaskActiveChord =>
        GetActiveHotkeyChord(QuickTaskCommand);

    public string GetActiveHotkeyChord(string commandName) =>
        _hotkeys.TryGetActive(commandName, out ActiveHotkeyRegistration? registration)
            ? registration!.Chord.ToString()
            : string.Empty;

    public string GetConfiguredHotkeyChord(string commandName)
    {
        lock (_settingsGate)
        {
            return _configuredHotkeys.TryGetValue(commandName, out PlatformHotkeyChord chord)
                ? chord.ToString()
                : string.Empty;
        }
    }

    public async Task<HotkeyUpdateResult> UpdateHotkeyAsync(
        string commandName,
        string canonicalChord,
        CancellationToken cancellationToken = default)
    {
        if (commandName is not (
            ContextTriggerCommand or
            QuickTaskCommand or
            DirectTakeActionCommand or
            SmartDictationCommand or
            RealtimeLiveModeCommand or
            CancelCommand or
            OpenSettingsCommand))
        {
            throw new ArgumentOutOfRangeException(nameof(commandName));
        }

        if (!HotkeyChordParser.TryParse(canonicalChord, out Cursivis.Windows.Platform.Hotkeys.HotkeyChord chord))
        {
            throw new ArgumentException("The shortcut is invalid or reserved.", nameof(canonicalChord));
        }

        HotkeyUpdateResult result = await _hotkeys.UpdateAsync(commandName, chord, cancellationToken);
        if (result.Status is not (HotkeyUpdateStatus.Success or HotkeyUpdateStatus.AlreadyActive))
        {
            return result;
        }

        lock (_settingsGate)
        {
            // TransactionalHotkeyRegistrar persists this chord to the dedicated
            // hotkeys.json store before it swaps the native registration. Keep
            // the UI projection in sync only after that atomic operation wins.
            _configuredHotkeys[commandName] = chord;
        }

        return result;
    }

    public async Task<ApplicationSettings> UpdateApplicationSettingsAsync(
        Func<ApplicationSettings, ApplicationSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _settingsWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ApplicationSettings next;
            lock (_settingsGate)
            {
                next = update(_currentSettings);
            }

            if (!SettingsValidator.Validate(next).IsValid)
            {
                throw new ArgumentException("The requested application settings are invalid.", nameof(update));
            }

            await _settingsStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
            lock (_settingsGate)
            {
                _currentSettings = next;
            }
            ContextTrigger.SetDefaultMode(next.Interaction.DefaultMode);
            ContextTrigger.SetCloseResultsAfterInsert(next.Interaction.CloseResultsAfterInsert);
            _navigationGuidanceConfiguration.Update(
                next.Privacy.AllowNavigationGuidanceCapture,
                next.Interaction.CaptureScope);

            return next;
        }
        finally
        {
            _settingsWriteGate.Release();
        }
    }

    public async Task<ApplicationSettings> SaveInteractionPreferencesAsync(
        InteractionSettings interaction,
        OrbVisibility orbVisibility,
        bool launchAtSignIn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        bool previousStartup = _startupRegistration.IsEnabled;
        if (previousStartup != launchAtSignIn)
        {
            _startupRegistration.SetEnabled(launchAtSignIn);
        }

        try
        {
            return await UpdateApplicationSettingsAsync(
                settings => settings with
                {
                    Interaction = interaction,
                    Appearance = settings.Appearance with { OrbVisibility = orbVisibility },
                    Startup = settings.Startup with { LaunchAtSignIn = launchAtSignIn },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (previousStartup != launchAtSignIn)
            {
                _startupRegistration.SetEnabled(previousStartup);
            }
            throw;
        }
    }

    public void ResetOrbPlacement() => _orbWindow.ResetRememberedPlacement();

    public async Task<float> TestMicrophoneAsync(
        string? endpointId,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        if (duration < TimeSpan.FromMilliseconds(250) || duration > TimeSpan.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        await _liveMode.StopAsync(cancellationToken).ConfigureAwait(false);
        await _smartDictation.CancelAsync(cancellationToken).ConfigureAwait(false);
        await using IDictationAudioCapture capture = await new WindowsDictationAudioCaptureFactory(
            WindowsAudioDeviceCatalog.ParseDeviceNumber(endpointId)).OpenAsync(cancellationToken);
        using var durationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        durationCancellation.CancelAfter(duration);
        float maximum = 0;
        try
        {
            await foreach (DictationAudioFrame frame in capture
                               .ReadCapturedAudioAsync(durationCancellation.Token)
                               .ConfigureAwait(false))
            {
                maximum = Math.Max(maximum, frame.Level);
            }
        }
        catch (OperationCanceledException) when (
            durationCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return maximum;
    }

    public async Task<LiveModeSnapshot> TestRealtimeConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        await _smartDictation.CancelAsync(cancellationToken).ConfigureAwait(false);
        await _liveMode.StopAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource<LiveModeSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(LiveModeSnapshot snapshot)
        {
            if (snapshot.State is LiveModeState.Listening or LiveModeState.UserSpeaking or LiveModeState.Error)
            {
                completion.TrySetResult(snapshot);
            }
        }

        _liveMode.SnapshotChanged += Observe;
        try
        {
            await _liveMode.StartAsync(cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _liveMode.SnapshotChanged -= Observe;
            await _liveMode.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task<HotkeyUpdateResult> UpdateQuickTaskHotkeyAsync(
        string canonicalChord,
        CancellationToken cancellationToken = default)
    {
        return await UpdateHotkeyAsync(QuickTaskCommand, canonicalChord, cancellationToken);
    }

    public Task<QuickTaskFinalizationResult> FinalizeQuickTaskAsync(
        string displayName,
        string roughDescription,
        CancellationToken cancellationToken = default) =>
        _quickTaskFinalizer.FinalizeAsync(
            displayName,
            roughDescription,
            ModelCatalog.Economy,
            cancellationToken);

    public async Task SaveQuickTaskAsync(
        QuickTaskDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsExplicitlyApproved)
        {
            throw new ArgumentException("The finalized Quick Task must be approved before saving.", nameof(definition));
        }

        if (QuickTaskSafetyValidator.Validate(definition.FinalizedInstruction).Count > 0)
        {
            throw new ArgumentException("The finalized Quick Task conflicts with centralized safety policy.", nameof(definition));
        }

        await _quickTaskStore.SaveAsync(definition, cancellationToken);
        lock (_quickTaskGate)
        {
            _currentQuickTask = definition;
        }
    }

    public Task<ContextTriggerExecutionResult> TestQuickTaskAsync(
        QuickTaskDefinition definition,
        string fixtureText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureText);
        ContextSnapshot context = ContextSnapshot.FromText(
            ContextKind.Text,
            ContextSource.DirectInput,
            new TargetIdentity("cursivis-settings", "quick-task-fixture"),
            fixtureText.Trim(),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        return ContextTrigger.ExecuteQuickTaskFixtureAsync(
            ContextExecutionInput.FromText(context),
            definition,
            cancellationToken);
    }

    public static async Task<AppRuntime> CreateAsync(nint mainWindowHandle)
    {
        DispatcherQueue mainWindowDispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("The Cursivis Settings dispatcher is unavailable.");
        CursivisStoragePaths paths = CursivisStoragePaths.ForCurrentUser();
        // WinUI does not install a SynchronizationContext for every async
        // continuation. Construct compositor-backed windows before the first
        // asynchronous storage operation, while this method is still on the
        // launch DispatcherQueue thread.
        var orbWindow = new ContextOrbWindow();
        var resultWindow = new ContextResultWindow();
        var liveModeWindow = new LiveModeOverlayWindow();
        _ = await CursivisStoragePaths.TryMigrateLegacyCurrentUserDataAsync(paths);
        var pointerMonitor = new NativePointerMonitor();
        var settingsStore = new VersionedJsonSettingsStore<ApplicationSettings>(
            new VersionedJsonSettingsStoreOptions(
                paths.SettingsFile,
                ApplicationSettings.CurrentSchemaVersion,
                maximumFileBytes: 512 * 1024),
            new ApplicationSettingsJsonCodec());
        SettingsLoadResult<ApplicationSettings> settingsLoad = await settingsStore.LoadAsync();
        var startupRegistration = new WindowsStartupRegistration();
        ApplicationSettings currentSettings = settingsLoad.Value;
        bool actualStartup = startupRegistration.IsEnabled;
        if (currentSettings.Startup.LaunchAtSignIn != actualStartup)
        {
            currentSettings = currentSettings with
            {
                Startup = currentSettings.Startup with { LaunchAtSignIn = actualStartup },
            };
        }
        if (settingsLoad.Status == SettingsLoadStatus.FirstRun || currentSettings != settingsLoad.Value)
        {
            await settingsStore.SaveAsync(currentSettings);
        }
        var secretStore = new WindowsCurrentUserSecretStore(
            new WindowsCurrentUserSecretStoreOptions(
                paths.SecretsDirectory,
                "Cursivis.OpenAI.ApiKey.v1"));
        var credentialManager = new WindowsOpenAiCredentialManager(secretStore);
        var credentialSource = new WindowsOpenAiCredentialSource(secretStore);
        // The selected-text workflow is intentionally Gemini + DataHub only.
        // OpenAI-backed voice capabilities remain optional and are isolated below.
        IResponsesGateway responsesGateway = new DataHubGeminiResponsesGateway();
        ITranscriptionGateway transcriptionGateway = new OpenAiTranscriptionGateway(credentialSource);
        IRealtimeGateway realtimeGateway = new OpenAiRealtimeGateway(credentialSource);
        IContextTriggerService contextService = new ContextTriggerService(responsesGateway);
        IQuickTaskFinalizationService quickTaskFinalizer = new QuickTaskFinalizationService(responsesGateway);
        var quickTaskStore = new VersionedJsonSettingsStore<QuickTaskDefinition>(
            new VersionedJsonSettingsStoreOptions(
                paths.QuickTaskFile,
                QuickTaskDefinition.CurrentSchemaVersion,
                maximumFileBytes: 128 * 1024),
            new QuickTaskJsonSettingsCodec(
                static definition => definition is not null &&
                                     definition.IsExplicitlyApproved &&
                                     QuickTaskSafetyValidator.Validate(definition.FinalizedInstruction).Count == 0));
        SettingsLoadResult<QuickTaskDefinition> quickTaskLoad = await quickTaskStore.LoadAsync();
        if (quickTaskLoad.Status == SettingsLoadStatus.FirstRun)
        {
            await quickTaskStore.SaveAsync(quickTaskLoad.Value);
        }

        var foreground = new Win32ForegroundWindowIdentityProvider();
        ITextSelectionReader[] readers =
        [
            new UiAutomationTextSelectionReader(),
            new WindowsProtectedClipboardSelectionReader(),
        ];
        ISelectionCaptureService capture = new LayeredSelectionCaptureService(foreground, readers);
        var screenCapture = new WindowsScreenCaptureService();
        IRegionContextCaptureService regionCapture = new RegionContextCaptureService(
            foreground,
            screenCapture);
        ISelectionReplacementService replacement = new WindowsSelectionReplacementService(foreground, readers);
        ITextInsertionService insertion = new WindowsTextInsertionService(foreground);
        ITextUndoService undo = new WindowsTextUndoService(foreground);
        IResultClipboardService clipboard = new WindowsResultClipboardService();
        var inputSettler = new WindowsHotkeyInputSettler();

        var foregroundActivator = new Win32ForegroundWindowActivator();
        var controller = new ContextTriggerController(
            capture,
            regionCapture,
            contextService,
            replacement,
            insertion,
            undo,
            clipboard,
            foregroundActivator,
            foreground,
            inputSettler,
            orbWindow,
            resultWindow,
            currentSettings.Models.ResponsesModel,
            currentSettings.Interaction.DefaultMode,
            currentSettings.Interaction.CloseResultsAfterInsert);
        var browserBridge = new BrowserBridgeService(
            new HashSet<string>(StringComparer.Ordinal)
            {
                BrowserExtensionIdentity.StableExtensionId,
            });
        await browserBridge.StartAsync();
        var takeActionService = new BrowserTakeActionService(
            responsesGateway,
            browserBridge);
        var takeActionController = new TakeActionController(
            browserBridge,
            takeActionService,
            capture,
            inputSettler,
            controller,
            orbWindow,
            resultWindow,
            currentSettings.Models.ResponsesModel);
        var liveModeMemory = new LiveModeMemoryStore(paths.MemoryFile);
        var navigationGuidanceInteraction = new WindowsNavigationGuidanceInteraction(orbWindow);
        var navigationGuidanceConfiguration = new NavigationGuidanceConfiguration(
            currentSettings.Privacy.AllowNavigationGuidanceCapture,
            currentSettings.Interaction.CaptureScope);
        var navigationGuidance = new NavigationGuidanceService(
            responsesGateway,
            new WindowsNavigationGuidanceCaptureService(foreground, screenCapture),
            navigationGuidanceInteraction,
            currentSettings.Models.ResponsesModel);
        // Constructed early so Live Mode selection/region tools can marshal to
        // the WinUI STA even when Realtime callbacks continue on workers.
        var uiDispatcher = new DispatcherQueueWindowThreadDispatcher(mainWindowDispatcher);
        var liveModeContextProvider = new LiveModeContextProvider(
            inputSettler,
            capture,
            foreground,
            uiDispatcher);
        var liveModeCapabilities = new WindowsLiveModeCapabilityExecutor(
            clipboard,
            insertion,
            regionCapture,
            contextService,
            takeActionController,
            navigationGuidance,
            navigationGuidanceConfiguration,
            currentSettings.Models.ResponsesModel,
            uiDispatcher);
        var liveMode = new RealtimeLiveModeService(
            realtimeGateway,
            new WindowsRealtimeAudioSessionFactory(
                WindowsAudioDeviceCatalog.ParseDeviceNumber(currentSettings.Voice.DeviceEndpointId)),
            liveModeContextProvider,
            new LiveModeToolExecutor(liveModeMemory, liveModeCapabilities, liveModeContextProvider),
            currentSettings.Models.RealtimeModel.Value);
        var smartDictation = new SmartDictationService(
            new WindowsDictationAudioCaptureFactory(
                WindowsAudioDeviceCatalog.ParseDeviceNumber(currentSettings.Voice.DeviceEndpointId)),
            new WindowsDictationTargetProvider(foreground),
            transcriptionGateway,
            responsesGateway,
            insertion,
            clipboard,
            ModelCatalog.EconomyTranscription,
            currentSettings.Models.ResponsesModel);

        ElementTheme requestedTheme = currentSettings.Appearance.Theme switch
        {
            DomainApplicationTheme.Light => ElementTheme.Light,
            DomainApplicationTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        resultWindow.SetRequestedTheme(requestedTheme);
        orbWindow.SetRequestedTheme(requestedTheme);
        liveModeWindow.SetRequestedTheme(requestedTheme);

        var messageHook = new Win32WindowMessageHook(mainWindowHandle);
        var hotkeyPersister = new JsonHotkeyStatePersister(paths.HotkeysFile);
        INativeHotkeyApi nativeHotkeys = new WindowThreadNativeHotkeyApi(
            uiDispatcher,
            new Win32NativeHotkeyApi());
        var hotkeys = new TransactionalHotkeyRegistrar(
            mainWindowHandle,
            nativeHotkeys,
            hotkeyPersister);
        var escapeDismissHotkey = new ScopedUnmodifiedHotkeyRegistration(
            mainWindowHandle,
            registrationId: 0x3FFE,
            virtualKey: 0x1B,
            nativeHotkeys);

        var configuredHotkeys = new Dictionary<string, PlatformHotkeyChord>(StringComparer.Ordinal);
        var startupHotkeyFallbacks = new Dictionary<string, HotkeyStartupFallback>(StringComparer.Ordinal);

        PlatformHotkeyChord LoadHotkey(string commandName, HotkeyCommand command)
        {
            if (hotkeyPersister.TryLoad(commandName, out PlatformHotkeyChord persisted))
            {
                configuredHotkeys[commandName] = persisted;
                return persisted;
            }

            string canonical = HotkeySettings.CreateDefault()[command].Canonical;
            if (!HotkeyChordParser.TryParse(canonical, out PlatformHotkeyChord chord))
            {
                throw new InvalidOperationException($"The default {command} shortcut is invalid.");
            }

            configuredHotkeys[commandName] = chord;
            return chord;
        }

        async Task<HotkeyUpdateResult> RegisterInitialHotkeyAsync(
            string commandName,
            HotkeyCommand settingsCommand,
            uint fallbackVirtualKey)
        {
            PlatformHotkeyChord requested = LoadHotkey(commandName, settingsCommand);
            HotkeyUpdateResult registered = await hotkeys.RegisterInitialAsync(commandName, requested);
            if (registered.Status != HotkeyUpdateStatus.Conflict)
            {
                return registered;
            }

            // A global chord can be claimed after it was saved (for example by
            // a legacy Cursivis installation). Keep the command usable with a
            // stable F-key fallback, persist that replacement, and surface it
            // explicitly in Settings so the user can choose a different chord.
            // A system utility can also own the primary fallback, so try an
            // alternate modifier set before declaring the command inactive.
            foreach (PlatformHotkeyChord fallback in GetFallbackCandidates(fallbackVirtualKey))
            {
                if (fallback == requested)
                {
                    continue;
                }

                HotkeyUpdateResult fallbackResult = await hotkeys.UpdateAsync(commandName, fallback);
                if (fallbackResult.Status is not (HotkeyUpdateStatus.Success or HotkeyUpdateStatus.AlreadyActive))
                {
                    continue;
                }

                configuredHotkeys[commandName] = fallback;
                startupHotkeyFallbacks[commandName] = new HotkeyStartupFallback(
                    requested.ToString(),
                    fallback.ToString());
                return fallbackResult;
            }

            return registered;
        }

        static IEnumerable<PlatformHotkeyChord> GetFallbackCandidates(uint virtualKey)
        {
            yield return new PlatformHotkeyChord(
                PlatformHotkeyModifiers.Control | PlatformHotkeyModifiers.Alt,
                virtualKey);
            yield return new PlatformHotkeyChord(
                PlatformHotkeyModifiers.Control | PlatformHotkeyModifiers.Shift,
                virtualKey);
        }

        HotkeyUpdateResult contextStatus = await RegisterInitialHotkeyAsync(
            ContextTriggerCommand,
            HotkeyCommand.ContextTrigger,
            fallbackVirtualKey: 0x75); // F6
        HotkeyUpdateResult quickTaskStatus = await RegisterInitialHotkeyAsync(
            QuickTaskCommand,
            HotkeyCommand.CustomQuickTask,
            fallbackVirtualKey: 0x79); // F10
        HotkeyUpdateResult directTakeActionStatus = await RegisterInitialHotkeyAsync(
            DirectTakeActionCommand,
            HotkeyCommand.DirectTakeAction,
            fallbackVirtualKey: 0x77); // F8
        HotkeyUpdateResult smartDictationStatus = await RegisterInitialHotkeyAsync(
            SmartDictationCommand,
            HotkeyCommand.SmartDictation,
            fallbackVirtualKey: 0x78); // F9
        HotkeyUpdateResult liveModeStatus = await RegisterInitialHotkeyAsync(
            RealtimeLiveModeCommand,
            HotkeyCommand.RealtimeLiveMode,
            fallbackVirtualKey: 0x76); // F7
        HotkeyUpdateResult cancelStatus = await RegisterInitialHotkeyAsync(
            CancelCommand,
            HotkeyCommand.CancelEmergencyStop,
            fallbackVirtualKey: 0x7A); // F11
        HotkeyUpdateResult settingsStatus = await RegisterInitialHotkeyAsync(
            OpenSettingsCommand,
            HotkeyCommand.OpenSettings,
            fallbackVirtualKey: 0x7B); // F12

        return new AppRuntime(
            credentialManager,
            responsesGateway,
            messageHook,
            hotkeys,
            escapeDismissHotkey,
            controller,
            orbWindow,
            resultWindow,
            browserBridge,
            takeActionController,
            liveModeWindow,
            liveMode,
            smartDictation,
            liveModeMemory,
            pointerMonitor,
            quickTaskStore,
            settingsStore,
            startupRegistration,
            navigationGuidanceConfiguration,
            navigationGuidanceInteraction,
            quickTaskFinalizer,
            quickTaskLoad.Value,
            currentSettings,
            settingsLoad.Status,
            configuredHotkeys,
            contextStatus,
            quickTaskStatus,
            directTakeActionStatus,
            smartDictationStatus,
            liveModeStatus,
            cancelStatus,
            settingsStatus,
            startupHotkeyFallbacks);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _liveCompletionVisibilityCancellation?.Cancel();
        _liveCompletionVisibilityCancellation?.Dispose();
        _liveCompletionVisibilityCancellation = null;
        _dictationCompletionVisibilityCancellation?.Cancel();
        _dictationCompletionVisibilityCancellation?.Dispose();
        _dictationCompletionVisibilityCancellation = null;
        _messageHook.HotkeyPressed -= OnHotkeyPressed;
        _resultWindow.SettingsRequested -= OnSettingsRequested;
        _orbWindow.SettingsRequested -= OnSettingsRequested;
        _orbWindow.MicrophoneRequested -= OnLiveModeRequested;
        _orbWindow.CancelRequested -= OnOrbCancelRequested;
        _orbWindow.LiveTranscriptVisibilityRequested -= OnLiveTranscriptVisibilityRequested;
        _liveModeWindow.StopRequested -= OnLiveModeRequested;
        _liveMode.SnapshotChanged -= OnLiveModeSnapshotChanged;
        _smartDictation.SnapshotChanged -= OnSmartDictationSnapshotChanged;
        _resultWindow.ThemeRequested -= OnThemeRequested;
        _resultWindow.OverlayVisibilityChanged -= OnResultVisibilityChanged;
        _pointerMonitor.PointerPressed -= OnPointerPressed;
        _escapeDismissHotkey.Dispose();
        ContextTrigger.Dispose();
        _takeActionController.Dispose();
        await _smartDictation.DisposeAsync();
        await _liveMode.DisposeAsync();
        await _browserBridge.DisposeAsync();
        _messageHook.Dispose();
        _pointerMonitor.Dispose();
        await _hotkeys.DisposeAsync();
        _navigationGuidanceInteraction.Dispose();
        _settingsWriteGate.Dispose();
        _resultWindow.Close();
        _liveModeWindow.Close();
        _orbWindow.Close();
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs args)
    {
        if (HotkeyCaptureCoordinator.IsCapturing)
        {
            if (TryGetRegisteredHotkeyChord(args.RegistrationId, out string chord))
            {
                _ = HotkeyCaptureCoordinator.TryCaptureRegisteredChord(chord);
            }
            else if (_escapeDismissHotkey.Matches(args.RegistrationId))
            {
                _ = HotkeyCaptureCoordinator.TryCancelCapture();
            }

            return;
        }

        if (_escapeDismissHotkey.Matches(args.RegistrationId))
        {
            ContextTrigger.DismissTransientResult();
            return;
        }

        if (_hotkeys.TryGetActive(ContextTriggerCommand, out ActiveHotkeyRegistration? context) &&
            context?.RegistrationId == args.RegistrationId)
        {
            ContextTrigger.Invoke();
            return;
        }

        if (_hotkeys.TryGetActive(QuickTaskCommand, out ActiveHotkeyRegistration? quickTask) &&
            quickTask?.RegistrationId == args.RegistrationId)
        {
            ContextTrigger.InvokeQuickTask(CurrentQuickTask);
            return;
        }

        if (_hotkeys.TryGetActive(DirectTakeActionCommand, out ActiveHotkeyRegistration? takeAction) &&
            takeAction?.RegistrationId == args.RegistrationId)
        {
            _takeActionController.InvokeDirect();
            return;
        }

        if (_hotkeys.TryGetActive(SmartDictationCommand, out ActiveHotkeyRegistration? smartDictation) &&
            smartDictation?.RegistrationId == args.RegistrationId)
        {
            _ = ToggleSmartDictationAsync();
            return;
        }

        if (_hotkeys.TryGetActive(RealtimeLiveModeCommand, out ActiveHotkeyRegistration? liveMode) &&
            liveMode?.RegistrationId == args.RegistrationId)
        {
            _ = ToggleLiveModeAsync();
            return;
        }

        if (_hotkeys.TryGetActive(OpenSettingsCommand, out ActiveHotkeyRegistration? settings) &&
            settings?.RegistrationId == args.RegistrationId)
        {
            OnSettingsRequested(this, EventArgs.Empty);
            return;
        }

        if (_hotkeys.TryGetActive(CancelCommand, out ActiveHotkeyRegistration? cancel) &&
            cancel?.RegistrationId == args.RegistrationId)
        {
            ContextTrigger.Cancel();
            _takeActionController.Cancel();
            _ = _smartDictation.CancelAsync();
            _ = _liveMode.StopAsync();
        }
    }

    private bool TryGetRegisteredHotkeyChord(int registrationId, out string chord)
    {
        foreach (string commandName in new[]
                 {
                     ContextTriggerCommand,
                     QuickTaskCommand,
                     DirectTakeActionCommand,
                     SmartDictationCommand,
                     RealtimeLiveModeCommand,
                     CancelCommand,
                     OpenSettingsCommand,
                 })
        {
            if (_hotkeys.TryGetActive(commandName, out ActiveHotkeyRegistration? registration) &&
                registration?.RegistrationId == registrationId)
            {
                chord = registration.Chord.ToString();
                return true;
            }
        }

        chord = string.Empty;
        return false;
    }

    private void OnSettingsRequested(object? sender, EventArgs args)
    {
        _ = _smartDictation.CancelAsync();
        _ = _liveMode.StopAsync();
        _resultWindow.Hide();
        _liveModeWindow.Hide();
        _orbWindow.Hide();
        App.ShowSettingsWindow();
    }

    private void OnThemeRequested(ElementTheme theme)
    {
        _resultWindow.SetRequestedTheme(theme);
        _orbWindow.SetRequestedTheme(theme);
        _liveModeWindow.SetRequestedTheme(theme);
        _ = UpdateApplicationSettingsAsync(settings => settings with
        {
            Appearance = settings.Appearance with
            {
                Theme = theme == ElementTheme.Dark
                    ? DomainApplicationTheme.Dark
                    : DomainApplicationTheme.Light,
            },
        });
    }

    private void OnLiveModeRequested(object? sender, EventArgs args) =>
        _ = ToggleLiveModeAsync();

    private void OnOrbCancelRequested(object? sender, EventArgs args)
    {
        if (_smartDictation.Snapshot.IsActive)
        {
            _ = _smartDictation.CancelAsync();
            return;
        }

        if (_liveMode.Snapshot.IsActive)
        {
            _ = _liveMode.StopAsync();
        }
    }

    private async Task ToggleLiveModeAsync()
    {
        try
        {
            if (!_liveMode.Snapshot.IsActive)
            {
                await _smartDictation.CancelAsync();
                ContextTrigger.DismissTransientResult();
            }

            await _liveMode.ToggleAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ToggleSmartDictationAsync()
    {
        try
        {
            if (!_smartDictation.Snapshot.IsActive)
            {
                await _liveMode.StopAsync();
                ContextTrigger.DismissTransientResult();
            }

            await _smartDictation.ToggleAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnSmartDictationSnapshotChanged(SmartDictationSnapshot snapshot)
    {
        _ = _orbWindow.DispatcherQueue.TryEnqueue(() => ApplySmartDictationSnapshot(snapshot));
    }

    private void ApplySmartDictationSnapshot(SmartDictationSnapshot snapshot)
    {
        _dictationCompletionVisibilityCancellation?.Cancel();
        _dictationCompletionVisibilityCancellation?.Dispose();
        _dictationCompletionVisibilityCancellation = null;

        OrbPresentationState orbState = snapshot.State switch
        {
            SmartDictationState.Listening => OrbPresentationState.Listening,
            SmartDictationState.Transcribing or SmartDictationState.Polishing => OrbPresentationState.Thinking,
            SmartDictationState.Inserting => OrbPresentationState.Executing,
            SmartDictationState.Done => OrbPresentationState.Done,
            SmartDictationState.Cancelled => OrbPresentationState.Cancelled,
            SmartDictationState.Error => OrbPresentationState.Error,
            _ => OrbPresentationState.Idle,
        };
        string detail = snapshot.State switch
        {
            SmartDictationState.Listening =>
                $"Speak naturally · press again to finish · level {Math.Round(snapshot.AudioLevel * 100):0}%",
            SmartDictationState.Transcribing => "Using OpenAI Audio Transcriptions",
            SmartDictationState.Polishing => "Removing false starts and restoring punctuation",
            SmartDictationState.Inserting => "Returning text to the original application",
            SmartDictationState.Done when snapshot.Inserted => "Text inserted · use the app's Undo command to revert",
            SmartDictationState.Done when snapshot.CopiedToClipboard => "The original target changed, so the text was copied",
            SmartDictationState.Cancelled => "No text was inserted or copied",
            SmartDictationState.Error => snapshot.SafeError ?? "Smart Dictation unavailable",
            _ => snapshot.Status,
        };
        _orbWindow.ShowDictationState(orbState, snapshot.Status, detail);

        if (snapshot.State is SmartDictationState.Done or SmartDictationState.Cancelled)
        {
            _dictationCompletionVisibilityCancellation = new CancellationTokenSource();
            _ = HideCompletedDictationOrbAsync(_dictationCompletionVisibilityCancellation.Token);
        }
    }

    private async Task HideCompletedDictationOrbAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), cancellationToken);
            _ = _orbWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_smartDictation.Snapshot.IsActive)
                {
                    _orbWindow.Hide();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnLiveModeSnapshotChanged(LiveModeSnapshot snapshot)
    {
        _ = _orbWindow.DispatcherQueue.TryEnqueue(() => ApplyLiveModeSnapshot(snapshot));
    }

    private void ApplyLiveModeSnapshot(LiveModeSnapshot snapshot)
    {
        _liveCompletionVisibilityCancellation?.Cancel();
        _liveCompletionVisibilityCancellation?.Dispose();
        _liveCompletionVisibilityCancellation = null;
        if (snapshot.State == LiveModeState.Ended)
        {
            _liveModeWindow.Hide();
            _orbWindow.ShowState(
                OrbPresentationState.Done,
                "Live Mode ended",
                "Conversation closed");
            _liveCompletionVisibilityCancellation = new CancellationTokenSource();
            _ = HideCompletedLiveOrbAsync(_liveCompletionVisibilityCancellation.Token);
            return;
        }

        // Live Mode must keep the compact orb (with faint Live-only glow), the
        // circular cancel control, and the transcript panel visible together.
        OrbPresentationState orbState = snapshot.State switch
        {
            LiveModeState.Connecting => OrbPresentationState.Thinking,
            LiveModeState.Listening or LiveModeState.UserSpeaking => OrbPresentationState.Listening,
            LiveModeState.Thinking => OrbPresentationState.Thinking,
            LiveModeState.Speaking => OrbPresentationState.Speaking,
            LiveModeState.ExecutingTool => OrbPresentationState.Executing,
            LiveModeState.Stopping => OrbPresentationState.Cancelled,
            LiveModeState.Error => OrbPresentationState.Error,
            _ => OrbPresentationState.Idle,
        };
        string detail = snapshot.State switch
        {
            LiveModeState.UserSpeaking when !string.IsNullOrWhiteSpace(snapshot.UserTranscript) =>
                snapshot.UserTranscript,
            LiveModeState.Speaking when !string.IsNullOrWhiteSpace(snapshot.AssistantTranscript) =>
                snapshot.AssistantTranscript,
            LiveModeState.Error => snapshot.SafeError ?? "Live Mode unavailable",
            LiveModeState.Listening => "Speak naturally · press again to end",
            LiveModeState.Connecting => "Opening a secure Realtime session",
            _ => snapshot.Status,
        };
        bool transcriptVisible = CurrentSettings.Voice.LiveTranscriptPanelVisible;
        _orbWindow.ShowLiveState(
            orbState,
            snapshot.Status,
            detail,
            snapshot.AudioLevel,
            transcriptVisible);
        if (transcriptVisible)
        {
            _liveModeWindow.ShowSnapshot(snapshot, _orbWindow.OverlayBounds);
        }
        else
        {
            _liveModeWindow.Hide();
        }
    }

    private void OnLiveTranscriptVisibilityRequested(
        object? sender,
        LiveTranscriptVisibilityRequestedEventArgs args) =>
        _ = UpdateLiveTranscriptVisibilityAsync(args.Visible);

    private async Task UpdateLiveTranscriptVisibilityAsync(bool visible)
    {
        try
        {
            await UpdateApplicationSettingsAsync(settings => settings with
            {
                Voice = settings.Voice with { LiveTranscriptPanelVisible = visible },
            });

            LiveModeSnapshot snapshot = _liveMode.Snapshot;
            if (snapshot.State == LiveModeState.Ended)
            {
                return;
            }

            ApplyLiveModeSnapshot(snapshot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // Keep the prior persisted preference intact if settings storage is
            // unavailable. Reapply the current snapshot immediately so the eye
            // control and transcript cannot imply a preference was saved.
            if (_liveMode.Snapshot.State != LiveModeState.Ended)
            {
                ApplyLiveModeSnapshot(_liveMode.Snapshot);
            }
        }
    }

    private async Task HideCompletedLiveOrbAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), cancellationToken);
            _ = _orbWindow.DispatcherQueue.TryEnqueue(() =>
            {
                if (_liveMode.Snapshot.State == LiveModeState.Ended)
                {
                    _orbWindow.Hide();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnResultVisibilityChanged(bool visible)
    {
        if (!visible)
        {
            _pointerMonitor.Stop();
            _escapeDismissHotkey.Disable();
            return;
        }

        _ = _escapeDismissHotkey.Enable();

        try
        {
            _pointerMonitor.Start();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _pointerMonitor.Stop();
        }
    }

    private void OnPointerPressed(object? sender, NativePointerPressedEventArgs args)
    {
        if (!_resultWindow.IsVisible)
        {
            return;
        }

        OverlayRectangle[] children = _orbWindow.IsVisible
            ? [_orbWindow.OverlayBounds]
            : [];
        var context = new OverlayDismissalContext(
            IsResultPanelVisible: true,
            _resultWindow.OverlayBounds,
            children,
            _resultWindow.IsInteracting);
        if (OverlayDismissalPolicy.ShouldDismiss(args.Position, context))
        {
            ContextTrigger.DismissTransientResult();
        }
    }

}

public sealed record HotkeyStartupFallback(string RequestedChord, string ActiveChord);
