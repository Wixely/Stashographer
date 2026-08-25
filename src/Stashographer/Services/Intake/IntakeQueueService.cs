using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Stashographer.Data;
using Stashographer.Data.Entities;
using Stashographer.Services.Ai;
using Stashographer.Services.Config;
using Stashographer.Services.Images;
using Stashographer.Services.Inventory;
using Stashographer.Services.Lookup;

namespace Stashographer.Services.Intake;

public record RememberedDestinations(int? LocationId, int? ContainerId);

public record IntakeQueueCounts(int Waiting, int Processing, int Ready, int Failed, int Completed);

public record ReceiptApplied(int MatchedLines, int MatchedItems);

/// <summary>
/// Durable capture queue. Enqueue operations only persist input; lookup/model work happens
/// later, preserving the fast capture loop used by phones and keyboard-wedge scanners.
/// </summary>
public class IntakeQueueService(
    IDbConnectionFactory db,
    ImageService images,
    ILookupRouter lookup,
    PhotoIntakeService photoIntake,
    InventoryService inventory,
    IntakeQueueSignal signal,
    ILogger<IntakeQueueService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<IntakeQueueItem> EnqueueBarcodeAsync(string code, CancellationToken ct = default) =>
        EnqueueBarcodeCoreAsync(code, null, null, ct);

    public Task<IntakeQueueItem> EnqueueBarcodeFromBrowserAsync(
        string code, string browserUploadToken, CancellationToken ct = default) =>
        EnqueueBarcodeCoreAsync(code, browserUploadToken, null, ct);

    public Task<IntakeQueueItem> EnqueueLiveBarcodeAsync(
        string code, TimeSpan holdFor, CancellationToken ct = default) =>
        EnqueueBarcodeCoreAsync(code, null, DateTimeOffset.UtcNow.Add(holdFor), ct);

    private async Task<IntakeQueueItem> EnqueueBarcodeCoreAsync(
        string code, string? browserUploadToken, DateTimeOffset? liveCaptureHoldUntil,
        CancellationToken ct)
    {
        code = code.Trim();
        if (code.Length == 0) throw new ArgumentException("A barcode or ISBN is required.", nameof(code));
        if (browserUploadToken is not null
            && await GetByBrowserUploadTokenAsync(browserUploadToken, ct) is { } existing)
            return existing;

        var item = new IntakeQueueItem
        {
            SessionId = await GetOrCreateActiveSessionIdAsync(ct),
            SourceType = IntakeSourceType.Barcode,
            SourceCode = code,
            CaptureQuantity = 1,
            LiveCaptureHoldUntil = liveCaptureHoldUntil,
            BrowserUploadToken = browserUploadToken,
            Status = IntakeQueueStatus.Pending,
            Draft = new Item { Name = string.Empty, Code = code, ItemKindId = 7 },
            CreatedAt = DateTimeOffset.UtcNow
        };
        item.Id = await InsertIdempotentlyAsync(item, ct);
        signal.Pulse();
        return item;
    }

    public Task<IntakeQueueItem> EnqueuePhotoAsync(
        Stream content, string mediaType, string? originalName, bool multipleItems = true,
        CancellationToken ct = default) =>
        EnqueuePhotoCoreAsync(content, mediaType, originalName, multipleItems, null, ct);

    public Task<IntakeQueueItem> EnqueuePhotoFromBrowserAsync(
        Stream content, string mediaType, string? originalName, bool multipleItems,
        string browserUploadToken, CancellationToken ct = default) =>
        EnqueuePhotoCoreAsync(
            content, mediaType, originalName, multipleItems, browserUploadToken, ct);

    private async Task<IntakeQueueItem> EnqueuePhotoCoreAsync(
        Stream content, string mediaType, string? originalName, bool multipleItems,
        string? browserUploadToken, CancellationToken ct)
    {
        if (browserUploadToken is not null
            && await GetByBrowserUploadTokenAsync(browserUploadToken, ct) is { } existing)
            return existing;
        var stored = await images.SaveAsync(content, mediaType, originalName, null, ct);
        var item = new IntakeQueueItem
        {
            SessionId = await GetOrCreateActiveSessionIdAsync(ct),
            SourceType = IntakeSourceType.Photo,
            BrowserUploadToken = browserUploadToken,
            ImageId = stored.Id,
            IsMultiPhoto = multipleItems,
            Status = IntakeQueueStatus.Pending,
            Draft = new Item { Name = string.Empty, ItemKindId = 7, ImageId = stored.Id },
            CreatedAt = DateTimeOffset.UtcNow
        };
        item.Id = await InsertIdempotentlyAsync(item, ct);
        signal.Pulse();
        return item;
    }

    /// <summary>
    /// Queues purchase evidence as an explicit override for earlier items in the active session.
    /// Its processing and acceptance never change inventory quantities.
    /// </summary>
    public Task<IntakeQueueItem> EnqueueReceiptAsync(
        Stream content, string mediaType, string? originalName, CancellationToken ct = default) =>
        EnqueueReceiptCoreAsync(content, mediaType, originalName, null, ct);

    public Task<IntakeQueueItem> EnqueueReceiptFromBrowserAsync(
        Stream content, string mediaType, string? originalName, string browserUploadToken,
        CancellationToken ct = default) =>
        EnqueueReceiptCoreAsync(content, mediaType, originalName, browserUploadToken, ct);

    private async Task<IntakeQueueItem> EnqueueReceiptCoreAsync(
        Stream content, string mediaType, string? originalName, string? browserUploadToken,
        CancellationToken ct)
    {
        if (browserUploadToken is not null
            && await GetByBrowserUploadTokenAsync(browserUploadToken, ct) is { } existing)
            return existing;
        var stored = await images.SaveAsync(content, mediaType, originalName, null, ct);
        var item = new IntakeQueueItem
        {
            SessionId = await GetOrCreateActiveSessionIdAsync(ct),
            SourceType = IntakeSourceType.Receipt,
            SourceTypeOverride = true,
            BrowserUploadToken = browserUploadToken,
            ImageId = stored.Id,
            Status = IntakeQueueStatus.Pending,
            Draft = new Item { Name = string.Empty, ItemKindId = 7 },
            CreatedAt = DateTimeOffset.UtcNow
        };
        item.Id = await InsertIdempotentlyAsync(item, ct);
        signal.Pulse();
        return item;
    }

    public async Task UpdateLiveBarcodeQuantityAsync(
        int id, string code, int quantity, TimeSpan holdFor, CancellationToken ct = default)
    {
        code = code.Trim();
        if (quantity < 1) throw new ArgumentOutOfRangeException(nameof(quantity));
        using var conn = await db.OpenAsync(ct);
        var changed = await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems
            SET CaptureQuantity = @quantity,
                DraftJson = json_set(DraftJson, '$.quantity', @quantity),
                IncrementBy = @quantity,
                LiveCaptureHoldUntil = @holdUntil
            WHERE Id = @id AND SourceType = @barcode AND SourceCode = @code
              AND Status NOT IN (@accepted, @rejected);
            """, new
        {
            id,
            code,
            quantity,
            holdUntil = DateTimeOffset.UtcNow.Add(holdFor).ToString("O"),
            barcode = (int)IntakeSourceType.Barcode,
            accepted = (int)IntakeQueueStatus.Accepted,
            rejected = (int)IntakeQueueStatus.Rejected
        });
        if (changed == 0)
            throw new InvalidOperationException("That barcode capture is no longer available to update.");
        signal.Pulse();
    }

    public async Task FinalizeLiveBarcodeAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems SET LiveCaptureHoldUntil = NULL
            WHERE Id = @id AND SourceType = @barcode
              AND Status NOT IN (@accepted, @rejected);
            """, new
        {
            id,
            barcode = (int)IntakeSourceType.Barcode,
            accepted = (int)IntakeQueueStatus.Accepted,
            rejected = (int)IntakeQueueStatus.Rejected
        });
        signal.Pulse();
    }

    /// <summary>
    /// Queues an already stored image that AI classified as purchase evidence. Unlike the
    /// receipt/order override, this classification may still be corrected by the user.
    /// </summary>
    public async Task<IntakeQueueItem> EnqueueStoredPurchaseEvidenceAsync(
        int imageId, string? browserUploadToken = null, CancellationToken ct = default)
    {
        if (browserUploadToken is not null
            && await GetByBrowserUploadTokenAsync(browserUploadToken, ct) is { } existing)
            return existing;
        if (await images.GetAsync(imageId, ct) is null)
            throw new InvalidOperationException("The uploaded image no longer exists.");

        var item = new IntakeQueueItem
        {
            SessionId = await GetOrCreateActiveSessionIdAsync(ct),
            SourceType = IntakeSourceType.Receipt,
            SourceTypeOverride = false,
            BrowserUploadToken = browserUploadToken,
            ImageId = imageId,
            Status = IntakeQueueStatus.Pending,
            Draft = new Item { Name = string.Empty, ItemKindId = 7 },
            CreatedAt = DateTimeOffset.UtcNow
        };
        item.Id = await InsertIdempotentlyAsync(item, ct);
        signal.Pulse();
        return item;
    }

    /// <summary>
    /// Queues an automation/manual proposal directly for human review. It is never accepted
    /// automatically, even when background review requirements are disabled.
    /// </summary>
    public async Task<IntakeQueueItem> EnqueueDraftAsync(Item draft, CancellationToken ct = default)
    {
        draft.Name = draft.Name.Trim();
        if (draft.Name.Length == 0) throw new ArgumentException("An item name is required.", nameof(draft));
        if (draft.Quantity <= 0) throw new ArgumentException("Quantity must be greater than zero.", nameof(draft));
        draft.Id = 0;

        var item = new IntakeQueueItem
        {
            SessionId = await GetOrCreateActiveSessionIdAsync(ct),
            SourceType = IntakeSourceType.Manual,
            SourceCode = draft.Code,
            Status = IntakeQueueStatus.ReadyForReview,
            Draft = draft,
            ProposalAction = IntakeAction.CreateNew,
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow
        };
        item.Id = await InsertAsync(item, ct);
        signal.Pulse();
        return item;
    }

    public async Task<List<IntakeQueueItem>> GetOpenAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<QueueRow>(QueueSelect + " " + """
            WHERE q.Status NOT IN (@accepted, @rejected)
            ORDER BY q.Id;
            """, new { accepted = (int)IntakeQueueStatus.Accepted, rejected = (int)IntakeQueueStatus.Rejected });
        return rows.Select(Map).ToList();
    }

    public async Task<IntakeQueueItem?> GetAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<QueueRow>(QueueSelect + " WHERE q.Id = @id", new { id });
        return row is null ? null : Map(row);
    }

    public async Task<IntakeQueueCounts> GetCountsAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int Status, int Count)>(
            "SELECT Status, COUNT(*) AS Count FROM IntakeQueueItems GROUP BY Status");
        var counts = rows.ToDictionary(x => (IntakeQueueStatus)x.Status, x => x.Count);
        return new IntakeQueueCounts(
            counts.GetValueOrDefault(IntakeQueueStatus.Pending),
            counts.GetValueOrDefault(IntakeQueueStatus.Processing),
            counts.GetValueOrDefault(IntakeQueueStatus.ReadyForReview),
            counts.GetValueOrDefault(IntakeQueueStatus.Failed),
            counts.GetValueOrDefault(IntakeQueueStatus.Accepted) + counts.GetValueOrDefault(IntakeQueueStatus.Rejected));
    }

    public async Task<IntakeSession> GetCurrentSessionAsync(CancellationToken ct = default)
    {
        var id = await GetOrCreateActiveSessionIdAsync(ct);
        using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<SessionRow>(
            "SELECT Id, StartedAt, EndedAt FROM IntakeSessions WHERE Id = @id", new { id });
        return Map(row);
    }

    public async Task<IntakeSession> StartNewSessionAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await conn.ExecuteAsync("UPDATE IntakeSessions SET EndedAt = @now WHERE EndedAt IS NULL", new { now });
        var id = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO IntakeSessions (StartedAt) VALUES (@now);
            SELECT last_insert_rowid();
            """, new { now });
        return new IntakeSession(id, DateTimeOffset.Parse(now), null);
    }

    /// <summary>
    /// Finds the most recently accepted direct location and container independently for an
    /// intake session. Deriving this from accepted drafts keeps the shortcut durable without
    /// maintaining duplicate mutable state.
    /// </summary>
    public async Task<RememberedDestinations> GetRememberedDestinationsAsync(
        int sessionId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var drafts = await conn.QueryAsync<string>("""
            SELECT DraftJson FROM IntakeQueueItems
            WHERE SessionId = @sessionId AND Status = @accepted AND DraftJson IS NOT NULL
            ORDER BY ReviewedAt DESC, Id DESC;
            """, new { sessionId, accepted = (int)IntakeQueueStatus.Accepted });
        int? locationId = null;
        int? containerId = null;
        foreach (var json in drafts)
        {
            var draft = DeserializeDraft(json);
            locationId ??= draft.LocationId;
            containerId ??= draft.ContainerId;
            if (locationId is not null && containerId is not null) break;
        }
        return new RememberedDestinations(locationId, containerId);
    }

    /// <summary>Processes one specific capture. Returns false when another worker claimed it.</summary>
    public async Task<bool> ProcessAsync(
        int id, IntakeOptions options, bool aiEnabled, CancellationToken ct = default)
    {
        using (var conn = await db.OpenAsync(ct))
        {
            var changed = await conn.ExecuteAsync("""
                UPDATE IntakeQueueItems
                SET Status = @processing, ProcessingStartedAt = @now, Error = NULL
                WHERE Id = @id AND Status IN (@pending, @failed)
                  AND (LiveCaptureHoldUntil IS NULL OR LiveCaptureHoldUntil <= @now);
                """, new
            {
                id,
                processing = (int)IntakeQueueStatus.Processing,
                pending = (int)IntakeQueueStatus.Pending,
                failed = (int)IntakeQueueStatus.Failed,
                now = DateTimeOffset.UtcNow.ToString("O")
            });
            if (changed == 0) return false;
        }

        try
        {
            var queued = await GetAsync(id) ?? throw new InvalidOperationException("Queue item disappeared.");
            if (queued.SourceType == IntakeSourceType.Receipt)
            {
                if (!aiEnabled)
                    throw new InvalidOperationException(
                        "Configure an AI vision model to extract this receipt or order.");
                await ProcessReceiptAsync(queued, options.ContextItemCount, ct);
                return true;
            }
            List<ProcessedCapture> processed;
            if (queued.SourceType == IntakeSourceType.Barcode)
            {
                processed = new List<ProcessedCapture> { await ProcessBarcodeAsync(queued, ct) };
            }
            else if (queued.SourceType == IntakeSourceType.Photo)
            {
                if (!aiEnabled)
                    throw new InvalidOperationException("Configure an AI vision model, or enter this item manually.");
                var analysis = await photoIntake.AnalyzeStoredAsync(
                    queued.ImageId
                    ?? throw new InvalidOperationException("Queued photo has no stored image."), ct);
                if (!queued.SourceTypeOverride && analysis.IsPurchaseEvidence)
                {
                    await MarkAsPurchaseEvidenceAsync(queued.Id, ct);
                    queued.SourceType = IntakeSourceType.Receipt;
                    await ProcessReceiptAsync(queued, options.ContextItemCount, ct);
                    return true;
                }
                processed = await ProcessPhotoAsync(
                    queued, options.ContextItemCount, analysis, ct);
            }
            else
            {
                throw new InvalidOperationException("Manual drafts are already ready for review.");
            }

            var recentPlacement = await GetRecentDraftsAsync(queued, 1, ct);
            foreach (var capture in processed) ApplyRecentPlacement(capture.Draft, recentPlacement);

            var first = processed.FirstOrDefault()
                ?? throw new InvalidOperationException("No items were found in the queued photo.");
            await StoreProcessedAsync(id, first, ct);
            var processedIds = new List<(int Id, ProcessedCapture Capture)> { (id, first) };
            foreach (var additional in processed.Skip(1))
                processedIds.Add((await InsertProcessedAsync(queued, additional, ct), additional));

            if (!options.RequireReview)
            {
                foreach (var entry in processedIds.Where(x =>
                             !string.IsNullOrWhiteSpace(x.Capture.Draft.Name)
                             && x.Capture.Action is not (IntakeAction.ChooseCandidate or IntakeAction.AttachImage)))
                {
                    await AcceptAsync(entry.Id, entry.Capture.Draft,
                        entry.Capture.Action is IntakeAction.IncrementExisting or IntakeAction.CreateStockLot
                            ? entry.Capture.MatchedItemId
                            : null, ct);
                }
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            await ReturnToPendingAsync(id);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Intake queue item {QueueItemId} failed", id);
            await MarkFailedAsync(id, ex.Message);
            return true;
        }
    }

    /// <summary>Claims and processes the next capture allowed by the current policy.</summary>
    public async Task<bool> ProcessNextAsync(
        IntakeOptions options, bool aiEnabled, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var id = await conn.QueryFirstOrDefaultAsync<int?>("""
            SELECT Id FROM IntakeQueueItems
            WHERE Status = @pending
              AND (LiveCaptureHoldUntil IS NULL OR LiveCaptureHoldUntil <= @now)
              AND ((SourceType = @barcode AND @barcodes = 1)
                OR (SourceType IN (@photo, @receipt) AND @photos = 1 AND @ai = 1))
            ORDER BY Id LIMIT 1;
            """, new
        {
            pending = (int)IntakeQueueStatus.Pending,
            barcode = (int)IntakeSourceType.Barcode,
            photo = (int)IntakeSourceType.Photo,
            receipt = (int)IntakeSourceType.Receipt,
            barcodes = options.AutoProcessBarcodes ? 1 : 0,
            photos = options.AutoProcessPhotos ? 1 : 0,
            ai = aiEnabled ? 1 : 0,
            now = DateTimeOffset.UtcNow.ToString("O")
        });
        return id is not null && await ProcessAsync(id.Value, options, aiEnabled, ct);
    }

    /// <summary>Returns work interrupted by a previous process shutdown to the pending state.</summary>
    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems SET Status = @pending, ProcessingStartedAt = NULL,
                Error = 'Processing was interrupted and will be retried.'
            WHERE Status = @processing;
            """, new
        {
            pending = (int)IntakeQueueStatus.Pending,
            processing = (int)IntakeQueueStatus.Processing
        });
    }

    public async Task RetryAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems SET Status = @pending, Error = NULL, ReceiptJson = NULL,
                ProcessingStartedAt = NULL, ProcessedAt = NULL
            WHERE Id = @id AND Status = @failed;
            """, new { id, pending = (int)IntakeQueueStatus.Pending, failed = (int)IntakeQueueStatus.Failed });
        signal.Pulse();
    }

    /// <summary>
    /// Explicitly reclassifies an image and resets its generated data for a clean retry.
    /// The override is durable so AI cannot immediately undo the correction.
    /// </summary>
    public async Task ReclassifyImageAsync(
        int id, IntakeSourceType sourceType, CancellationToken ct = default)
    {
        if (sourceType is not (IntakeSourceType.Photo or IntakeSourceType.Receipt))
            throw new ArgumentOutOfRangeException(nameof(sourceType));
        var queued = await GetAsync(id, ct)
            ?? throw new InvalidOperationException("Queue item was not found.");
        if (queued.ImageId is null)
            throw new InvalidOperationException("This queue item has no image to reclassify.");
        if (queued.Status is IntakeQueueStatus.Accepted or IntakeQueueStatus.Rejected)
            throw new InvalidOperationException("Completed queue items cannot be reclassified.");
        if (queued.Status == IntakeQueueStatus.Processing)
            throw new InvalidOperationException("Wait for processing to finish before reclassifying this image.");

        var draft = new Item
        {
            Name = string.Empty,
            ItemKindId = 7,
            ImageId = sourceType == IntakeSourceType.Photo ? queued.ImageId : null
        };
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems
            SET SourceType = @sourceType, SourceTypeOverride = 1,
                Status = @pending, DraftJson = @draft, ReceiptJson = NULL,
                ProposalAction = NULL, MatchedItemId = NULL, MatchedItemName = NULL,
                MatchedQueueItemId = NULL, CaptureRelationship = NULL,
                RelationshipConfidence = NULL, RelationshipReason = NULL,
                SuggestedImageRole = NULL, IncrementBy = 1, AppliedItemId = NULL,
                Error = NULL, ProcessingStartedAt = NULL, ProcessedAt = NULL, ReviewedAt = NULL
            WHERE Id = @id;
            """, new
        {
            id,
            sourceType = (int)sourceType,
            pending = (int)IntakeQueueStatus.Pending,
            draft = JsonSerializer.Serialize(draft, Json)
        });
        signal.Pulse();
    }

    public async Task UpdateDraftAsync(int id, Item draft, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE IntakeQueueItems SET DraftJson = @json WHERE Id = @id AND Status NOT IN (@accepted, @rejected)",
            new
            {
                id,
                json = JsonSerializer.Serialize(draft, Json),
                accepted = (int)IntakeQueueStatus.Accepted,
                rejected = (int)IntakeQueueStatus.Rejected
            });
    }

    /// <summary>Lists same-session captures that a receipt line may be linked to.</summary>
    public async Task<List<ReceiptMatchCandidate>> GetReceiptCandidatesAsync(
        int receiptQueueItemId, int limit = 100, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 0, 100);
        if (limit == 0) return [];
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ReceiptCandidateRow>("""
            SELECT candidate.Id AS QueueItemId, candidate.AppliedItemId AS InventoryItemId,
                   candidate.DraftJson
            FROM IntakeQueueItems receipt
            JOIN IntakeQueueItems candidate
              ON candidate.SessionId = receipt.SessionId AND candidate.Id < receipt.Id
            WHERE receipt.Id = @receiptQueueItemId
              AND candidate.SourceType != @receipt
              AND candidate.DraftJson IS NOT NULL
              AND candidate.Status IN (@ready, @accepted)
            ORDER BY candidate.Id DESC LIMIT @limit;
            """, new
        {
            receiptQueueItemId,
            receipt = (int)IntakeSourceType.Receipt,
            ready = (int)IntakeQueueStatus.ReadyForReview,
            accepted = (int)IntakeQueueStatus.Accepted,
            limit
        });
        return rows.Select(row =>
        {
            var draft = DeserializeDraft(row.DraftJson);
            return new ReceiptMatchCandidate(
                row.QueueItemId,
                row.InventoryItemId,
                draft.Name,
                draft.Code,
                draft.Attributes);
        }).Where(candidate => !string.IsNullOrWhiteSpace(candidate.Name)).ToList();
    }

    /// <summary>
    /// Accepts reviewed receipt matches as provenance and shared images. This deliberately
    /// contains no inventory quantity update.
    /// </summary>
    public async Task<ReceiptApplied> AcceptReceiptAsync(
        int id, ReceiptExtraction receipt, CancellationToken ct = default)
    {
        var queued = await GetAsync(id, ct) ?? throw new InvalidOperationException("Queue item was not found.");
        if (queued.SourceType != IntakeSourceType.Receipt)
            throw new InvalidOperationException("This queue item is not a receipt.");
        if (queued.Status == IntakeQueueStatus.Accepted)
            throw new InvalidOperationException("This receipt was already accepted.");
        if (queued.Status == IntakeQueueStatus.Rejected)
            throw new InvalidOperationException("This receipt was rejected.");
        if (queued.Status != IntakeQueueStatus.ReadyForReview)
            throw new InvalidOperationException("Process this receipt before accepting it.");
        var imageId = queued.ImageId
            ?? throw new InvalidOperationException("This receipt has no stored image.");
        var selected = receipt.Lines.Where(line => line.Selected).ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Select at least one receipt line and inventory item.");

        receipt.Currency = string.IsNullOrWhiteSpace(receipt.Currency)
            ? null
            : SpecialAttributeCatalog.NormalizeCurrencyCode(receipt.Currency);
        var resolved = new List<(ReceiptLineSuggestion Line, int ItemId)>();
        foreach (var line in selected)
        {
            line.Description = line.Description.Trim();
            if (line.Description.Length == 0)
                throw new InvalidOperationException("Every selected receipt line needs a description.");
            if (line.Quantity <= 0 || line.UnitPrice < 0 || line.LineTotal < 0)
                throw new InvalidOperationException("Receipt quantities and prices cannot be negative.");
            var itemId = line.MatchedItemId;
            if (itemId is null && line.MatchedQueueItemId is { } matchedQueueId)
                itemId = await ResolveReceiptItemAsync(queued, matchedQueueId, ct);
            if (itemId is null)
                throw new InvalidOperationException(
                    $"Choose an inventory item for receipt line '{line.Description}'.");
            if (await inventory.GetAsync(itemId.Value, ct) is null)
                throw new InvalidOperationException("A selected inventory item no longer exists.");
            line.MatchedItemId = itemId;
            resolved.Add((line, itemId.Value));
        }

        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var (line, itemId) in resolved)
        {
            await conn.ExecuteAsync("""
                INSERT INTO ItemImages (ItemId, ImageId, Role, IsPrimary, SortOrder, CreatedAt)
                SELECT @itemId, @imageId, @role, 0,
                       COALESCE((SELECT MAX(SortOrder) + 1 FROM ItemImages WHERE ItemId = @itemId), 0),
                       @now
                ON CONFLICT(ItemId, ImageId) DO NOTHING;
                """, new
            {
                itemId,
                imageId,
                role = (int)ItemImageRole.Receipt,
                now
            }, tx);
            var purchaseParameters = new DynamicParameters();
            purchaseParameters.Add("queueItemId", id);
            purchaseParameters.Add("lineIndex", line.LineIndex);
            purchaseParameters.Add("itemId", itemId);
            purchaseParameters.Add("imageId", imageId);
            purchaseParameters.Add("merchant", Clean(receipt.Merchant));
            purchaseParameters.Add("purchasedOn", receipt.PurchaseDate?.ToString("yyyy-MM-dd"));
            purchaseParameters.Add("description", line.Description);
            purchaseParameters.Add("quantity", line.Quantity);
            purchaseParameters.Add("unitPrice", line.UnitPrice);
            purchaseParameters.Add("currency", receipt.Currency);
            purchaseParameters.Add("lineTotal", line.LineTotal);
            purchaseParameters.Add(
                "confidence",
                line.Confidence == MatchConfidence.None ? null : (int)line.Confidence);
            purchaseParameters.Add("now", now);
            await conn.ExecuteAsync("""
                INSERT INTO ItemPurchases
                    (QueueItemId, ReceiptLineIndex, ItemId, ImageId, Merchant, PurchasedOn,
                     Description, Quantity, UnitPrice, Currency, LineTotal, Confidence, CreatedAt)
                VALUES
                    (@queueItemId, @lineIndex, @itemId, @imageId, @merchant, @purchasedOn,
                     @description, @quantity, @unitPrice, @currency, @lineTotal, @confidence, @now);
                """, purchaseParameters, tx);
        }
        var changed = await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems
            SET Status = @accepted, ReceiptJson = @receiptJson, ReviewedAt = @now
            WHERE Id = @id AND Status NOT IN (@accepted, @rejected);
            """, new
        {
            id,
            accepted = (int)IntakeQueueStatus.Accepted,
            rejected = (int)IntakeQueueStatus.Rejected,
            receiptJson = JsonSerializer.Serialize(receipt, Json),
            now
        }, tx);
        if (changed != 1)
            throw new InvalidOperationException("This receipt was already reviewed.");
        tx.Commit();
        return new ReceiptApplied(resolved.Count, resolved.Select(entry => entry.ItemId).Distinct().Count());
    }

    public async Task<List<ItemPurchase>> GetPurchasesAsync(int itemId, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<ItemPurchase>("""
            SELECT Id, QueueItemId, ReceiptLineIndex, ItemId, ImageId, Merchant, PurchasedOn,
                   Description, Quantity, UnitPrice, Currency, LineTotal, Confidence, CreatedAt
            FROM ItemPurchases WHERE ItemId = @itemId
            ORDER BY PurchasedOn DESC, CreatedAt DESC, Id DESC;
            """, new { itemId });
        return rows.ToList();
    }

    public Task<IntakeApplied> AcceptAsync(
        int id, Item draft, int? matchedItemId, CancellationToken ct = default) =>
        AcceptCoreAsync(id, draft, matchedItemId, null, null, ct);

    public Task<IntakeApplied> AcceptImageAttachmentAsync(
        int id, Item draft, ItemImageRole imageRole, CancellationToken ct = default) =>
        AcceptCoreAsync(id, draft, null, imageRole, IntakeAction.AttachImage, ct);

    public Task<IntakeApplied> AcceptAdditionalCopyAsync(
        int id, Item draft, CancellationToken ct = default) =>
        AcceptCoreAsync(id, draft, null, null, IntakeAction.IncrementExisting, ct);

    private async Task<IntakeApplied> AcceptCoreAsync(
        int id, Item draft, int? matchedItemId, ItemImageRole? imageRole,
        IntakeAction? actionOverride, CancellationToken ct)
    {
        var queued = await GetAsync(id) ?? throw new InvalidOperationException("Queue item was not found.");
        if (queued.Status == IntakeQueueStatus.Accepted)
            throw new InvalidOperationException("This queue item was already accepted.");
        if (queued.Status == IntakeQueueStatus.Rejected)
            throw new InvalidOperationException("This queue item was rejected.");

        IntakeApplied applied;
        var incrementTargetId = matchedItemId;
        if (actionOverride == IntakeAction.IncrementExisting)
        {
            incrementTargetId ??= queued.MatchedItemId
                                  ?? await GetAppliedItemIdAsync(queued.MatchedQueueItemId, ct);
            if (incrementTargetId is null)
                throw new InvalidOperationException(
                    "Accept the earlier capture first, then mark this as another copy.");
        }
        var reviewedAction = actionOverride ?? queued.ProposalAction;
        if (reviewedAction == IntakeAction.AttachImage)
        {
            if (queued.MatchedQueueItemId is null && queued.MatchedItemId is null && matchedItemId is null)
                throw new InvalidOperationException("No earlier capture is available for this image.");
            var existingId = matchedItemId ?? queued.MatchedItemId
                ?? await GetAppliedItemIdAsync(queued.MatchedQueueItemId, ct)
                ?? throw new InvalidOperationException(
                    "Accept the earlier capture first, then accept this additional view.");
            var existing = await inventory.GetAsync(existingId, ct)
                ?? throw new InvalidOperationException("The matched inventory item no longer exists.");
            if (MergeMissingMetadata(existing, draft))
                await inventory.SaveAsync(existing, ct);
            var imageId = queued.ImageId ?? draft.ImageId
                ?? throw new InvalidOperationException("This queue item has no stored image to attach.");
            await inventory.AttachImageAsync(
                existingId, imageId, imageRole ?? queued.SuggestedImageRole ?? ItemImageRole.Detail,
                ct: ct);
            applied = new IntakeApplied(IntakeAction.AttachImage, existingId, existing.Name, 0);
        }
        else if (reviewedAction == IntakeAction.CreateStockLot && incrementTargetId is { } lotTargetId)
        {
            var created = await inventory.CreateStockLotAsync(lotTargetId, draft, ct);
            applied = new IntakeApplied(
                IntakeAction.CreateStockLot, created.Id, created.Name, created.Quantity);
        }
        else if (incrementTargetId is { } existingId)
        {
            var existing = await inventory.GetAsync(existingId, ct)
                ?? throw new InvalidOperationException("The selected inventory item no longer exists.");
            if (InventoryService.RequiresSeparateStockLot(existing, draft))
            {
                var created = await inventory.CreateStockLotAsync(existingId, draft, ct);
                applied = new IntakeApplied(
                    IntakeAction.CreateStockLot, created.Id, created.Name, created.Quantity);
            }
            else
            {
                if (SpecialAttributeCatalog.MergeMissing(existing, draft))
                    await inventory.SaveAsync(existing, ct);
                var incrementBy = actionOverride == IntakeAction.IncrementExisting && queued.IncrementBy <= 0
                    ? Math.Max(1, draft.Quantity)
                    : queued.IncrementBy;
                await inventory.AdjustQuantityAsync(existingId, incrementBy, ct);
                if (draft.LocationId is not null || draft.ContainerId is not null)
                    await inventory.MoveItemsAsync([existingId], draft.LocationId, draft.ContainerId, ct);
                applied = new IntakeApplied(IntakeAction.IncrementExisting, existingId, existing.Name, incrementBy);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(draft.Name))
                throw new InvalidOperationException("Enter an item name before accepting it.");
            draft.Id = 0;
            var created = await inventory.SaveAsync(draft, ct);
            applied = new IntakeApplied(IntakeAction.CreateNew, created.Id, created.Name, created.Quantity);
        }

        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems
            SET Status = @accepted, DraftJson = @draft, AppliedItemId = @itemId, ReviewedAt = @now
            WHERE Id = @id;
            """, new
        {
            id,
            accepted = (int)IntakeQueueStatus.Accepted,
            draft = JsonSerializer.Serialize(draft, Json),
            itemId = applied.ItemId,
            now = DateTimeOffset.UtcNow.ToString("O")
        });
        return applied;
    }

    public async Task RejectAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems SET Status = @rejected, ReviewedAt = @now
            WHERE Id = @id AND Status NOT IN (@accepted, @rejected);
            """, new
        {
            id,
            rejected = (int)IntakeQueueStatus.Rejected,
            accepted = (int)IntakeQueueStatus.Accepted,
            now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private async Task<ProcessedCapture> ProcessBarcodeAsync(IntakeQueueItem queued, CancellationToken ct)
    {
        var code = queued.SourceCode ?? throw new InvalidOperationException("Queued barcode is empty.");
        var quantity = Math.Max(1, queued.CaptureQuantity);
        var result = await lookup.LookupAsync(code, ct);
        var draft = result.Found
            ? new Item
            {
                Name = result.Name ?? string.Empty,
                Code = result.Code,
                Description = result.Description,
                ThumbnailUrl = result.ThumbnailUrl,
                Attributes = new(result.Attributes),
                ItemKindId = KindId(result.SuggestedKind),
                Quantity = quantity
            }
            : new Item { Name = string.Empty, Code = code, ItemKindId = 7, Quantity = quantity };

        var candidates = await inventory.FindCandidatesAsync(draft.Name, code, ct: ct);
        var exact = candidates
            .Where(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return exact.Count switch
        {
            0 => new ProcessedCapture(draft, IntakeAction.CreateNew, null, null, quantity),
            1 => new ProcessedCapture(draft, IntakeAction.IncrementExisting, exact[0].Id, exact[0].Name, quantity),
            _ => new ProcessedCapture(draft, IntakeAction.ChooseCandidate, null, null, quantity)
        };
    }

    private async Task ProcessReceiptAsync(
        IntakeQueueItem queued, int contextCount, CancellationToken ct)
    {
        if (queued.ImageId is not { } imageId)
            throw new InvalidOperationException("Queued purchase evidence has no stored image.");
        var candidates = await GetReceiptCandidatesAsync(
            queued.Id, Math.Clamp(contextCount, 0, 25), ct);
        var receipt = await photoIntake.ExtractReceiptStoredAsync(imageId, candidates, ct)
            ?? throw new InvalidOperationException("The vision model could not read this receipt or order.");
        if (receipt.Lines.Count == 0)
            throw new InvalidOperationException("No purchased lines were found in this receipt or order.");

        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems
            SET Status = @ready, ReceiptJson = @receiptJson, ProcessedAt = @now, Error = NULL
            WHERE Id = @id;
            """, new
        {
            id = queued.Id,
            ready = (int)IntakeQueueStatus.ReadyForReview,
            receiptJson = JsonSerializer.Serialize(receipt, Json),
            now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private async Task MarkAsPurchaseEvidenceAsync(int id, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems
            SET SourceType = @receipt, ReceiptJson = NULL
            WHERE Id = @id AND SourceTypeOverride = 0;
            """, new { id, receipt = (int)IntakeSourceType.Receipt });
    }

    private async Task<List<ProcessedCapture>> ProcessPhotoAsync(
        IntakeQueueItem queued, int contextCount, CaptureAnalysis analysis,
        CancellationToken ct)
    {
        if (queued.ImageId is not { } imageId)
            throw new InvalidOperationException("Queued photo has no stored image.");

        var recentCount = Math.Clamp(contextCount, 0, 25);
        var recent = await GetRecentDraftsAsync(queued, recentCount, ct);
        var recentCaptures = await GetRecentPhotoCapturesAsync(queued, recentCount, ct);
        string? context = null;
        if (recent.Count > 0)
        {
            var descriptions = new List<string>();
            foreach (var item in recent) descriptions.Add(await DescribeAsync(item, ct));
            context = "Recent captures (newest first):\n" + string.Join("\n", descriptions);
        }
        var results = queued.IsMultiPhoto
            ? await photoIntake.ProcessMultiStoredAsync(imageId, context, analysis, ct)
            : new List<IntakeResult>
            {
                await photoIntake.ProcessStoredAsync(imageId, context, analysis, ct)
            };
        var processed = new List<ProcessedCapture>();
        foreach (var result in results)
        {
            var relationship = await photoIntake.ClassifyCaptureRelationshipAsync(
                result, recentCaptures, ct);
            var recentCapture = relationship?.QueueItemId is { } queueItemId
                ? recentCaptures.FirstOrDefault(candidate => candidate.QueueItemId == queueItemId)
                : FindSameProductCapture(result, recentCaptures);

            var action = result.Proposal.Action;
            var matchedItemId = result.Proposal.MatchedItemId;
            var matchedName = result.Proposal.MatchedItemName;
            var incrementBy = result.Proposal.IncrementBy;
            var captureRelationship = relationship?.Relationship;
            var confidence = relationship?.Confidence;
            var reason = relationship?.Reason;
            var suggestedRole = relationship?.SuggestedRole;

            if (recentCapture is not null
                && relationship is
                {
                    Relationship: CaptureRelationship.SamePhysicalItem,
                    Confidence: MatchConfidence.Medium or MatchConfidence.High
                })
            {
                action = IntakeAction.AttachImage;
                matchedItemId = recentCapture.InventoryItemId;
                matchedName = recentCapture.Name;
                incrementBy = 0;
            }
            else if (recentCapture is not null
                     && IsSameProduct(result, recentCapture)
                     && relationship is not
                     {
                         Relationship: CaptureRelationship.AnotherInstance or CaptureRelationship.DifferentItem,
                         Confidence: MatchConfidence.Medium or MatchConfidence.High
                     })
            {
                // Product-level similarity must not become an automatic quantity change when
                // the model could not establish whether this is the same physical instance.
                action = IntakeAction.ChooseCandidate;
                matchedItemId = null;
                matchedName = recentCapture.Name;
                captureRelationship ??= CaptureRelationship.Uncertain;
                confidence ??= MatchConfidence.None;
                reason ??= "Could not verify whether this is another view or another copy.";
            }

            processed.Add(new ProcessedCapture(
                result.Proposal.Draft,
                action,
                matchedItemId,
                matchedName,
                incrementBy,
                recentCapture?.QueueItemId,
                captureRelationship,
                confidence,
                reason,
                suggestedRole));
        }
        return processed;
    }

    private async Task<List<RecentCaptureCandidate>> GetRecentPhotoCapturesAsync(
        IntakeQueueItem queued, int count, CancellationToken ct)
    {
        if (count <= 0) return [];
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<RecentCaptureRow>("""
            SELECT Id AS QueueItemId, AppliedItemId AS InventoryItemId, ImageId, DraftJson
            FROM IntakeQueueItems
            WHERE SessionId = @sessionId AND Id < @id AND SourceType = @photo
              AND ImageId IS NOT NULL AND DraftJson IS NOT NULL
              AND Status IN (@ready, @accepted)
            ORDER BY Id DESC LIMIT @count;
            """, new
        {
            sessionId = queued.SessionId,
            id = queued.Id,
            photo = (int)IntakeSourceType.Photo,
            count,
            ready = (int)IntakeQueueStatus.ReadyForReview,
            accepted = (int)IntakeQueueStatus.Accepted
        });
        return rows.Select(row =>
        {
            var draft = DeserializeDraft(row.DraftJson);
            return new RecentCaptureCandidate(
                row.QueueItemId,
                row.InventoryItemId,
                draft.Name,
                draft.Code,
                draft.Attributes,
                row.ImageId);
        }).ToList();
    }

    private static RecentCaptureCandidate? FindSameProductCapture(
        IntakeResult result, IReadOnlyList<RecentCaptureCandidate> recentCaptures) =>
        recentCaptures.FirstOrDefault(candidate => IsSameProduct(result, candidate));

    private static bool IsSameProduct(IntakeResult result, RecentCaptureCandidate candidate)
    {
        var identification = result.Identification;
        if (!string.IsNullOrWhiteSpace(identification?.Barcode)
            && string.Equals(identification.Barcode, candidate.Code, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(identification?.Name)
               && InventoryService.NormalizeName(identification.Name)
               == InventoryService.NormalizeName(candidate.Name);
    }

    private async Task<List<Item>> GetRecentDraftsAsync(
        IntakeQueueItem queued, int count, CancellationToken ct)
    {
        if (count <= 0) return new List<Item>();
        using var conn = await db.OpenAsync(ct);
        var json = await conn.QueryAsync<string>("""
            SELECT DraftJson FROM IntakeQueueItems
            WHERE SessionId = @sessionId AND Id < @id AND DraftJson IS NOT NULL
              AND Status IN (@ready, @accepted)
            ORDER BY Id DESC LIMIT @count;
            """, new
        {
            sessionId = queued.SessionId,
            id = queued.Id,
            count,
            ready = (int)IntakeQueueStatus.ReadyForReview,
            accepted = (int)IntakeQueueStatus.Accepted
        });
        return json.Select(DeserializeDraft).ToList();
    }

    private static void ApplyRecentPlacement(Item draft, IReadOnlyList<Item> recent)
    {
        var placed = recent.FirstOrDefault(x => x.ContainerId is not null || x.LocationId is not null);
        if (placed is null || draft.ContainerId is not null || draft.LocationId is not null) return;
        draft.ContainerId = placed.ContainerId;
        draft.LocationId = placed.ContainerId is null ? placed.LocationId : null;
    }

    private async Task<string> DescribeAsync(Item item, CancellationToken ct)
    {
        var attributes = item.Attributes.Count == 0
            ? string.Empty
            : $"; attributes: {string.Join(", ", item.Attributes.Select(x => $"{x.Key}={x.Value}"))}";
        var placement = string.Empty;
        using var conn = await db.OpenAsync(ct);
        if (item.ContainerId is { } containerId)
        {
            var place = await conn.QuerySingleOrDefaultAsync<(string ContainerName, string LocationName)>("""
                SELECT c.Name AS ContainerName, l.Name AS LocationName
                FROM Containers c JOIN Locations l ON l.Id = c.LocationId WHERE c.Id = @containerId
                """, new { containerId });
            if (!string.IsNullOrWhiteSpace(place.ContainerName))
                placement = $"; stored in {place.ContainerName}, {place.LocationName}";
        }
        else if (item.LocationId is { } locationId)
        {
            var name = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT Name FROM Locations WHERE Id = @locationId", new { locationId });
            if (!string.IsNullOrWhiteSpace(name)) placement = $"; stored in {name}";
        }
        return $"- {item.Name}; kind {KindName(item.ItemKindId)}{placement}{attributes}";
    }

    private async Task StoreProcessedAsync(int id, ProcessedCapture processed, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems SET Status = @Ready,
                DraftJson = CASE WHEN SourceType = @Barcode
                    THEN json_set(@Draft, '$.quantity', CaptureQuantity) ELSE @Draft END,
                ImageId = @ImageId,
                IsMultiPhoto = 0,
                ProposalAction = @Action, MatchedItemId = @MatchedId,
                MatchedItemName = @MatchedName,
                IncrementBy = CASE WHEN SourceType = @Barcode
                    THEN CaptureQuantity ELSE @IncrementBy END,
                MatchedQueueItemId = @MatchedQueueItemId,
                CaptureRelationship = @CaptureRelationship,
                RelationshipConfidence = @RelationshipConfidence,
                RelationshipReason = @RelationshipReason,
                SuggestedImageRole = @SuggestedImageRole,
                ProcessedAt = @Now, Error = NULL
            WHERE Id = @Id;
            """, new
        {
            Id = id,
            Ready = (int)IntakeQueueStatus.ReadyForReview,
            Barcode = (int)IntakeSourceType.Barcode,
            Draft = JsonSerializer.Serialize(processed.Draft, Json),
            ImageId = processed.Draft.ImageId,
            Action = (int)processed.Action,
            MatchedId = processed.MatchedItemId,
            MatchedName = processed.MatchedItemName,
            IncrementBy = processed.IncrementBy,
            processed.MatchedQueueItemId,
            CaptureRelationship = processed.CaptureRelationship is { } relationship ? (int)relationship : (int?)null,
            RelationshipConfidence = processed.RelationshipConfidence is { } confidence ? (int)confidence : (int?)null,
            processed.RelationshipReason,
            SuggestedImageRole = processed.SuggestedImageRole is { } role ? (int)role : (int?)null,
            Now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private async Task<int> InsertProcessedAsync(
        IntakeQueueItem source, ProcessedCapture processed, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO IntakeQueueItems
                (SessionId, SourceType, SourceTypeOverride, ImageId, IsMultiPhoto, Status, DraftJson, ProposalAction,
                 MatchedItemId, MatchedItemName, MatchedQueueItemId, CaptureRelationship,
                 RelationshipConfidence, RelationshipReason, SuggestedImageRole,
                 IncrementBy, CreatedAt, ProcessedAt)
            VALUES
                (@SessionId, @SourceType, @SourceTypeOverride, @ImageId, 0, @Status, @Draft, @Action,
                 @MatchedId, @MatchedName, @MatchedQueueItemId, @CaptureRelationship,
                 @RelationshipConfidence, @RelationshipReason, @SuggestedImageRole,
                 @IncrementBy, @CreatedAt, @ProcessedAt);
            SELECT last_insert_rowid();
            """, new
        {
            SessionId = source.SessionId,
            SourceType = (int)IntakeSourceType.Photo,
            SourceTypeOverride = source.SourceTypeOverride ? 1 : 0,
            ImageId = processed.Draft.ImageId,
            Status = (int)IntakeQueueStatus.ReadyForReview,
            Draft = JsonSerializer.Serialize(processed.Draft, Json),
            Action = (int)processed.Action,
            MatchedId = processed.MatchedItemId,
            MatchedName = processed.MatchedItemName,
            processed.MatchedQueueItemId,
            CaptureRelationship = processed.CaptureRelationship is { } relationship ? (int)relationship : (int?)null,
            RelationshipConfidence = processed.RelationshipConfidence is { } confidence ? (int)confidence : (int?)null,
            processed.RelationshipReason,
            SuggestedImageRole = processed.SuggestedImageRole is { } role ? (int)role : (int?)null,
            IncrementBy = processed.IncrementBy,
            CreatedAt = source.CreatedAt.ToString("O"),
            ProcessedAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private async Task MarkFailedAsync(int id, string error)
    {
        using var conn = await db.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE IntakeQueueItems SET Status = @failed, Error = @error, ProcessedAt = @now
            WHERE Id = @id;
            """, new
        {
            id,
            failed = (int)IntakeQueueStatus.Failed,
            error = error.Length > 1000 ? error[..1000] : error,
            now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private async Task ReturnToPendingAsync(int id)
    {
        using var conn = await db.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE IntakeQueueItems SET Status = @pending, ProcessingStartedAt = NULL WHERE Id = @id",
            new { id, pending = (int)IntakeQueueStatus.Pending });
    }

    private async Task<int?> GetAppliedItemIdAsync(int? queueItemId, CancellationToken ct)
    {
        if (queueItemId is null) return null;
        using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<int?>(
            "SELECT AppliedItemId FROM IntakeQueueItems WHERE Id = @queueItemId",
            new { queueItemId });
    }

    private async Task<int?> ResolveReceiptItemAsync(
        IntakeQueueItem receipt, int matchedQueueItemId, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<int?>("""
            SELECT AppliedItemId FROM IntakeQueueItems
            WHERE Id = @matchedQueueItemId
              AND SessionId = @sessionId
              AND Id < @receiptId
              AND SourceType != @receipt
              AND Status = @accepted;
            """, new
        {
            matchedQueueItemId,
            sessionId = receipt.SessionId,
            receiptId = receipt.Id,
            receipt = (int)IntakeSourceType.Receipt,
            accepted = (int)IntakeQueueStatus.Accepted
        });
    }

    private static bool MergeMissingMetadata(Item target, Item observed)
    {
        var changed = SpecialAttributeCatalog.MergeMissing(target, observed);
        if (string.IsNullOrWhiteSpace(target.Description) && !string.IsNullOrWhiteSpace(observed.Description))
        {
            target.Description = observed.Description;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.Code) && !string.IsNullOrWhiteSpace(observed.Code))
        {
            target.Code = observed.Code;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.Unit) && !string.IsNullOrWhiteSpace(observed.Unit))
        {
            target.Unit = observed.Unit;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(target.Notes) && !string.IsNullOrWhiteSpace(observed.Notes))
        {
            target.Notes = observed.Notes;
            changed = true;
        }
        foreach (var (name, value) in observed.Attributes)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)
                || target.Attributes.Keys.Any(existing =>
                    string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            target.Attributes[name] = value;
            changed = true;
        }
        return changed;
    }

    private async Task<int> GetOrCreateActiveSessionIdAsync(CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO IntakeSessions (StartedAt)
            SELECT @now WHERE NOT EXISTS (SELECT 1 FROM IntakeSessions WHERE EndedAt IS NULL);
            """, new { now = DateTimeOffset.UtcNow.ToString("O") });
        return await conn.QuerySingleAsync<int>(
            "SELECT Id FROM IntakeSessions WHERE EndedAt IS NULL ORDER BY Id DESC LIMIT 1");
    }

    private async Task<int> InsertAsync(IntakeQueueItem item, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO IntakeQueueItems
                (SessionId, SourceType, SourceTypeOverride, SourceCode, CaptureQuantity, LiveCaptureHoldUntil,
                 BrowserUploadToken, ImageId, IsMultiPhoto, Status, DraftJson,
                 ProposalAction, IncrementBy, CreatedAt, ProcessedAt)
            VALUES
                (@SessionId, @SourceType, @SourceTypeOverride, @SourceCode, @CaptureQuantity, @LiveCaptureHoldUntil,
                 @BrowserUploadToken, @ImageId, @IsMultiPhoto, @Status, @DraftJson,
                 @ProposalAction, @IncrementBy, @CreatedAt, @ProcessedAt);
            SELECT last_insert_rowid();
            """, new
        {
            item.SessionId,
            SourceType = (int)item.SourceType,
            SourceTypeOverride = item.SourceTypeOverride ? 1 : 0,
            item.SourceCode,
            item.CaptureQuantity,
            LiveCaptureHoldUntil = item.LiveCaptureHoldUntil?.ToString("O"),
            item.BrowserUploadToken,
            item.ImageId,
            IsMultiPhoto = item.IsMultiPhoto ? 1 : 0,
            Status = (int)item.Status,
            DraftJson = JsonSerializer.Serialize(item.Draft, Json),
            ProposalAction = item.ProposalAction is { } action ? (int)action : (int?)null,
            item.IncrementBy,
            CreatedAt = item.CreatedAt.ToString("O"),
            ProcessedAt = item.ProcessedAt?.ToString("O")
        });
    }

    private async Task<int> InsertIdempotentlyAsync(IntakeQueueItem item, CancellationToken ct)
    {
        try
        {
            return await InsertAsync(item, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19
                                         && item.BrowserUploadToken is not null)
        {
            var existing = await GetByBrowserUploadTokenAsync(item.BrowserUploadToken, ct);
            if (existing is not null) return existing.Id;
            throw;
        }
    }

    private async Task<IntakeQueueItem?> GetByBrowserUploadTokenAsync(
        string token, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<QueueRow>(
            QueueSelect + " WHERE q.BrowserUploadToken = @token", new { token });
        return row is null ? null : Map(row);
    }

    private static int KindId(string? kind) => kind switch
    {
        "Grocery" => 1,
        "Book" => 2,
        "Tool" => 3,
        "Electronics" => 4,
        "Media" => 5,
        "Clothing" => 6,
        _ => 7
    };

    private static string KindName(int kindId) => kindId switch
    {
        1 => "Grocery",
        2 => "Book",
        3 => "Tool",
        4 => "Electronics",
        5 => "Media",
        6 => "Clothing",
        _ => "Other"
    };

    private static Item DeserializeDraft(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Item { Name = string.Empty, ItemKindId = 7 };
        try
        {
            return JsonSerializer.Deserialize<Item>(json, Json)
                   ?? new Item { Name = string.Empty, ItemKindId = 7 };
        }
        catch (JsonException)
        {
            return new Item { Name = string.Empty, ItemKindId = 7 };
        }
    }

    private static ReceiptExtraction? DeserializeReceipt(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReceiptExtraction>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IntakeQueueItem Map(QueueRow row) => new()
    {
        Id = row.Id,
        SessionId = row.SessionId,
        SourceType = (IntakeSourceType)row.SourceType,
        SourceTypeOverride = row.SourceTypeOverride,
        SourceCode = row.SourceCode,
        CaptureQuantity = Math.Max(1, row.CaptureQuantity),
        LiveCaptureHoldUntil = ParseDate(row.LiveCaptureHoldUntil),
        BrowserUploadToken = row.BrowserUploadToken,
        ImageId = row.ImageId,
        IsMultiPhoto = row.IsMultiPhoto,
        Status = (IntakeQueueStatus)row.Status,
        Draft = DeserializeDraft(row.DraftJson),
        Receipt = DeserializeReceipt(row.ReceiptJson),
        ProposalAction = row.ProposalAction is { } action ? (IntakeAction)action : null,
        MatchedItemId = row.MatchedItemId,
        MatchedItemName = row.MatchedItemName,
        MatchedQueueItemId = row.MatchedQueueItemId,
        CaptureRelationship = row.CaptureRelationship is { } relationship
            ? (CaptureRelationship)relationship
            : null,
        RelationshipConfidence = row.RelationshipConfidence is { } confidence
            ? (MatchConfidence)confidence
            : null,
        RelationshipReason = row.RelationshipReason,
        SuggestedImageRole = row.SuggestedImageRole is { } role ? (ItemImageRole)role : null,
        IncrementBy = row.IncrementBy,
        AppliedItemId = row.AppliedItemId,
        Error = row.Error,
        CreatedAt = DateTimeOffset.Parse(row.CreatedAt),
        ProcessingStartedAt = ParseDate(row.ProcessingStartedAt),
        ProcessedAt = ParseDate(row.ProcessedAt),
        ReviewedAt = ParseDate(row.ReviewedAt)
    };

    private static IntakeSession Map(SessionRow row) =>
        new(row.Id, DateTimeOffset.Parse(row.StartedAt), ParseDate(row.EndedAt));

    private static DateTimeOffset? ParseDate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.Parse(value);

    private const string QueueSelect = """
        SELECT q.Id, q.SessionId, q.SourceType, q.SourceTypeOverride, q.SourceCode,
               q.CaptureQuantity, q.LiveCaptureHoldUntil, q.BrowserUploadToken,
               q.ImageId, q.IsMultiPhoto, q.Status,
               q.DraftJson, q.ReceiptJson, q.ProposalAction, q.MatchedItemId, q.MatchedItemName,
               q.MatchedQueueItemId, q.CaptureRelationship, q.RelationshipConfidence,
               q.RelationshipReason, q.SuggestedImageRole,
               q.IncrementBy, q.AppliedItemId, q.Error, q.CreatedAt,
               q.ProcessingStartedAt, q.ProcessedAt, q.ReviewedAt
        FROM IntakeQueueItems q
        """;

    private sealed class QueueRow
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public int SourceType { get; set; }
        public bool SourceTypeOverride { get; set; }
        public string? SourceCode { get; set; }
        public int CaptureQuantity { get; set; }
        public string? LiveCaptureHoldUntil { get; set; }
        public string? BrowserUploadToken { get; set; }
        public int? ImageId { get; set; }
        public bool IsMultiPhoto { get; set; }
        public int Status { get; set; }
        public string? DraftJson { get; set; }
        public string? ReceiptJson { get; set; }
        public int? ProposalAction { get; set; }
        public int? MatchedItemId { get; set; }
        public string? MatchedItemName { get; set; }
        public int? MatchedQueueItemId { get; set; }
        public int? CaptureRelationship { get; set; }
        public int? RelationshipConfidence { get; set; }
        public string? RelationshipReason { get; set; }
        public int? SuggestedImageRole { get; set; }
        public decimal IncrementBy { get; set; }
        public int? AppliedItemId { get; set; }
        public string? Error { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string? ProcessingStartedAt { get; set; }
        public string? ProcessedAt { get; set; }
        public string? ReviewedAt { get; set; }
    }

    private sealed class SessionRow
    {
        public int Id { get; set; }
        public string StartedAt { get; set; } = string.Empty;
        public string? EndedAt { get; set; }
    }

    private sealed class RecentCaptureRow
    {
        public int QueueItemId { get; set; }
        public int? InventoryItemId { get; set; }
        public int ImageId { get; set; }
        public string DraftJson { get; set; } = string.Empty;
    }

    private sealed class ReceiptCandidateRow
    {
        public int QueueItemId { get; set; }
        public int? InventoryItemId { get; set; }
        public string DraftJson { get; set; } = string.Empty;
    }

    private record ProcessedCapture(
        Item Draft,
        IntakeAction Action,
        int? MatchedItemId,
        string? MatchedItemName,
        decimal IncrementBy,
        int? MatchedQueueItemId = null,
        CaptureRelationship? CaptureRelationship = null,
        MatchConfidence? RelationshipConfidence = null,
        string? RelationshipReason = null,
        ItemImageRole? SuggestedImageRole = null);
}
