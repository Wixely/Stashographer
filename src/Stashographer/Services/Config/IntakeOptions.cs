namespace Stashographer.Services.Config;

/// <summary>Runtime-configurable capture and queue policy.</summary>
public class IntakeOptions
{
    /// <summary>When false, Scan uses the original immediate validation flow.</summary>
    public bool QueueEnabled { get; set; } = true;

    /// <summary>Run product lookup for queued barcodes in the background.</summary>
    public bool AutoProcessBarcodes { get; set; } = true;

    /// <summary>Run vision processing for queued photos when an AI model is configured.</summary>
    public bool AutoProcessPhotos { get; set; } = true;

    /// <summary>Hold processed suggestions for item-by-item acceptance.</summary>
    public bool RequireReview { get; set; } = true;

    /// <summary>Number of earlier captures supplied as weak session context to the model.</summary>
    public int ContextItemCount { get; set; } = 8;

    /// <summary>Keep the camera stream open after a successful live barcode read.</summary>
    public bool ContinueLiveScanning { get; set; } = true;

    /// <summary>Visible pause between accepted live barcode reads, in milliseconds.</summary>
    public int LiveScanCooldownMilliseconds { get; set; } = 2000;

    /// <summary>Ask for a total quantity when the same barcode is intentionally read again.</summary>
    public bool PromptForConsecutiveBarcodeQuantity { get; set; } = true;
}
