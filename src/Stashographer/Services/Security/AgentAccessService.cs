using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Stashographer.Data;

namespace Stashographer.Services.Security;

public enum AgentSurface
{
    Api,
    Mcp
}

public enum AgentAccessOutcome
{
    Allowed,
    Unavailable,
    Unauthorized
}

public sealed record AgentCredentialInfo(string Suffix, DateTimeOffset CreatedAtUtc);

public sealed record AgentAccessConfiguration(
    bool ApiAvailable,
    bool McpAvailable,
    bool ApiEnabled,
    bool McpEnabled,
    AgentCredentialInfo? ApiCredential,
    AgentCredentialInfo? McpCredential);

public sealed record GeneratedAgentCredential(
    AgentSurface Surface,
    string Secret,
    string Suffix,
    DateTimeOffset CreatedAtUtc);

public sealed record AgentAuthenticationResult(
    AgentAccessOutcome Outcome,
    string? CredentialSuffix = null);

/// <summary>Deployment/application activation, credential rotation, authentication, and auditing.</summary>
public sealed class AgentAccessService(
    IDbConnectionFactory db,
    IOptions<AgentFeatureOptions> options,
    TimeProvider clock)
{
    private const string ApiEnabledKey = "Automation.ApiEnabled";
    private const string McpEnabledKey = "Automation.McpEnabled";
    private readonly AgentFeatureOptions _features = options.Value;

    public async Task<AgentAccessConfiguration> GetAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var settings = (await conn.QueryAsync<(string Key, string Value)>(
            "SELECT Key, Value FROM Settings WHERE Key IN (@api, @mcp)",
            new { api = ApiEnabledKey, mcp = McpEnabledKey }))
            .ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        var credentials = (await conn.QueryAsync<AgentCredentialRow>(
            "SELECT Kind, SecretHash, Suffix, CreatedAtUtc FROM AgentCredentials"))
            .ToDictionary(row => row.Kind, StringComparer.Ordinal);

        return new AgentAccessConfiguration(
            _features.EnableApi,
            _features.EnableMcp,
            ReadBool(settings, ApiEnabledKey),
            ReadBool(settings, McpEnabledKey),
            CredentialInfo(credentials.GetValueOrDefault(nameof(AgentSurface.Api))),
            CredentialInfo(credentials.GetValueOrDefault(nameof(AgentSurface.Mcp))));
    }

    public async Task UpdateEnabledAsync(
        bool apiEnabled,
        bool mcpEnabled,
        CancellationToken ct = default)
    {
        var current = await GetAsync(ct);
        if (apiEnabled && !_features.EnableApi && !current.ApiEnabled)
            throw new InvalidOperationException("API access is not enabled by the deployment configuration.");
        if (mcpEnabled && !_features.EnableMcp && !current.McpEnabled)
            throw new InvalidOperationException("MCP access is not enabled by the deployment configuration.");
        if (apiEnabled && current.ApiCredential is null)
            throw new InvalidOperationException("Generate an API key before enabling API access.");
        if (mcpEnabled && (!apiEnabled || current.ApiCredential is null))
            throw new InvalidOperationException("MCP requires enabled API access and an active API key.");

        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        await SetAsync(conn, tx, ApiEnabledKey, apiEnabled);
        await SetAsync(conn, tx, McpEnabledKey, mcpEnabled);
        tx.Commit();
    }

    public async Task<GeneratedAgentCredential> GenerateAsync(
        AgentSurface surface,
        CancellationToken ct = default)
    {
        if (surface == AgentSurface.Api && !_features.EnableApi)
            throw new InvalidOperationException("API access is not enabled by the deployment configuration.");
        if (surface == AgentSurface.Mcp)
        {
            if (!_features.EnableMcp)
                throw new InvalidOperationException("MCP access is not enabled by the deployment configuration.");
            if ((await GetAsync(ct)).ApiCredential is null)
                throw new InvalidOperationException("Generate an API key before generating an MCP key.");
        }

        var now = clock.GetUtcNow();
        var prefix = surface == AgentSurface.Api ? "stashographer_api_" : "stashographer_mcp_";
        var secret = prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var hash = Hash(secret);
        var suffix = secret[^8..];

        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO AgentCredentials (Kind, SecretHash, Suffix, CreatedAtUtc)
            VALUES (@Kind, @SecretHash, @Suffix, @CreatedAtUtc)
            ON CONFLICT(Kind) DO UPDATE SET
                SecretHash = excluded.SecretHash,
                Suffix = excluded.Suffix,
                CreatedAtUtc = excluded.CreatedAtUtc;
            """, new
        {
            Kind = surface.ToString(),
            SecretHash = hash,
            Suffix = suffix,
            CreatedAtUtc = now.ToString("O")
        });
        return new GeneratedAgentCredential(surface, secret, suffix, now);
    }

    public async Task RemoveMcpKeyAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "DELETE FROM AgentCredentials WHERE Kind = @kind",
            new { kind = nameof(AgentSurface.Mcp) });
    }

    public async Task<AgentAuthenticationResult> AuthenticateAsync(
        AgentSurface surface,
        string? authorizationHeader,
        CancellationToken ct = default)
    {
        var configuration = await GetAsync(ct);
        var available = surface == AgentSurface.Api
            ? configuration.ApiAvailable && configuration.ApiEnabled && configuration.ApiCredential is not null
            : configuration.McpAvailable && configuration.McpEnabled
              && configuration.ApiAvailable && configuration.ApiEnabled
              && configuration.ApiCredential is not null;
        if (!available) return new AgentAuthenticationResult(AgentAccessOutcome.Unavailable);

        var credential = surface == AgentSurface.Api
            ? configuration.ApiCredential
            : configuration.McpCredential;
        if (surface == AgentSurface.Mcp && credential is null)
            return new AgentAuthenticationResult(AgentAccessOutcome.Allowed);

        var token = BearerToken(authorizationHeader);
        if (token is null) return new AgentAuthenticationResult(AgentAccessOutcome.Unauthorized);

        using var conn = await db.OpenAsync(ct);
        var expectedHash = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT SecretHash FROM AgentCredentials WHERE Kind = @kind",
            new { kind = surface.ToString() });
        if (expectedHash is null || !FixedTimeEquals(expectedHash, Hash(token)))
            return new AgentAuthenticationResult(AgentAccessOutcome.Unauthorized);
        return new AgentAuthenticationResult(AgentAccessOutcome.Allowed, credential?.Suffix);
    }

    public async Task RecordAccessAsync(
        AgentSurface surface,
        string? credentialSuffix,
        string method,
        string path,
        int statusCode,
        string correlationId,
        CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO AgentAccessEvents
                (Surface, CredentialSuffix, Method, Path, StatusCode, CorrelationId, OccurredAtUtc)
            VALUES
                (@Surface, @CredentialSuffix, @Method, @Path, @StatusCode, @CorrelationId, @OccurredAtUtc);
            """, new
        {
            Surface = surface.ToString(),
            CredentialSuffix = credentialSuffix,
            Method = method,
            Path = path,
            StatusCode = statusCode,
            CorrelationId = correlationId,
            OccurredAtUtc = clock.GetUtcNow().ToString("O")
        });
    }

    private static Task SetAsync(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        string key,
        bool value) => connection.ExecuteAsync("""
            INSERT INTO Settings (Key, Value) VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """, new { key, value = value.ToString() }, transaction);

    private static AgentCredentialInfo? CredentialInfo(AgentCredentialRow? row) => row is null
        ? null
        : new AgentCredentialInfo(row.Suffix, DateTimeOffset.Parse(row.CreatedAtUtc));

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) && parsed;

    private static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected),
            Convert.FromHexString(actual));

    private static string? BearerToken(string? header)
    {
        const string prefix = "Bearer ";
        return header is not null && header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim()
            : null;
    }

    private sealed record AgentCredentialRow(
        string Kind,
        string SecretHash,
        string Suffix,
        string CreatedAtUtc)
    {
        public AgentCredentialRow() : this(string.Empty, string.Empty, string.Empty, string.Empty) { }
    }
}
