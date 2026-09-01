using System.Text.Json.Serialization;

namespace Stashographer.Services.Images;

/// <summary>The server-side action completed by a circuit-independent browser upload.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserUploadKind
{
    Image,
    QueuedPhoto,
    QueuedReceipt,
    QueuedModifyPhoto,
    Barcode,
    QueuedBarcode
}

/// <summary>
/// Durable receipt retained by both the server and browser until a Blazor component
/// acknowledges it. Queue operations are complete before this receipt is returned.
/// </summary>
public sealed record BrowserUploadResult(
    string Token,
    BrowserUploadKind Kind,
    int? ImageId,
    int? QueueItemId,
    string? Code,
    int? ModifyQueueItemId = null);

public sealed record BrowserUploadFailure(string Message, bool Retryable);

public sealed class BrowserUploadInProgressException : Exception
{
    public BrowserUploadInProgressException()
        : base("This upload is already being processed.") { }
}
