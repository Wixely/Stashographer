using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using MudBlazor.Services;
using Stashographer.Components;
using Stashographer.Data;
using Stashographer.Data.Migrations;
using Stashographer.Services.Automation;
using Stashographer.Services.Ai;
using Stashographer.Services.Config;
using Stashographer.Services.Images;
using Stashographer.Services.Inventory;
using Stashographer.Services.Intake;
using Stashographer.Services.Lookup;
using Stashographer.Services.Security;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// --- Razor / Blazor + MudBlazor -----------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddMudServices();
builder.Services.Configure<AgentFeatureOptions>(
    builder.Configuration.GetSection(AgentFeatureOptions.SectionName));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "stashographer.admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.LoginPath = "/admin/login";
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = async context =>
        {
            var validator = context.HttpContext.RequestServices.GetRequiredService<AdminPasswordValidator>();
            if (!validator.IsCurrent(context.Principal))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync();
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();
builder.Services.AddDataProtection()
    .SetApplicationName("Stashographer")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(
        builder.Configuration["Stashographer:DataProtectionKeysPath"] ?? "App_Data/keys",
        builder.Environment.ContentRootPath)));
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("admin-login", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
builder.Services.AddSingleton<AdminPasswordValidator>();
builder.Services.AddSingleton(TimeProvider.System);

// --- Persistence: Dapper over SQLite (provider seam for Postgres in phase 2) ---
DapperConfig.Register();
var connectionString = builder.Configuration.GetConnectionString("Default")
                       ?? "Data Source=stashographer.db";
builder.Services.AddSingleton<IDbConnectionFactory>(new SqliteConnectionFactory(connectionString));
builder.Services.AddSingleton<MigrationRunner>();

// --- Lookup providers + router ------------------------------------------------
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<OpenFoodFactsProvider>(c =>
{
    c.BaseAddress = new Uri("https://world.openfoodfacts.org/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashographer/1.0 (household inventory app)");
});
builder.Services.AddHttpClient<OpenLibraryProvider>(c =>
{
    c.BaseAddress = new Uri("https://openlibrary.org/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashographer/1.0 (household inventory app)");
});
builder.Services.AddScoped<ILookupRouter, LookupRouter>();
builder.Services.AddSingleton<BarcodeImageDecoder>();

// --- Domain services ----------------------------------------------------------
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<AttributeNameService>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<ContainerService>();
builder.Services.AddScoped<QuickLinksService>();
builder.Services.AddScoped<BomService>();
builder.Services.AddScoped<MealPlanService>();
builder.Services.AddScoped<ItemDraftState>();
builder.Services.AddScoped<SampleDataSeeder>();
builder.Services.AddScoped<AgentAccessService>();
builder.Services.AddScoped<AutomationOperations>();

// --- Image storage ------------------------------------------------------------
var imageOptions = builder.Configuration.GetSection(ImageOptions.SectionName).Get<ImageOptions>() ?? new ImageOptions();
builder.Services.AddSingleton(imageOptions);
builder.Services.AddHttpClient(nameof(ImageService), c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashographer/1.0 (household inventory app)");
    c.Timeout = TimeSpan.FromSeconds(15); // remote image fetches must not hang item saves
});
builder.Services.AddScoped<ImageService>();

// --- Sample/test data (development) -------------------------------------------
var sampleData = builder.Configuration.GetSection(SampleDataOptions.SectionName).Get<SampleDataOptions>()
                 ?? new SampleDataOptions();

// --- AI enrichment (optional, OpenAI-protocol) --------------------------------
// Configuration/env supply the initial defaults; settings saved from the UI (stored in the
// DB) take precedence and are applied at runtime via the client provider — no restart needed.
var aiOptions = builder.Configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
builder.Services.AddSingleton<IAiClientProvider, OpenAiClientProvider>();
builder.Services.AddScoped<IAiEnrichmentService, OpenAiEnrichmentService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<PhotoIntakeService>();
builder.Services.AddSingleton<IntakeQueueSignal>();
builder.Services.AddScoped<IntakeQueueService>();
builder.Services.AddHostedService<IntakeQueueWorker>();
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<StashographerMcpTools>();

var app = builder.Build();

// Fail fast instead of exposing an unprotected configuration page after a deployment mistake.
_ = app.Services.GetRequiredService<AdminPasswordValidator>();

// --- Apply hand-written SQL migrations at startup -----------------------------
await app.Services.GetRequiredService<MigrationRunner>().MigrateAsync();

// --- Activate AI: DB-saved settings win over configuration defaults -----------
using (var aiScope = app.Services.CreateScope())
{
    var stored = await aiScope.ServiceProvider.GetRequiredService<SettingsService>().GetAiOptionsAsync();
    app.Services.GetRequiredService<IAiClientProvider>().Reconfigure(stored ?? aiOptions);
}

// --- Optionally populate sample/test data -------------------------------------
if (sampleData.Enabled)
{
    using var seedScope = app.Services.CreateScope();
    await seedScope.ServiceProvider.GetRequiredService<SampleDataSeeder>().SeedAsync(sampleData.Reset);
}

// --- HTTP pipeline ------------------------------------------------------------
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AgentAccessMiddleware>();
app.UseAntiforgery();
app.UseRateLimiter();

app.MapPost("/auth/login", async (
    HttpContext context,
    IAntiforgery antiforgery,
    AdminPasswordValidator passwords) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var password = form["password"].ToString();
    var returnUrl = NormalizeReturnUrl(form["returnUrl"].ToString());

    if (!passwords.IsValid(password))
        return Results.LocalRedirect(
            $"/admin/login?invalid=true&returnUrl={Uri.EscapeDataString(returnUrl)}");

    await context.SignInAsync(passwords.CreatePrincipal());
    return Results.LocalRedirect(returnUrl);
}).RequireRateLimiting("admin-login");

app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    await antiforgery.ValidateRequestAsync(context);
    await context.SignOutAsync();
    return Results.LocalRedirect("/");
}).RequireAuthorization();

app.MapAutomationApi();
app.MapMcp("/mcp").DisableAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Container liveness probe (Docker HEALTHCHECK / compose). Deliberately does not touch
// the database: migrations already ran at startup, so a DB fault here would be a reason
// to look at logs, not to have the orchestrator restart-loop the container.
app.MapGet("/health", () => Results.Ok("healthy"));

// Image serving: /img/{id} for the original, /img/{id}?w=240 for an on-demand thumbnail.
// Image content for a given id is immutable (a new upload gets a new id), so cache hard.
app.MapGet("/img/{id:int}", async (int id, int? w, ImageService images, HttpContext ctx, CancellationToken ct) =>
{
    // Cache hard only on success — an immutable-cached 404 would stick for a year.
    void CacheForever() => ctx.Response.Headers.CacheControl = "public,max-age=31536000,immutable";

    if (w is > 0)
    {
        var thumb = await images.GetThumbnailAsync(id, Math.Clamp(w.Value, 16, 2000), ct);
        if (thumb is null) return Results.NotFound();
        CacheForever();
        return Results.Bytes(thumb.Value.Bytes, thumb.Value.ContentType);
    }

    var image = await images.GetAsync(id, ct);
    var original = image is null ? null : images.OpenOriginal(image);
    if (original is null) return Results.NotFound();
    CacheForever();
    return Results.Stream(original.Value.Stream, original.Value.ContentType);
});

app.Run();

static string NormalizeReturnUrl(string? value)
{
    if (string.IsNullOrWhiteSpace(value)
        || !value.StartsWith("/", StringComparison.Ordinal)
        || value.StartsWith("//", StringComparison.Ordinal))
        return "/settings";

    return value;
}

/// <summary>Exposed so integration tests can reference the app's entry assembly.</summary>
public partial class Program;
