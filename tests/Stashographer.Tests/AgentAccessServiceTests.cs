using Dapper;
using Microsoft.Extensions.Options;
using Stashographer.Services.Security;

namespace Stashographer.Tests;

public sealed class AgentAccessServiceTests
{
    [Fact]
    public async Task Deployment_flags_make_disabled_surfaces_unavailable()
    {
        await using var db = await TestDb.CreateAsync();
        var access = CreateAccess(db, enableApi: false, enableMcp: false);

        var configuration = await access.GetAsync();

        Assert.False(configuration.ApiAvailable);
        Assert.False(configuration.McpAvailable);
        Assert.Equal(
            AgentAccessOutcome.Unavailable,
            (await access.AuthenticateAsync(AgentSurface.Api, null)).Outcome);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            access.GenerateAsync(AgentSurface.Api));
    }

    [Fact]
    public async Task Api_key_is_hashed_and_rotation_invalidates_the_previous_key()
    {
        await using var db = await TestDb.CreateAsync();
        var access = CreateAccess(db);
        var first = await access.GenerateAsync(AgentSurface.Api);
        await access.UpdateEnabledAsync(apiEnabled: true, mcpEnabled: false);

        Assert.Equal(
            AgentAccessOutcome.Allowed,
            (await access.AuthenticateAsync(AgentSurface.Api, $"Bearer {first.Secret}")).Outcome);
        Assert.Equal(
            AgentAccessOutcome.Unauthorized,
            (await access.AuthenticateAsync(AgentSurface.Api, "Bearer incorrect")).Outcome);

        using (var conn = await db.Factory.OpenAsync())
        {
            var stored = await conn.QuerySingleAsync<(string SecretHash, string Suffix)>(
                "SELECT SecretHash, Suffix FROM AgentCredentials WHERE Kind = 'Api'");
            Assert.NotEqual(first.Secret, stored.SecretHash);
            Assert.DoesNotContain(first.Secret, stored.SecretHash);
            Assert.Equal(first.Suffix, stored.Suffix);
        }

        var second = await access.GenerateAsync(AgentSurface.Api);
        Assert.Equal(
            AgentAccessOutcome.Unauthorized,
            (await access.AuthenticateAsync(AgentSurface.Api, $"Bearer {first.Secret}")).Outcome);
        Assert.Equal(
            AgentAccessOutcome.Allowed,
            (await access.AuthenticateAsync(AgentSurface.Api, $"Bearer {second.Secret}")).Outcome);
    }

    [Fact]
    public async Task Mcp_requires_api_but_its_own_key_is_optional()
    {
        await using var db = await TestDb.CreateAsync();
        var access = CreateAccess(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            access.UpdateEnabledAsync(apiEnabled: false, mcpEnabled: true));
        await access.GenerateAsync(AgentSurface.Api);
        await access.UpdateEnabledAsync(apiEnabled: true, mcpEnabled: true);

        Assert.Equal(
            AgentAccessOutcome.Allowed,
            (await access.AuthenticateAsync(AgentSurface.Mcp, null)).Outcome);

        var mcp = await access.GenerateAsync(AgentSurface.Mcp);
        Assert.Equal(
            AgentAccessOutcome.Unauthorized,
            (await access.AuthenticateAsync(AgentSurface.Mcp, null)).Outcome);
        Assert.Equal(
            AgentAccessOutcome.Allowed,
            (await access.AuthenticateAsync(AgentSurface.Mcp, $"Bearer {mcp.Secret}")).Outcome);

        await access.RemoveMcpKeyAsync();
        Assert.Equal(
            AgentAccessOutcome.Allowed,
            (await access.AuthenticateAsync(AgentSurface.Mcp, null)).Outcome);
    }

    [Fact]
    public async Task Access_audit_stores_request_metadata_without_secrets_or_content()
    {
        await using var db = await TestDb.CreateAsync();
        var access = CreateAccess(db);
        var credential = await access.GenerateAsync(AgentSurface.Api);

        await access.RecordAccessAsync(
            AgentSurface.Api,
            credential.Suffix,
            "POST",
            "/api/v1/intake/items",
            201,
            "trace-123");

        using var conn = await db.Factory.OpenAsync();
        var row = await conn.QuerySingleAsync<AccessEvent>("""
            SELECT Surface, CredentialSuffix, Method, Path, StatusCode, CorrelationId
            FROM AgentAccessEvents
            """);
        Assert.Equal("Api", row.Surface);
        Assert.Equal(credential.Suffix, row.CredentialSuffix);
        Assert.Equal("POST", row.Method);
        Assert.Equal("/api/v1/intake/items", row.Path);
        Assert.Equal(201, row.StatusCode);
        Assert.Equal("trace-123", row.CorrelationId);

        var databaseText = string.Join(' ', await conn.QueryAsync<string>("""
            SELECT Surface || ' ' || COALESCE(CredentialSuffix, '') || ' ' || Method || ' ' || Path || ' '
                   || StatusCode || ' ' || CorrelationId
            FROM AgentAccessEvents
            """));
        Assert.DoesNotContain(credential.Secret, databaseText);
    }

    private static AgentAccessService CreateAccess(
        TestDb db,
        bool enableApi = true,
        bool enableMcp = true) =>
        new(
            db.Factory,
            Options.Create(new AgentFeatureOptions
            {
                EnableApi = enableApi,
                EnableMcp = enableMcp
            }),
            TimeProvider.System);

    private sealed record AccessEvent(
        string Surface,
        string? CredentialSuffix,
        string Method,
        string Path,
        int StatusCode,
        string CorrelationId)
    {
        public AccessEvent() : this(string.Empty, null, string.Empty, string.Empty, 0, string.Empty) { }
    }
}
