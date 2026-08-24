namespace Stashographer.Services.Automation;

public static class AutomationApiEndpoints
{
    public static RouteGroupBuilder MapAutomationApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");
        api.MapGet(string.Empty, () => new AutomationIdentity(
            "Stashographer API",
            "v1",
            ["inventory", "item-kinds", "places", "intake"],
            "Automation can propose queue drafts; a user must accept them in Intake Queue."));
        api.MapGet("/inventory", (
            string? search,
            int? itemKindId,
            int? locationId,
            int? containerId,
            int? limit,
            AutomationOperations operations,
            CancellationToken ct) => operations.SearchInventoryAsync(
                search, itemKindId, locationId, containerId, limit ?? 100, ct));
        api.MapGet("/inventory/{id:int}", (int id, AutomationOperations operations, CancellationToken ct) =>
            ExecuteAsync(() => operations.GetItemAsync(id, ct)));
        api.MapGet("/item-kinds", (AutomationOperations operations, CancellationToken ct) =>
            operations.ListItemKindsAsync(ct));
        api.MapGet("/places", (AutomationOperations operations, CancellationToken ct) =>
            operations.ListPlacesAsync(ct));
        api.MapGet("/intake", (AutomationOperations operations, CancellationToken ct) =>
            operations.ListIntakeQueueAsync(ct));
        api.MapGet("/intake/{id:int}", (int id, AutomationOperations operations, CancellationToken ct) =>
            ExecuteAsync(() => operations.GetIntakeItemAsync(id, ct)));
        api.MapPost("/intake/barcodes", (BarcodeIntakeRequest request, AutomationOperations operations, CancellationToken ct) =>
            ExecuteCreatedAsync(() => operations.QueueBarcodeAsync(request.Code, ct)));
        api.MapPost("/intake/items", (ItemDraftRequest request, AutomationOperations operations, CancellationToken ct) =>
            ExecuteCreatedAsync(() => operations.QueueItemDraftAsync(request, ct)));
        api.MapPut("/intake/{id:int}/draft", (int id, ItemDraftRequest request, AutomationOperations operations, CancellationToken ct) =>
            ExecuteAsync(() => operations.UpdateIntakeDraftAsync(id, request, ct)));
        api.MapPost("/intake/session", (AutomationOperations operations, CancellationToken ct) =>
            operations.StartIntakeSessionAsync(ct));
        api.MapPost("/intake/photos", QueuePhotoAsync).DisableAntiforgery();
        return api;
    }

    private static async Task<IResult> QueuePhotoAsync(
        HttpRequest request,
        bool? multipleItems,
        AutomationOperations operations,
        CancellationToken ct)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "Send multipart/form-data with a photo field." });
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("photo") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "A non-empty photo is required." });

        try
        {
            await using var content = file.OpenReadStream();
            var queued = await operations.QueuePhotoAsync(
                content,
                file.ContentType,
                file.FileName,
                multipleItems ?? true,
                ct);
            return Results.Created($"/api/v1/intake/{queued.Id}", queued);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ExecuteAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(await operation());
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> ExecuteCreatedAsync(
        Func<Task<AutomationQueueItem>> operation)
    {
        try
        {
            var queued = await operation();
            return Results.Created($"/api/v1/intake/{queued.Id}", queued);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
