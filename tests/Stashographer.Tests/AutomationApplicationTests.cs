using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Stashographer.Data;
using Stashographer.Data.Entities;
using Stashographer.Services.Automation;
using Stashographer.Services.Inventory;
using Stashographer.Services.Security;

namespace Stashographer.Tests;

[Collection(AdminEnvironmentCollection.Name)]
public sealed class AutomationApplicationTests
{
    [Fact]
    public async Task Api_requires_activation_and_key_and_only_creates_review_drafts()
    {
        await using var app = CreateApplication();
        using var client = app.Factory.CreateClient();
        await using (var initialScope = app.Factory.Services.CreateAsyncScope())
        {
            var initialConnections = initialScope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
            using var initialConnection = await initialConnections.OpenAsync();
            var databasePath = await initialConnection.QuerySingleAsync<string>(
                "SELECT file FROM pragma_database_list WHERE name = 'main'");
            Assert.StartsWith(app.Directory, databasePath, StringComparison.OrdinalIgnoreCase);
            var initial = await initialScope.ServiceProvider.GetRequiredService<AgentAccessService>().GetAsync();
            Assert.False(initial.ApiEnabled);
            Assert.Null(initial.ApiCredential);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1")).StatusCode);
        using (var historyPage = await client.GetAsync("/consumption"))
        {
            Assert.Equal(HttpStatusCode.OK, historyPage.StatusCode);
            Assert.Contains("Use history", await historyPage.Content.ReadAsStringAsync());
        }

        string secret;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<AgentAccessService>();
            secret = (await access.GenerateAsync(AgentSurface.Api)).Secret;
            await access.UpdateEnabledAsync(apiEnabled: true, mcpEnabled: false);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1")).StatusCode);

        await using (var consumptionScope = app.Factory.Services.CreateAsyncScope())
        {
            var inventoryService = consumptionScope.ServiceProvider.GetRequiredService<InventoryService>();
            var consumptionService = consumptionScope.ServiceProvider.GetRequiredService<ConsumptionService>();
            var usedItem = await inventoryService.SaveAsync(new Item
            {
                Name = "API-visible soup",
                ItemKindId = 1,
                Quantity = 2,
                Unit = "cans"
            });
            await consumptionService.UseItemAsync(usedItem.Id, description: "API history check");
        }
        using (var consumptionResponse = await client.GetAsync("/api/v1/consumption?search=soup"))
        {
            Assert.Equal(HttpStatusCode.OK, consumptionResponse.StatusCode);
            using var consumptionJson = JsonDocument.Parse(await consumptionResponse.Content.ReadAsStringAsync());
            var history = Assert.Single(consumptionJson.RootElement.EnumerateArray());
            Assert.Equal("Manual", history.GetProperty("kind").GetString());
            Assert.Equal("API-visible soup", Assert.Single(history.GetProperty("lines").EnumerateArray())
                .GetProperty("itemName").GetString());
        }

        using var created = await client.PostAsJsonAsync("/api/v1/intake/items", new ItemDraftRequest
        {
            Name = "API-proposed cable",
            ItemKindId = 4,
            Quantity = 2,
            LocationId = 3,
            Attributes = new() { ["Connector"] = "USB-C" },
            PriceAmount = 8.99m,
            ExpiryDate = new DateOnly(2027, 1, 2),
            ExpiryKind = ExpiryDateKind.BestBefore
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var queueJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("Manual", queueJson.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal("ReadyForReview", queueJson.RootElement.GetProperty("status").GetString());
        var specialAttributes = queueJson.RootElement.GetProperty("draft").GetProperty("specialAttributes");
        Assert.Equal(
            "GBP",
            specialAttributes.GetProperty("price").GetProperty("currencyCode").GetString());
        Assert.Equal(
            "2027-01-02",
            specialAttributes.GetProperty("expiry").GetProperty("dateValue").GetString());

        using var inventory = await client.GetAsync("/api/v1/inventory?search=API-proposed%20cable");
        Assert.Equal(HttpStatusCode.OK, inventory.StatusCode);
        using var inventoryJson = JsonDocument.Parse(await inventory.Content.ReadAsStringAsync());
        Assert.Equal(0, inventoryJson.RootElement.GetArrayLength());

        await using var auditScope = app.Factory.Services.CreateAsyncScope();
        var connections = auditScope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var connection = await connections.OpenAsync();
        Assert.True(await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM AgentAccessEvents WHERE Surface = 'Api'") >= 3);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM AgentAccessEvents WHERE Path LIKE @secret",
            new { secret = $"%{secret}%" }));
    }

    [Fact]
    public async Task Mcp_exposes_shared_tools_and_can_be_locked_with_a_separate_key()
    {
        await using var app = CreateApplication();
        using var client = app.Factory.CreateClient();
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<AgentAccessService>();
            await access.GenerateAsync(AgentSurface.Api);
            await access.UpdateEnabledAsync(apiEnabled: true, mcpEnabled: true);
        }

        using var enabledRequest = McpProbe();
        using var enabled = await client.SendAsync(enabledRequest);
        Assert.NotEqual(HttpStatusCode.NotFound, enabled.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, enabled.StatusCode);

        string mcpSecret;
        await using (var scope = app.Factory.Services.CreateAsyncScope())
        {
            var access = scope.ServiceProvider.GetRequiredService<AgentAccessService>();
            mcpSecret = (await access.GenerateAsync(AgentSurface.Mcp)).Secret;
            Assert.NotNull((await access.GetAsync()).McpCredential);
            Assert.Equal(
                AgentAccessOutcome.Unauthorized,
                (await access.AuthenticateAsync(AgentSurface.Mcp, null)).Outcome);
        }

        using var credentialClient = app.Factory.CreateClient();
        Assert.Null(credentialClient.DefaultRequestHeaders.Authorization);
        using var deniedGet = await credentialClient.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedGet.StatusCode);
        using var deniedRequest = McpProbe();
        using var deniedPost = await credentialClient.SendAsync(deniedRequest);
        await using (var auditScope = app.Factory.Services.CreateAsyncScope())
        {
            var connections = auditScope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
            using var connection = await connections.OpenAsync();
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM AgentAccessEvents WHERE Surface = 'Mcp'"));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, deniedPost.StatusCode);

        using var keyedRequest = McpProbe();
        keyedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcpSecret);
        using var keyed = await credentialClient.SendAsync(keyedRequest);
        Assert.NotEqual(HttpStatusCode.Unauthorized, keyed.StatusCode);

        credentialClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", mcpSecret);
        await using (var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(credentialClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            credentialClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: false))
        await using (var mcp = await McpClient.CreateAsync(transport))
        {
            var tools = await mcp.ListToolsAsync();
            Assert.Contains(tools, tool => tool.Name == "search_inventory");
            Assert.Contains(tools, tool => tool.Name == "list_consumption_history");
            Assert.Contains(tools, tool => tool.Name == "queue_item_draft");
            Assert.DoesNotContain(tools, tool => tool.Name.Contains("accept", StringComparison.OrdinalIgnoreCase));

            var result = await mcp.CallToolAsync(
                "list_item_kinds",
                new Dictionary<string, object?>());
            Assert.False(result.IsError ?? false);
            Assert.NotNull(result.StructuredContent);

            var historyResult = await mcp.CallToolAsync(
                "list_consumption_history",
                new Dictionary<string, object?>());
            Assert.False(historyResult.IsError ?? false);
            Assert.NotNull(historyResult.StructuredContent);
        }
    }

    private static HttpRequestMessage McpProbe() => new(HttpMethod.Post, "/mcp")
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json")
    };

    private static ApplicationHarness CreateApplication()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "stashographer-automation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var environment = new Dictionary<string, string?>
        {
            [AdminPasswordValidator.PasswordEnvironmentVariable] =
                "test-automation-administrator-password",
            ["ConnectionStrings__Default"] =
                $"Data Source={Path.Combine(directory, "web.db")};Pooling=False",
            ["Images__RootPath"] = Path.Combine(directory, "images"),
            ["Stashographer__DataProtectionKeysPath"] = Path.Combine(directory, "keys"),
            ["Stashographer__EnableApi"] = "true",
            ["Stashographer__EnableMcp"] = "true",
            ["SampleData__Enabled"] = "false",
            ["Ai__Enabled"] = "false"
        };
        var previousEnvironment = environment.Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable);
        foreach (var (key, value) in environment)
            Environment.SetEnvironmentVariable(key, value);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Testing"));
        return new ApplicationHarness(directory, factory, previousEnvironment);
    }

    private sealed class ApplicationHarness(
        string directory,
        WebApplicationFactory<Program> factory,
        IReadOnlyDictionary<string, string?> previousEnvironment) : IAsyncDisposable
    {
        public string Directory { get; } = directory;
        public WebApplicationFactory<Program> Factory { get; } = factory;

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var (key, value) in previousEnvironment)
                Environment.SetEnvironmentVariable(key, value);
            for (var attempt = 0; System.IO.Directory.Exists(Directory); attempt++)
            {
                try
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(50 * (attempt + 1));
                }
            }
        }
    }
}
