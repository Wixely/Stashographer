namespace Stashographer.Services.Security;

/// <summary>Deployment-level capability gates; both surfaces are unavailable by default.</summary>
public sealed class AgentFeatureOptions
{
    public const string SectionName = "Stashographer";

    public bool EnableApi { get; set; }
    public bool EnableMcp { get; set; }
}
