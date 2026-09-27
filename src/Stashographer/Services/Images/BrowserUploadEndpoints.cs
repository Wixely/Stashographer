using System.Security.Claims;
using DnaX.Uploads;
using Microsoft.AspNetCore.Antiforgery;

namespace Stashographer.Services.Images;

/// <summary>
/// Converts a completed DNAX transport session into Stashographer's durable image/queue
/// receipt. Transport completion and business completion are separately idempotent so either
/// response can be lost without duplicating an image or queue item.
/// </summary>
public static class BrowserUploadEndpoints
{
    private const long MaximumLegacyRequestBytes = 22L * 1024 * 1024;

    public static RouteGroupBuilder MapBrowserUploads(this IEndpointRouteBuilder endpoints)
    {
        var uploads = endpoints.MapGroup("/browser-uploads")
            .RequireAuthorization(UploadOwnerAuthentication.Policy)
            .RequireRateLimiting("browser-uploads");
        uploads.MapGet("/antiforgery-token", GetAntiforgeryToken);
        // One-release compatibility path for tabs loaded before the DNAX rollout.
        uploads.MapPost(string.Empty, LegacyUploadAsync);
        uploads.MapPost("/{id:guid}/complete", CompleteAsync);
        uploads.MapGet("/{token}", GetAsync);
        return uploads;
    }

    private static async Task<IResult> LegacyUploadAsync(
        HttpContext context,
        IAntiforgery antiforgery,
        BrowserUploadService uploads,
        CancellationToken ct)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            if (context.Request.ContentLength is > MaximumLegacyRequestBytes)
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
            return Results.Conflict(new { pending = true, retryable = true });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException
                                       or InvalidOperationException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery)
    {
        context.Response.Headers.CacheControl = "no-store";
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new { token = tokens.RequestToken });
    }

    private static async Task<IResult> CompleteAsync(
        Guid id,
        bool multipleItems,
        HttpContext context,
        IAntiforgery antiforgery,
        DiskUploadStore transport,
        BrowserUploadService uploads,
        CancellationToken ct)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            var owner = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new UploadException(401, "A stable upload owner is required.");
            var status = transport.Get(owner, id);
            if (!Enum.TryParse<BrowserUploadKind>(status.Profile, out var kind))
                return Results.BadRequest(new { error = "The upload profile is not supported." });

            BrowserUploadResult? result = null;
            await transport.ReadCompletedAsync(owner, id, async (content, cancellationToken) =>
            {
                result = await uploads.ProcessAsync(
                    id.ToString(),
                    kind,
                    content,
                    ContentTypeFor(status.FileName),
                    status.FileName,
                    multipleItems,
                    cancellationToken);
            }, ct);
            return Results.Ok(result);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(new
            {
                error = "The upload authorization expired. Completion will retry automatically.",
                retryable = true
            });
        }
        catch (BrowserUploadInProgressException)
        {
            return Results.Conflict(new { pending = true, retryable = true });
        }
        catch (UploadException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
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

    private static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
}
