using System.Security.Cryptography;
using Cursivis.Contracts.OpenAI;
using Cursivis.Domain.Context;
using Cursivis.Domain.Interaction;
using Cursivis.Domain.Models;
using Cursivis.Domain.QuickTasks;

namespace Cursivis.Application.Context;

public enum SelectionCaptureStatus
{
    Captured,
    NoSelection,
    ForegroundChanged,
    TimedOut,
    Cancelled,
    Unavailable,
}

public sealed record SelectionCaptureResult(
    SelectionCaptureStatus Status,
    ContextSnapshot? Context,
    TimeSpan Duration,
    string SafeDetail)
{
    public static SelectionCaptureResult Captured(ContextSnapshot context, TimeSpan duration) =>
        new(SelectionCaptureStatus.Captured, context, duration, string.Empty);

    public static SelectionCaptureResult Failed(
        SelectionCaptureStatus status,
        TimeSpan duration,
        string safeDetail)
    {
        if (status == SelectionCaptureStatus.Captured)
        {
            throw new ArgumentException("A captured result requires context.", nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(safeDetail);
        return new SelectionCaptureResult(status, null, duration, safeDetail.Trim());
    }
}

public interface ISelectionCaptureService
{
    Task<SelectionCaptureResult> CaptureAsync(
        TimeSpan contextLifetime,
        CancellationToken cancellationToken = default);
}

public readonly record struct ScreenAnchor(int X, int Y);

public sealed record DetectedColor(
    byte Red,
    byte Green,
    byte Blue,
    string Hex,
    string ApproximateName);

public enum RegionContextCaptureStatus
{
    ImageCaptured,
    ColorDetected,
    Cancelled,
    Failed,
}

public sealed record RegionContextCaptureResult(
    RegionContextCaptureStatus Status,
    ContextExecutionInput? Input,
    DetectedColor? Color,
    ScreenAnchor Anchor,
    string SafeDetail);

public interface IRegionContextCaptureService
{
    Task<RegionContextCaptureResult> CaptureAsync(
        TimeSpan contextLifetime,
        CancellationToken cancellationToken = default);
}

public enum TextReplacementStatus
{
    Replaced,
    StaleTarget,
    UnsupportedTarget,
    Cancelled,
    Failed,
}

public sealed record TextReplacementResult(
    TextReplacementStatus Status,
    string SafeDetail);

public interface ISelectionReplacementService
{
    Task<TextReplacementResult> ReplaceAsync(
        ContextSnapshot expectedContext,
        string replacement,
        CancellationToken cancellationToken = default);
}

public enum TextInsertionStatus
{
    Inserted,
    StaleTarget,
    UnsupportedTarget,
    Cancelled,
    Failed,
}

public sealed record TextInsertionResult(
    TextInsertionStatus Status,
    string SafeDetail);

public interface ITextInsertionService
{
    Task<TextInsertionResult> InsertAsync(
        ContextSnapshot expectedContext,
        string text,
        CancellationToken cancellationToken = default);
}

public enum TextUndoStatus
{
    Undone,
    StaleTarget,
    UnsupportedTarget,
    Cancelled,
    Failed,
}

public sealed record TextUndoResult(
    TextUndoStatus Status,
    string SafeDetail);

public interface ITextUndoService
{
    Task<TextUndoResult> UndoAsync(
        ContextSnapshot expectedContext,
        CancellationToken cancellationToken = default);
}

public sealed class ContextImagePayload
{
    public const int MaximumEncodedBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
    };

    public ContextImagePayload(byte[] encodedBytes, string mediaType, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(encodedBytes);
        if (encodedBytes.Length is 0 or > MaximumEncodedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(encodedBytes));
        }

        if (string.IsNullOrWhiteSpace(mediaType) || !SupportedMediaTypes.Contains(mediaType.Trim()))
        {
            throw new ArgumentException("The captured image media type is unsupported.", nameof(mediaType));
        }

        if (pixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        // Own an immutable copy so the digest/fingerprint cannot be invalidated
        // by a caller reusing its capture buffer after the session begins.
        EncodedBytes = encodedBytes.ToArray();
        MediaType = mediaType.Trim().ToLowerInvariant();
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public ReadOnlyMemory<byte> EncodedBytes { get; }

    public string MediaType { get; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }
}

public sealed class ContextExecutionInput
{
    private ContextExecutionInput(ContextSnapshot context, ContextImagePayload? image)
    {
        Context = context;
        Image = image;
    }

    public ContextSnapshot Context { get; }

    public ContextImagePayload? Image { get; }

    public static ContextExecutionInput FromText(ContextSnapshot context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Kind == ContextKind.Image)
        {
            throw new ArgumentException("Image context requires its captured payload.", nameof(context));
        }

        return new ContextExecutionInput(context, null);
    }

    public static ContextExecutionInput FromImage(ContextSnapshot context, ContextImagePayload image)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(image);
        if (context.Kind != ContextKind.Image)
        {
            throw new ArgumentException("An image payload requires image context.", nameof(context));
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(image.EncodedBytes.Span, digest);
        if (!digest.SequenceEqual(context.ImageDigest.Span))
        {
            throw new ArgumentException("The captured image does not match its context fingerprint.", nameof(image));
        }

        return new ContextExecutionInput(context, image);
    }
}

public enum ContextTriggerExecutionStatus
{
    Completed,
    NeedsGuidedMode,
    ProviderFailed,
    MalformedResponse,
    Cancelled,
}

public sealed record ContextTriggerExecutionResult(
    ContextTriggerExecutionStatus Status,
    SmartResult? Result,
    OpenAiFailure? Failure,
    ModelIdentifier Model,
    TimeSpan Duration);

public sealed record GuidedOption
{
    public const int MaximumDynamicOptions = 4;
    public const int MaximumInstructionCharacters = 60_000;

    public GuidedOption(string id, string label, string instruction, bool isCustomTask = false)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 32)
        {
            throw new ArgumentException("A guided option id is required and must be compact.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(label) || label.Trim().Length is < 2 or > 28)
        {
            throw new ArgumentException("A guided option label must be between 2 and 28 characters.", nameof(label));
        }

        if (!isCustomTask && (string.IsNullOrWhiteSpace(instruction) ||
                              instruction.Trim().Length is < 12 or > MaximumInstructionCharacters))
        {
            throw new ArgumentException(
                $"A guided option instruction must be between 12 and {MaximumInstructionCharacters:N0} characters.",
                nameof(instruction));
        }

        Id = id.Trim();
        Label = label.Trim();
        Instruction = instruction?.Trim() ?? string.Empty;
        IsCustomTask = isCustomTask;
    }

    public string Id { get; }

    public string Label { get; }

    public string Instruction { get; }

    public bool IsCustomTask { get; }

    public static GuidedOption CustomTask { get; } = new(
        "custom_task",
        "Custom task",
        string.Empty,
        isCustomTask: true);
}

public sealed record GuidedOptionsExecutionResult(
    ContextTriggerExecutionStatus Status,
    IReadOnlyList<GuidedOption>? Options,
    OpenAiFailure? Failure,
    ModelIdentifier Model,
    TimeSpan Duration);

public interface IContextTriggerService
{
    Task<ContextTriggerExecutionResult> ExecuteAsync(
        ContextExecutionInput input,
        ModelIdentifier model,
        CancellationToken cancellationToken = default);

    Task<ContextTriggerExecutionResult> ExecuteGuidedAsync(
        ContextExecutionInput input,
        GuidedOperation operation,
        string? customInstruction,
        ModelIdentifier model,
        CancellationToken cancellationToken = default);

    Task<GuidedOptionsExecutionResult> GenerateGuidedOptionsAsync(
        ContextExecutionInput input,
        ModelIdentifier model,
        CancellationToken cancellationToken = default);

    Task<ContextTriggerExecutionResult> ExecuteGuidedInstructionAsync(
        ContextExecutionInput input,
        GuidedOption option,
        ModelIdentifier model,
        CancellationToken cancellationToken = default);

    Task<ContextTriggerExecutionResult> ExecuteQuickTaskAsync(
        ContextExecutionInput input,
        QuickTaskDefinition definition,
        ModelIdentifier model,
        CancellationToken cancellationToken = default);
}

public enum ContextSessionAccessStatus
{
    Available,
    Missing,
    Expired,
}

public sealed record ContextSessionAccess(
    ContextSessionAccessStatus Status,
    ContextExecutionInput? Input,
    SmartResult? LatestResult,
    string? ClipboardText)
{
    public ContextSnapshot? Context => Input?.Context;
}

public enum ResultClipboardWriteStatus
{
    Copied,
    Failed,
    Cancelled,
}

public sealed record ResultClipboardWriteResult(
    ResultClipboardWriteStatus Status,
    string SafeDetail);

public interface IResultClipboardService
{
    Task<ResultClipboardWriteResult> CopyTextAsync(
        string text,
        CancellationToken cancellationToken = default);
}
