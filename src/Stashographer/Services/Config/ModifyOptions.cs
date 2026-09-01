namespace Stashographer.Services.Config;

/// <summary>Runtime policy for photo-first deferred inventory modifications.</summary>
public sealed class ModifyOptions
{
    /// <summary>Run vision identification in the background when an AI model is configured.</summary>
    public bool AutoProcessPhotos { get; set; } = true;

    /// <summary>Detect multiple objects and create one focused queue entry for each.</summary>
    public bool SplitMultipleItems { get; set; } = true;

    /// <summary>Number of earlier confirmed session matches supplied as weak context.</summary>
    public int ContextItemCount { get; set; } = 8;
}
