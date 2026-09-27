using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Stashographer.Services.Intake;
using Stashographer.Services.Security;

namespace Stashographer.Tests;

[Collection(AdminEnvironmentCollection.Name)]
public sealed partial class AdminApplicationTests : IAsyncLifetime
{
    private const string Password = "test-administrator-password";
    private string _directory = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private Dictionary<string, string?> _previousEnvironment = null!;

    public Task InitializeAsync()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "stashographer-admin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var environment = new Dictionary<string, string?>
        {
            [AdminPasswordValidator.PasswordEnvironmentVariable] = Password,
            ["ConnectionStrings__Default"] =
                $"Data Source={Path.Combine(_directory, "web.db")};Pooling=False",
            ["Images__RootPath"] = Path.Combine(_directory, "images"),
            ["Stashographer__DataProtectionKeysPath"] = Path.Combine(_directory, "keys"),
            ["SampleData__Enabled"] = "false",
            ["Ai__Enabled"] = "false"
        };
        _previousEnvironment = environment.Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable);
        foreach (var (key, value) in environment)
            Environment.SetEnvironmentVariable(key, value);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Testing"));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        foreach (var (key, value) in _previousEnvironment)
            Environment.SetEnvironmentVariable(key, value);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Configured_password_creates_and_can_end_an_administrator_session()
    {
        using var client = CreateClient();
        var loginPage = await client.GetStringAsync("/admin/login");
        var loginToken = AntiforgeryToken(loginPage);

        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = loginToken,
                ["password"] = Password,
                ["returnUrl"] = "/settings"
            }));

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/settings", login.Headers.Location?.OriginalString);
        using var settings = await client.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var settingsHtml = await settings.Content.ReadAsStringAsync();
        Assert.Contains("Administrator configuration", settingsHtml);

        using var logout = await client.PostAsync("/auth/logout", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryToken(settingsHtml)
            }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/", logout.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/settings")).StatusCode);
    }

    [Fact]
    public async Task Settings_requires_login_and_external_return_urls_are_rejected()
    {
        using var client = CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/inventory")).StatusCode);
        using var unauthenticated = await client.GetAsync("/settings");
        Assert.Equal(HttpStatusCode.Redirect, unauthenticated.StatusCode);
        Assert.Contains("/admin/login", unauthenticated.Headers.Location?.OriginalString);

        var loginPage = await client.GetStringAsync("/admin/login?returnUrl=%2F%2Fevil.example");
        using var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = AntiforgeryToken(loginPage),
                ["password"] = Password,
                ["returnUrl"] = "//evil.example"
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/settings", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Administrator_login_is_rate_limited()
    {
        using var client = CreateClient();
        var loginPage = await client.GetStringAsync("/admin/login");
        var token = AntiforgeryToken(loginPage);
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["__RequestVerificationToken"] = token,
                    ["password"] = "incorrect-password",
                    ["returnUrl"] = "/settings"
                }));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(
            [
                HttpStatusCode.Redirect,
                HttpStatusCode.Redirect,
                HttpStatusCode.Redirect,
                HttpStatusCode.Redirect,
                HttpStatusCode.Redirect,
                HttpStatusCode.TooManyRequests
            ],
            statuses);
    }

    [Fact]
    public async Task Browser_photo_upload_is_antiforgery_protected_and_idempotently_queued()
    {
        using var client = CreateClient();
        _ = await client.GetStringAsync("/scan"); // establishes the protected upload-owner cookie
        var antiforgery = await DnaXAntiforgeryTokenAsync(client);
        var token = Guid.NewGuid();
        var png = await PngAsync();

        using (var rejected = await client.PostAsJsonAsync("/dnax-uploads/sessions", new
        {
            id = token,
            profile = "QueuedPhoto",
            fileName = "mobile-camera.png",
            length = png.LongLength
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        using (var created = await SendDnaxAsync(client, HttpMethod.Post, "/dnax-uploads/sessions",
                   antiforgery, JsonContent.Create(new
                   {
                       id = token,
                       profile = "QueuedPhoto",
                       fileName = "mobile-camera.png",
                       length = png.LongLength
                   })))
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        using (var bytes = new ByteArrayContent(png))
        {
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/dnax-uploads/sessions/{token}/bytes")
            {
                Content = bytes
            };
            request.Headers.Add("X-DnaX-Antiforgery", antiforgery);
            request.Headers.Add("Upload-Offset", "0");
            request.Headers.Add("Upload-SHA256", Convert.ToHexString(SHA256.HashData(png)));
            using var uploaded = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        }

        using (var completed = await SendDnaxAsync(
                   client, HttpMethod.Post, $"/dnax-uploads/sessions/{token}/complete", antiforgery))
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        int queueItemId;
        using (var response = await SendDnaxAsync(
                   client, HttpMethod.Post,
                   $"/browser-uploads/{token}/complete?multipleItems=false", antiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("QueuedPhoto", json.RootElement.GetProperty("kind").GetString());
            Assert.True(json.RootElement.GetProperty("imageId").GetInt32() > 0);
            queueItemId = json.RootElement.GetProperty("queueItemId").GetInt32();
        }

        // A lost business-completion response can cause the browser to repeat conversion of
        // the same DNAX transport receipt. It must return the original queue receipt.
        using (var retry = await SendDnaxAsync(
                   client, HttpMethod.Post,
                   $"/browser-uploads/{token}/complete?multipleItems=false", antiforgery))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            using var json = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
            Assert.Equal(queueItemId, json.RootElement.GetProperty("queueItemId").GetInt32());
        }

        using (var recovered = await client.GetAsync($"/browser-uploads/{token}"))
        {
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            using var json = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync());
            Assert.Equal(queueItemId, json.RootElement.GetProperty("queueItemId").GetInt32());
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IntakeQueueService>();
        Assert.Equal(queueItemId, Assert.Single(
            await queue.GetOpenAsync(), item => item.BrowserUploadToken == token.ToString()).Id);
    }

    [Fact]
    public async Task Browser_upload_can_refresh_antiforgery_without_a_live_blazor_circuit()
    {
        using var client = CreateClient();
        // A direct transport request also receives an isolated upload-owner identity; users
        // do not need an administrator session or a live Blazor circuit.
        using var capabilities = await client.GetAsync("/dnax-uploads/capabilities");
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        using var capabilitiesJson = JsonDocument.Parse(await capabilities.Content.ReadAsStringAsync());
        Assert.True(capabilitiesJson.RootElement.GetProperty("profiles").TryGetProperty("QueuedPhoto", out _));
        Assert.False(string.IsNullOrWhiteSpace(
            capabilitiesJson.RootElement.GetProperty("ownerScope").GetString()));

        using var tokenResponse = await client.GetAsync("/browser-uploads/antiforgery-token");
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var antiforgery = tokenJson.RootElement.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(antiforgery));

    }

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> DnaXAntiforgeryTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/dnax-uploads/capabilities");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<HttpResponseMessage> SendDnaxAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string antiforgery,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-DnaX-Antiforgery", antiforgery);
        return await client.SendAsync(request);
    }

    private static async Task<byte[]> PngAsync()
    {
        using var image = new Image<Rgba32>(32, 24, new Rgba32(20, 80, 140));
        await using var output = new MemoryStream();
        await image.SaveAsPngAsync(output);
        return output.ToArray();
    }

    private static string AntiforgeryToken(string html)
    {
        var encoded = AntiforgeryTokenRegex().Match(html).Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(encoded));
        return WebUtility.HtmlDecode(encoded);
    }

    [GeneratedRegex(
        "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex AntiforgeryTokenRegex();
}
