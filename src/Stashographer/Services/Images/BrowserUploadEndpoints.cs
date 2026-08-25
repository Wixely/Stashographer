using Microsoft.AspNetCore.Antiforgery;

namespace Stashographer.Services.Images;

public static class BrowserUploadEndpoints
{
    private const long MaximumRequestBytes = 22L * 1024 * 1024;

    public static RouteGroupBuilder MapBrowserUploads(this IEndpointRouteBuilder endpoints)
    {
        var uploads = endpoints.MapGroup("/browser-uploads")
            .RequireRateLimiting("browser-uploads");
        uploads.MapGet("/antiforgery-token", GetAntiforgeryToken);
        uploads.MapPost(string.Empty, UploadAsync);
        uploads.MapGet("/{token}", GetAsync);
        return uploads;
    }

    private static IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
    {
        context.Response.Headers.CacheControl = "no-store";
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new { token = tokens.RequestToken });
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        BrowserUploadService uploads,
        CancellationToken ct)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            if (context.Request.ContentLength is > MaximumRequestBytes)
                return Results.BadRequest(new { error = "The image is too large." });
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Send multipart/form-data with a photo field." });

            var form = await context.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("photo") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "A non-empty image is required." });
            if (file.Length > 20L * 1024 * 1024)
                return Results.BadRequest(new { error = "The image is too large." });
            if (!Enum.TryParse<BrowserUploadKind>(
                    form["kind"].ToString(), ignoreCase: true, out var kind))
                return Results.BadRequest(new { error = "The browser upload action is invalid." });

            var multipleItems = !bool.TryParse(
                form["multipleItems"].ToString(), out var parsedMultiple) || parsedMultiple;
            await using var content = file.OpenReadStream();
            var result = await uploads.ProcessAsync(
                form["token"].ToString(),
                kind,
                content,
                file.ContentType,
                file.FileName,
                multipleItems,
                ct);
            return Results.Ok(result);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new
            {
                error = "The upload authorization expired. The selected image will retry automatically.",
                retryable = true
            });
        }
        catch (BrowserUploadInProgressException)
        {
            return Results.Conflict(new { pending = true });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException
                                       or InvalidOperationException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetAsync(
        string token, BrowserUploadService uploads, CancellationToken ct)
    {
        try
        {
            var result = await uploads.GetCompletedAsync(token, ct);
            if (result is not null) return Results.Ok(result);
            return await uploads.ExistsAsync(token, ct)
                ? Results.Accepted(value: new { pending = true })
                : Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
