using Dapper;
using Stashographer.Data;
using Stashographer.Services.Intake;
using Stashographer.Services.Lookup;

namespace Stashographer.Services.Images;

/// <summary>
/// Completes browser-owned HTTP uploads idempotently. This service deliberately sits outside
/// the Blazor circuit: queued captures are durable before the browser receives its receipt.
/// </summary>
public sealed class BrowserUploadService(
    IDbConnectionFactory db,
    ImageService images,
    IntakeQueueService intake,
    BarcodeImageDecoder barcodes)
{
    private const int Processing = 0;
    private const int Complete = 1;
    private static readonly TimeSpan StaleClaimAge = TimeSpan.FromMinutes(5);

    public async Task<BrowserUploadResult> ProcessAsync(
        string token,
        BrowserUploadKind kind,
        Stream content,
        string? contentType,
        string? originalName,
        bool multipleItems,
        CancellationToken ct = default)
    {
        token = ValidateToken(token);
        var claim = await ClaimAsync(token, kind, ct);
        if (claim.Result is not null) return claim.Result;
        if (!claim.Acquired) throw new BrowserUploadInProgressException();

        try
        {
            var result = kind switch
            {
                BrowserUploadKind.Image => await StoreImageAsync(
                    token, kind, content, contentType, originalName, ct),
                BrowserUploadKind.QueuedPhoto => await QueuePhotoAsync(
                    token, content, contentType, originalName, multipleItems, ct),
                BrowserUploadKind.QueuedReceipt => await QueueReceiptAsync(
                    token, content, contentType, originalName, ct),
                BrowserUploadKind.Barcode => await DecodeBarcodeAsync(
                    token, kind, content, ct),
                BrowserUploadKind.QueuedBarcode => await QueueBarcodeAsync(
                    token, content, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            await CompleteAsync(result, ct);
            return result;
        }
        catch
        {
            await ReleaseAsync(token);
            throw;
        }
    }

    public async Task<BrowserUploadResult?> GetCompletedAsync(
        string token, CancellationToken ct = default)
    {
        token = ValidateToken(token);
        using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<UploadRow>("""
            SELECT Token, Kind, Status, ImageId, QueueItemId, Code, CreatedAt, CompletedAt
            FROM BrowserUploads WHERE Token = @token;
            """, new { token });
        return row?.Status == Complete ? Map(row) : null;
    }

    public async Task<bool> ExistsAsync(string token, CancellationToken ct = default)
    {
        token = ValidateToken(token);
        using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM BrowserUploads WHERE Token = @token;", new { token }) > 0;
    }

    private async Task<BrowserUploadResult> StoreImageAsync(
        string token, BrowserUploadKind kind, Stream content, string? contentType,
        string? originalName, CancellationToken ct)
    {
        var image = await images.SaveAsync(content, contentType, originalName, null, ct);
        return new BrowserUploadResult(token, kind, image.Id, null, null);
    }

    private async Task<BrowserUploadResult> QueuePhotoAsync(
        string token, Stream content, string? contentType, string? originalName,
        bool multipleItems, CancellationToken ct)
    {
        var queued = await intake.EnqueuePhotoFromBrowserAsync(
            content, contentType ?? "application/octet-stream", originalName,
            multipleItems, token, ct);
        return new BrowserUploadResult(token, BrowserUploadKind.QueuedPhoto,
            queued.ImageId, queued.Id, null);
    }

    private async Task<BrowserUploadResult> QueueReceiptAsync(
        string token, Stream content, string? contentType, string? originalName,
        CancellationToken ct)
    {
        var queued = await intake.EnqueueReceiptFromBrowserAsync(
            content, contentType ?? "application/octet-stream", originalName, token, ct);
        return new BrowserUploadResult(token, BrowserUploadKind.QueuedReceipt,
            queued.ImageId, queued.Id, null);
    }

    private async Task<BrowserUploadResult> DecodeBarcodeAsync(
        string token, BrowserUploadKind kind, Stream content, CancellationToken ct)
    {
        var code = await barcodes.DecodeAsync(content, ct);
        return new BrowserUploadResult(token, kind, null, null, code);
    }

    private async Task<BrowserUploadResult> QueueBarcodeAsync(
        string token, Stream content, CancellationToken ct)
    {
        var code = await barcodes.DecodeAsync(content, ct);
        if (string.IsNullOrWhiteSpace(code))
            return new BrowserUploadResult(
                token, BrowserUploadKind.QueuedBarcode, null, null, null);
        var queued = await intake.EnqueueBarcodeFromBrowserAsync(code, token, ct);
        return new BrowserUploadResult(
            token, BrowserUploadKind.QueuedBarcode, null, queued.Id, code);
    }

    private async Task<UploadClaim> ClaimAsync(
        string token, BrowserUploadKind kind, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var row = await conn.QuerySingleOrDefaultAsync<UploadRow>("""
            SELECT Token, Kind, Status, ImageId, QueueItemId, Code, CreatedAt, CompletedAt
            FROM BrowserUploads WHERE Token = @token;
            """, new { token }, tx);
        if (row is not null)
        {
            if (row.Kind != (int)kind)
                throw new InvalidOperationException("The upload token was already used for another action.");
            if (row.Status == Complete)
            {
                tx.Commit();
                return new UploadClaim(false, Map(row));
            }
            if (DateTimeOffset.Parse(row.CreatedAt) > DateTimeOffset.UtcNow - StaleClaimAge)
            {
                tx.Commit();
                return new UploadClaim(false, null);
            }
            await conn.ExecuteAsync(
                "DELETE FROM BrowserUploads WHERE Token = @token AND Status = @processing;",
                new { token, processing = Processing }, tx);
        }

        await conn.ExecuteAsync("""
            INSERT INTO BrowserUploads (Token, Kind, Status, CreatedAt)
            VALUES (@token, @kind, @processing, @createdAt);
            """, new
        {
            token,
            kind = (int)kind,
            processing = Processing,
            createdAt = DateTimeOffset.UtcNow.ToString("O")
        }, tx);
        tx.Commit();
        return new UploadClaim(true, null);
    }

    private async Task CompleteAsync(BrowserUploadResult result, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        var changed = await conn.ExecuteAsync("""
            UPDATE BrowserUploads
            SET Status = @complete, ImageId = @ImageId, QueueItemId = @QueueItemId,
                Code = @Code, CompletedAt = @completedAt
            WHERE Token = @Token AND Status = @processing;
            """, new
        {
            result.Token,
            result.ImageId,
            result.QueueItemId,
            result.Code,
            complete = Complete,
            processing = Processing,
            completedAt = DateTimeOffset.UtcNow.ToString("O")
        });
        if (changed != 1)
            throw new InvalidOperationException("The upload receipt could not be completed.");
    }

    private async Task ReleaseAsync(string token)
    {
        try
        {
            using var conn = await db.OpenAsync();
            await conn.ExecuteAsync(
                "DELETE FROM BrowserUploads WHERE Token = @token AND Status = @processing;",
                new { token, processing = Processing });
        }
        catch
        {
            // Keep the original upload error. A stale processing claim is reclaimable later.
        }
    }

    private static string ValidateToken(string? token)
    {
        token = token?.Trim() ?? string.Empty;
        if (!Guid.TryParse(token, out _))
            throw new ArgumentException("The browser upload token is invalid.", nameof(token));
        return token;
    }

    private static BrowserUploadResult Map(UploadRow row) => new(
        row.Token,
        (BrowserUploadKind)row.Kind,
        row.ImageId,
        row.QueueItemId,
        row.Code);

    private sealed record UploadClaim(bool Acquired, BrowserUploadResult? Result);

    private sealed class UploadRow
    {
        public string Token { get; set; } = string.Empty;
        public int Kind { get; set; }
        public int Status { get; set; }
        public int? ImageId { get; set; }
        public int? QueueItemId { get; set; }
        public string? Code { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? CompletedAt { get; set; }
    }
}
