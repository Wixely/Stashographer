namespace Stashographer.Services.Ai;

/// <summary>
/// Configuration for the optional AI enrichment features. Bound from the <c>Ai</c> section
/// of configuration / environment variables. Works with any OpenAI-protocol endpoint
/// (OpenAI, Azure OpenAI, or a local server such as Ollama / LM Studio).
/// </summary>
public class AiOptions
{
    public const string SectionName = "Ai";

    public bool Enabled { get; set; }

    /// <summary>Base URL of the OpenAI-compatible API (leave empty for OpenAI itself).</summary>
    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Chat model id, e.g. "gpt-4o-mini".</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Optional separate model for vision calls (photo identify/detect/match). Defaults to
    /// <see cref="Model"/> when unset.
    /// </summary>
    public string? VisionModel { get; set; }

    /// <summary>The model to use for vision calls.</summary>
    public string EffectiveVisionModel => string.IsNullOrWhiteSpace(VisionModel) ? Model : VisionModel!;

    /// <summary>Enabled only when the flag is set and an API key is present.</summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}
