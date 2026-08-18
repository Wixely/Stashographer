using MudBlazor.Services;
using Stashographer.Components;
using Stashographer.Data;
using Stashographer.Data.Migrations;
using Stashographer.Services.Ai;
using Stashographer.Services.Config;
using Stashographer.Services.Images;
using Stashographer.Services.Inventory;
using Stashographer.Services.Lookup;

var builder = WebApplication.CreateBuilder(args);

// --- Razor / Blazor + MudBlazor -----------------------------------------------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

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

// --- Domain services ----------------------------------------------------------
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<CheckoutService>();
builder.Services.AddScoped<ContainerService>();
builder.Services.AddScoped<QuickLinksService>();
builder.Services.AddScoped<ItemDraftState>();
builder.Services.AddScoped<SampleDataSeeder>();

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

var app = builder.Build();

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
app.UseAntiforgery();

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

/// <summary>Exposed so integration tests can reference the app's entry assembly.</summary>
public partial class Program;
