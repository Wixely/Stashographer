using System.ClientModel;
using Microsoft.Extensions.AI;
using MudBlazor.Services;
using OpenAI;
using Stashographer.Components;
using Stashographer.Data;
using Stashographer.Data.Migrations;
using Stashographer.Services.Ai;
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
builder.Services.AddScoped<ItemDraftState>();
builder.Services.AddScoped<SampleDataSeeder>();

// --- Sample/test data (development) -------------------------------------------
var sampleData = builder.Configuration.GetSection(SampleDataOptions.SectionName).Get<SampleDataOptions>()
                 ?? new SampleDataOptions();

// --- AI enrichment (optional, OpenAI-protocol) --------------------------------
var aiOptions = builder.Configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
builder.Services.AddSingleton(aiOptions);
if (aiOptions.IsConfigured)
{
    var clientOptions = new OpenAIClientOptions();
    if (!string.IsNullOrWhiteSpace(aiOptions.Endpoint))
        clientOptions.Endpoint = new Uri(aiOptions.Endpoint);

    var openAiClient = new OpenAIClient(new ApiKeyCredential(aiOptions.ApiKey!), clientOptions);
    builder.Services.AddChatClient(openAiClient.GetChatClient(aiOptions.Model).AsIChatClient());
    builder.Services.AddScoped<IAiEnrichmentService, OpenAiEnrichmentService>();
}
else
{
    builder.Services.AddSingleton<IAiEnrichmentService, NullAiEnrichmentService>();
}

var app = builder.Build();

// --- Apply hand-written SQL migrations at startup -----------------------------
await app.Services.GetRequiredService<MigrationRunner>().MigrateAsync();

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

app.Run();

/// <summary>Exposed so integration tests can reference the app's entry assembly.</summary>
public partial class Program;
