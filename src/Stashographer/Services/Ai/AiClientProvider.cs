using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Stashographer.Services.Ai;

/// <summary>
/// Supplies the current chat/vision clients and can be reconfigured at runtime (from the
/// Settings page) without restarting the app — important inside Docker.
/// </summary>
public interface IAiClientProvider
{
    bool IsConfigured { get; }

    /// <summary>The options currently in effect.</summary>
    AiOptions Current { get; }

    IChatClient? GetChatClient();
    IChatClient? GetVisionClient();

    /// <summary>Rebuilds the clients for the given options. Safe to call while in use.</summary>
    void Reconfigure(AiOptions options);
}

/// <summary>
/// OpenAI-protocol implementation. State (options + built clients) is swapped atomically as
/// one immutable record, so in-flight calls keep the clients they started with.
/// </summary>
public class OpenAiClientProvider(ILogger<OpenAiClientProvider> logger) : IAiClientProvider
{
    private sealed record State(AiOptions Options, IChatClient? Chat, IChatClient? Vision);

    private volatile State _state = new(new AiOptions(), null, null);

    public bool IsConfigured => _state.Chat is not null;
    public AiOptions Current => _state.Options;
    public IChatClient? GetChatClient() => _state.Chat;
    public IChatClient? GetVisionClient() => _state.Vision;

    public void Reconfigure(AiOptions options)
    {
        if (!options.IsConfigured)
        {
            _state = new State(options, null, null);
            logger.LogInformation("AI disabled / not configured");
            return;
        }

        try
        {
            var clientOptions = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(options.Endpoint))
                clientOptions.Endpoint = new Uri(options.Endpoint);

            var client = new OpenAIClient(new ApiKeyCredential(options.ApiKey!), clientOptions);
            _state = new State(
                options,
                client.GetChatClient(options.Model).AsIChatClient(),
                client.GetChatClient(options.EffectiveVisionModel).AsIChatClient());
            logger.LogInformation("AI configured: model {Model}, vision {Vision}, endpoint {Endpoint}",
                options.Model, options.EffectiveVisionModel,
                string.IsNullOrWhiteSpace(options.Endpoint) ? "(OpenAI)" : options.Endpoint);
        }
        catch (Exception ex)
        {
            // e.g. malformed endpoint URI — stay unconfigured rather than crash.
            _state = new State(options, null, null);
            logger.LogError(ex, "AI configuration failed");
        }
    }
}
