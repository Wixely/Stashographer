using System.Text.Json;
using Dapper;
using Stashographer.Data;
using Stashographer.Data.Entities;
using Stashographer.Services.Ai;
using Stashographer.Services.Config;
using Stashographer.Services.Images;
using Stashographer.Services.Intake;
using Stashographer.Services.Inventory;

namespace Stashographer.Services.Modify;

/// <summary>
/// Durable photo reminders for explicit changes to existing inventory. Vision may identify an
/// item, but only <see cref="ApplyAsync"/> can change inventory and it always requires a user
/// supplied item and action.
/// </summary>
public sealed class ModifyQueueService(
    IDbConnectionFactory db,
    ImageService images,
    PhotoIntakeService photoIntake,
    InventoryService inventory,
    ConsumptionService consumption,
    IntakeQueueSignal signal,
    ILogger<ModifyQueueService> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ModifyQueueItem> EnqueuePhotoAsync(
        Stream content, string mediaType, string? originalName, bool multipleItems = true,
        CancellationToken ct = default) =>
        EnqueuePhotoCoreAsync(content, mediaType, originalName, multipleItems, null, ct);

    public Task<ModifyQueueItem> EnqueuePhotoFromBrowserAsync(
        Stream content, string mediaType, string? originalName, bool multipleItems,
        string browserUploadToken, CancellationToken ct = default) =>
        EnqueuePhotoCoreAsync(
            content, mediaType, originalName, multipleItems, browserUploadToken, ct);

    private async Task<ModifyQueueItem> EnqueuePhotoCoreAsync(
        Stream content, string mediaType, string? originalName, bool multipleItems,
        string? browserUploadToken, CancellationToken ct)
    {
        if (browserUploadToken is not null
            && await GetByBrowserUploadTokenAsync(browserUploadToken, ct) is { } existing)
            return existing;

        var image = await images.SaveAsync(content, mediaType, originalName, null, ct);
        var item = new ModifyQueueItem
        {
            SessionId = await GetOrCreateActiveSessionIdAsync(ct),
            OriginalImageId = image.Id,
            ImageId = image.Id,
            IsMultiPhoto = multipleItems,
            BrowserUploadToken = browserUploadToken,
            Status = ModifyQueueStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };
        item.Id = await InsertIdempotentlyAsync(item, ct);
        signal.Pulse();
        return item;
    }

    public async Task<List<ModifyQueueItem>> GetOpenAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<QueueRow>(SelectQueue + " " + """
            WHERE q.Status NOT IN (@applied, @dismissed)
            ORDER BY q.Id;
            """, new
        {
            applied = (int)ModifyQueueStatus.Applied,
            dismissed = (int)ModifyQueueStatus.Dismissed
        });
        return rows.Select(Map).ToList();
    }

    public async Task<ModifyQueueItem?> GetAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<QueueRow>(SelectQueue + " WHERE q.Id = @id", new { id });
        return row is null ? null : Map(row);
    }

    public async Task<ModifyQueueCounts> GetCountsAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var rows = await conn.QueryAsync<(int Status, int Count)>(
            "SELECT Status, COUNT(*) AS Count FROM ModifyQueueItems GROUP BY Status;");
        var counts = rows.ToDictionary(row => (ModifyQueueStatus)row.Status, row => row.Count);
        return new ModifyQueueCounts(
            counts.GetValueOrDefault(ModifyQueueStatus.Pending),
            counts.GetValueOrDefault(ModifyQueueStatus.Processing),
            counts.GetValueOrDefault(ModifyQueueStatus.ReadyForReview),
            counts.GetValueOrDefault(ModifyQueueStatus.Failed),
            counts.GetValueOrDefault(ModifyQueueStatus.Applied)
            + counts.GetValueOrDefault(ModifyQueueStatus.Dismissed));
    }

    public async Task<ModifySession> GetCurrentSessionAsync(CancellationToken ct = default)
    {
        var id = await GetOrCreateActiveSessionIdAsync(ct);
        using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<SessionRow>(
            "SELECT Id, StartedAt, EndedAt, WorkingLocationId, WorkingContainerId FROM ModifySessions WHERE Id = @id;",
            new { id });
        return Map(row);
    }

    public async Task<ModifySession> StartNewSessionAsync(
        int? workingLocationId = null, int? workingContainerId = null,
        CancellationToken ct = default)
    {
        ValidateOnePlace(workingLocationId, workingContainerId);
        using var conn = await db.OpenAsync(ct);
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var tx = conn.BeginTransaction();
        await ValidateDestinationAsync(conn, tx, workingLocationId, workingContainerId);
        await conn.ExecuteAsync(
            "UPDATE ModifySessions SET EndedAt = @now WHERE EndedAt IS NULL;", new { now }, tx);
        var id = await conn.ExecuteScalarAsync<int>("""
            INSERT INTO ModifySessions
                (StartedAt, WorkingLocationId, WorkingContainerId)
            VALUES (@now, @workingLocationId, @workingContainerId);
            SELECT last_insert_rowid();
            """, new { now, workingLocationId, workingContainerId }, tx);
        tx.Commit();
        return new ModifySession(id, DateTimeOffset.Parse(now), null, workingLocationId, workingContainerId);
    }

    public async Task SetWorkingPlaceAsync(
        int? locationId, int? containerId, CancellationToken ct = default)
    {
        ValidateOnePlace(locationId, containerId);
        var sessionId = await GetOrCreateActiveSessionIdAsync(ct);
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        await ValidateDestinationAsync(conn, tx, locationId, containerId);
        await conn.ExecuteAsync("""
            UPDATE ModifySessions
            SET WorkingLocationId = @locationId, WorkingContainerId = @containerId
            WHERE Id = @sessionId;
            """, new { sessionId, locationId, containerId }, tx);
        tx.Commit();
    }

    public async Task<bool> ProcessNextAsync(
        ModifyOptions options, bool aiEnabled, CancellationToken ct = default)
    {
        if (!options.AutoProcessPhotos || !aiEnabled) return false;
        using var conn = await db.OpenAsync(ct);
        var id = await conn.QueryFirstOrDefaultAsync<int?>("""
            SELECT Id FROM ModifyQueueItems
            WHERE Status = @pending
            ORDER BY CreatedAt, Id LIMIT 1;
            """, new { pending = (int)ModifyQueueStatus.Pending });
        return id is not null && await ProcessAsync(id.Value, options, aiEnabled, ct);
    }

    public async Task<DateTimeOffset?> GetNextProcessableCreatedAtAsync(
        ModifyOptions options, bool aiEnabled, CancellationToken ct = default)
    {
        if (!options.AutoProcessPhotos || !aiEnabled) return null;
        using var conn = await db.OpenAsync(ct);
        var value = await conn.QueryFirstOrDefaultAsync<string?>("""
            SELECT CreatedAt FROM ModifyQueueItems
            WHERE Status = @pending
            ORDER BY CreatedAt, Id LIMIT 1;
            """, new { pending = (int)ModifyQueueStatus.Pending });
        return value is null ? null : DateTimeOffset.Parse(value);
    }

    public async Task<bool> ProcessAsync(
        int id, ModifyOptions options, bool aiEnabled, CancellationToken ct = default)
    {
        if (!aiEnabled)
            throw new InvalidOperationException("Configure an AI vision model to identify this photo, or select the item manually.");

        using (var conn = await db.OpenAsync(ct))
        {
            var claimed = await conn.ExecuteAsync("""
                UPDATE ModifyQueueItems
                SET Status = @processing, ProcessingStartedAt = @now, Error = NULL
                WHERE Id = @id AND Status IN (@pending, @failed);
                """, new
            {
                id,
                processing = (int)ModifyQueueStatus.Processing,
                pending = (int)ModifyQueueStatus.Pending,
                failed = (int)ModifyQueueStatus.Failed,
                now = DateTimeOffset.UtcNow.ToString("O")
            });
            if (claimed == 0) return false;
        }

        try
        {
            var queued = await GetAsync(id)
                         ?? throw new InvalidOperationException("The modify queue item no longer exists.");
            var context = await BuildSessionContextAsync(queued, options.ContextItemCount, ct);
            var results = queued.IsMultiPhoto && options.SplitMultipleItems
                ? await photoIntake.ProcessMultiStoredAsync(queued.OriginalImageId, context, ct)
                : [await photoIntake.ProcessStoredAsync(queued.OriginalImageId, context, ct)];
            if (results.Count == 0)
                throw new InvalidOperationException("No inventory objects were found in this photo.");

            await StoreResultsAsync(queued, results, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ReturnToPendingAsync(id);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Modify queue item {QueueItemId} failed", id);
            await MarkFailedAsync(id, ex.Message);
            return true;
        }
    }

    public async Task RetryAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE ModifyQueueItems
            SET Status = @pending, Error = NULL, IdentificationJson = NULL,
                MatchedItemId = NULL, MatchedItemName = NULL, MatchConfidence = 0,
                MatchReason = NULL, MatchedItemUpdatedAt = NULL,
                ProcessingStartedAt = NULL, ProcessedAt = NULL
            WHERE Id = @id AND Status = @failed
              AND NOT EXISTS (
                  SELECT 1 FROM ModifyActionEvents action
                  WHERE action.ModifyQueueItemId = ModifyQueueItems.Id);
            """, new { id, pending = (int)ModifyQueueStatus.Pending, failed = (int)ModifyQueueStatus.Failed });
        var reloaded = await GetAsync(id, ct);
        if (reloaded?.Status == ModifyQueueStatus.Failed)
            throw new InvalidOperationException(
                "An inventory action already started for this reminder. Inspect the item before resolving it manually.");
        signal.Pulse();
    }

    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE ModifyQueueItems
            SET Status = @pending, ProcessingStartedAt = NULL,
                Error = 'Processing was interrupted and will be retried.'
            WHERE Status = @processing
              AND NOT EXISTS (
                  SELECT 1 FROM ModifyActionEvents action
                  WHERE action.ModifyQueueItemId = ModifyQueueItems.Id);
            """, new
        {
            pending = (int)ModifyQueueStatus.Pending,
            processing = (int)ModifyQueueStatus.Processing
        });
        await conn.ExecuteAsync("""
            UPDATE ModifyQueueItems
            SET Status = @applied, ReviewedAt = COALESCE(ReviewedAt, @now)
            WHERE Status = @processing
              AND EXISTS (
                  SELECT 1 FROM ModifyActionEvents action
                  WHERE action.ModifyQueueItemId = ModifyQueueItems.Id
                    AND action.AppliedAt IS NOT NULL);
            """, new
        {
            applied = (int)ModifyQueueStatus.Applied,
            processing = (int)ModifyQueueStatus.Processing,
            now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    public async Task DismissAsync(int id, CancellationToken ct = default)
    {
        using var conn = await db.OpenAsync(ct);
        var changed = await conn.ExecuteAsync("""
            UPDATE ModifyQueueItems SET Status = @dismissed, ReviewedAt = @now
            WHERE Id = @id AND Status NOT IN (@processing, @applied, @dismissed)
              AND NOT EXISTS (
                  SELECT 1 FROM ModifyActionEvents action
                  WHERE action.ModifyQueueItemId = ModifyQueueItems.Id);
            """, new
        {
            id,
            dismissed = (int)ModifyQueueStatus.Dismissed,
            processing = (int)ModifyQueueStatus.Processing,
            applied = (int)ModifyQueueStatus.Applied,
            now = DateTimeOffset.UtcNow.ToString("O")
        });
        if (changed != 1) throw new InvalidOperationException("This reminder is no longer available to dismiss.");
    }

    public async Task<ModifyApplied> ApplyAsync(
        int queueItemId, int itemId, ModifyActionRequest request, CancellationToken ct = default)
    {
        var queued = await GetAsync(queueItemId, ct)
                     ?? throw new InvalidOperationException("The modify queue item no longer exists.");
        if (queued.Status is ModifyQueueStatus.Applied or ModifyQueueStatus.Dismissed)
            throw new InvalidOperationException("This modify reminder was already completed.");
        if (queued.Status == ModifyQueueStatus.Processing)
            throw new InvalidOperationException("Wait for the current processing operation to finish.");

        var item = await inventory.GetAsync(itemId, ct)
                   ?? throw new InvalidOperationException("The selected inventory item no longer exists.");
        ValidateRequest(item, request);
        if (!string.IsNullOrWhiteSpace(request.ExpectedItemUpdatedAt)
            && !string.Equals(item.UpdatedAt.ToString("O"), request.ExpectedItemUpdatedAt,
                StringComparison.Ordinal))
            throw new InvalidOperationException("This item changed after it was selected. Reload it before applying the modification.");

        var beforeJson = JsonSerializer.Serialize(item, Json);
        var now = DateTimeOffset.UtcNow;
        using (var conn = await db.OpenAsync(ct))
        using (var tx = conn.BeginTransaction())
        {
            var existing = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ModifyActionEvents WHERE ModifyQueueItemId = @queueItemId;",
                new { queueItemId }, tx);
            if (existing > 0)
                throw new InvalidOperationException("This modification has already started and will not be applied twice.");
            var claimed = await conn.ExecuteAsync("""
                UPDATE ModifyQueueItems SET Status = @processing, AppliedAction = @action
                WHERE Id = @queueItemId AND Status NOT IN (@processing, @applied, @dismissed);
                """, new
            {
                queueItemId,
                processing = (int)ModifyQueueStatus.Processing,
                applied = (int)ModifyQueueStatus.Applied,
                dismissed = (int)ModifyQueueStatus.Dismissed,
                action = (int)request.Action
            }, tx);
            if (claimed != 1)
                throw new InvalidOperationException("This modification is no longer available.");
            await conn.ExecuteAsync("""
                INSERT INTO ModifyActionEvents
                    (ModifyQueueItemId, ItemId, Action, BeforeJson, CreatedAt)
                VALUES (@queueItemId, @itemId, @action, @beforeJson, @createdAt);
                """, new
            {
                queueItemId,
                itemId,
                action = (int)request.Action,
                beforeJson,
                createdAt = now.ToString("O")
            }, tx);
            tx.Commit();
        }

        int? createdItemId = null;
        int? consumptionEventId = null;
        try
        {
            switch (request.Action)
            {
                case ModifyAction.Decrement:
                {
                    var used = await consumption.UseItemAsync(
                        itemId, request.Quantity,
                        Clean(request.Description) ?? $"Modified from photo: used {item.Name}", ct);
                    consumptionEventId = used.EventId;
                    break;
                }
                case ModifyAction.Move when request.Quantity < item.Quantity:
                {
                    var split = await inventory.SplitAsync(
                        itemId, request.Quantity, request.LocationId, request.ContainerId, ct);
                    createdItemId = split.Created.Id;
                    break;
                }
                case ModifyAction.Move:
                    await inventory.MoveItemsAsync([itemId], request.LocationId, request.ContainerId, ct);
                    break;
                case ModifyAction.Delete:
                    await inventory.DeleteAsync(itemId, ct);
                    break;
                case ModifyAction.AttachImage:
                    await inventory.AttachImageAsync(
                        itemId, queued.ImageId, request.ImageRole, makePrimary: false, ct: ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.Action));
            }

            var after = request.Action == ModifyAction.Delete
                ? null
                : await inventory.GetAsync(createdItemId ?? itemId, ct);
            await FinalizeAppliedAsync(
                queueItemId, itemId, item.Name, request.Action,
                after, createdItemId, consumptionEventId, ct);
            return new ModifyApplied(
                queueItemId, request.Action, itemId, item.Name, createdItemId, consumptionEventId);
        }
        catch (Exception ex)
        {
            await MarkActionFailedAsync(queueItemId, ex.Message);
            throw;
        }
    }

    private async Task FinalizeAppliedAsync(
        int queueItemId, int itemId, string itemName, ModifyAction action, Item? after,
        int? createdItemId, int? consumptionEventId, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        if (consumptionEventId is not null)
        {
            await conn.ExecuteAsync("""
                UPDATE ConsumptionEvents SET ModifyQueueItemId = @queueItemId
                WHERE Id = @consumptionEventId;
                """, new { queueItemId, consumptionEventId }, tx);
        }
        await conn.ExecuteAsync("""
            UPDATE ModifyActionEvents
            SET AfterJson = @afterJson, ConsumptionEventId = @consumptionEventId,
                CreatedItemId = @createdItemId, AppliedAt = @now, Error = NULL
            WHERE ModifyQueueItemId = @queueItemId;
            """, new
        {
            queueItemId,
            afterJson = after is null ? null : JsonSerializer.Serialize(after, Json),
            consumptionEventId,
            createdItemId,
            now = DateTimeOffset.UtcNow.ToString("O")
        }, tx);
        var changed = await conn.ExecuteAsync("""
            UPDATE ModifyQueueItems
            SET Status = @applied, MatchedItemId = @itemId,
                MatchedItemName = COALESCE(MatchedItemName, @itemName),
                AppliedAction = @action, ReviewedAt = @now, Error = NULL
            WHERE Id = @queueItemId AND Status = @processing;
            """, new
        {
            queueItemId,
            itemId = action == ModifyAction.Delete ? (int?)null : itemId,
            itemName = after?.Name ?? itemName,
            action = (int)action,
            applied = (int)ModifyQueueStatus.Applied,
            processing = (int)ModifyQueueStatus.Processing,
            now = DateTimeOffset.UtcNow.ToString("O")
        }, tx);
        if (changed != 1)
            throw new InvalidOperationException("The modification was applied but its queue record could not be completed.");
        tx.Commit();
    }

    private async Task StoreResultsAsync(
        ModifyQueueItem queued, IReadOnlyList<IntakeResult> results, CancellationToken ct)
    {
        using var readConn = await db.OpenAsync(ct);
        var session = await readConn.QuerySingleAsync<SessionRow>("""
            SELECT Id, StartedAt, EndedAt, WorkingLocationId, WorkingContainerId
            FROM ModifySessions WHERE Id = @sessionId;
            """, new { sessionId = queued.SessionId });
        var mapped = new List<ResultRow>();
        foreach (var result in results)
        {
            var matchedId = result.Proposal.MatchedItemId;
            var matched = matchedId is { } itemId ? await inventory.GetAsync(itemId, ct) : null;
            var placeCandidates = result.Candidates.Where(candidate =>
                    session.WorkingContainerId is { } containerId
                        ? candidate.ContainerId == containerId
                        : session.WorkingLocationId is { } locationId
                          && candidate.LocationId == locationId
                          && candidate.ContainerId is null)
                .ToList();
            string? reason = null;
            if (placeCandidates.Count == 1 && matched?.Id != placeCandidates[0].Id)
            {
                matched = placeCandidates[0];
                reason = session.WorkingContainerId is not null
                    ? "Prioritized because it is currently in the working container."
                    : "Prioritized because it is currently loose in the working location.";
            }
            mapped.Add(new ResultRow(
                result.ImageId,
                result.Identification,
                matched?.Id,
                matched?.Name,
                reason is null
                    ? ConfidenceFor(result.Proposal.Action, matched is not null)
                    : MatchConfidence.Medium,
                reason,
                matched?.UpdatedAt.ToString("O")));
        }

        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await UpdateResultAsync(conn, tx, queued.Id, mapped[0], now);
        foreach (var result in mapped.Skip(1))
        {
            var parameters = new DynamicParameters();
            parameters.Add("sessionId", queued.SessionId);
            parameters.Add("originalImageId", queued.OriginalImageId);
            parameters.Add("imageId", result.ImageId);
            parameters.Add("ready", (int)ModifyQueueStatus.ReadyForReview);
            parameters.Add("identificationJson", SerializeIdentification(result.Identification));
            parameters.Add("matchedItemId", result.MatchedItemId);
            parameters.Add("matchedItemName", result.MatchedItemName);
            parameters.Add("matchConfidence", (int)result.Confidence);
            parameters.Add("matchReason", result.MatchReason);
            parameters.Add("matchedItemUpdatedAt", result.MatchedItemUpdatedAt);
            parameters.Add("createdAt", queued.CreatedAt.ToString("O"));
            parameters.Add("now", now);
            await conn.ExecuteAsync("""
                INSERT INTO ModifyQueueItems
                    (SessionId, OriginalImageId, ImageId, IsMultiPhoto, Status,
                     IdentificationJson, MatchedItemId, MatchedItemName, MatchConfidence, MatchReason,
                     MatchedItemUpdatedAt, CreatedAt, ProcessedAt)
                VALUES
                    (@sessionId, @originalImageId, @imageId, 0, @ready,
                     @identificationJson, @matchedItemId, @matchedItemName, @matchConfidence, @matchReason,
                     @matchedItemUpdatedAt, @createdAt, @now);
                """, parameters, tx);
        }
        tx.Commit();
    }

    private static Task UpdateResultAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        int id, ResultRow result, string now)
    {
        var parameters = new DynamicParameters();
        parameters.Add("id", id);
        parameters.Add("imageId", result.ImageId);
        parameters.Add("ready", (int)ModifyQueueStatus.ReadyForReview);
        parameters.Add("processing", (int)ModifyQueueStatus.Processing);
        parameters.Add("identificationJson", SerializeIdentification(result.Identification));
        parameters.Add("matchedItemId", result.MatchedItemId);
        parameters.Add("matchedItemName", result.MatchedItemName);
        parameters.Add("matchConfidence", (int)result.Confidence);
        parameters.Add("matchReason", result.MatchReason);
        parameters.Add("matchedItemUpdatedAt", result.MatchedItemUpdatedAt);
        parameters.Add("now", now);
        return conn.ExecuteAsync("""
            UPDATE ModifyQueueItems
            SET ImageId = @imageId, IsMultiPhoto = 0, Status = @ready,
                IdentificationJson = @identificationJson,
                MatchedItemId = @matchedItemId, MatchedItemName = @matchedItemName,
                MatchConfidence = @matchConfidence, MatchReason = @matchReason,
                MatchedItemUpdatedAt = @matchedItemUpdatedAt,
                ProcessedAt = @now, Error = NULL
            WHERE Id = @id AND Status = @processing;
            """, parameters, tx);
    }

    private async Task<string?> BuildSessionContextAsync(
        ModifyQueueItem queued, int count, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        var session = await conn.QuerySingleAsync<SessionRow>("""
            SELECT Id, StartedAt, EndedAt, WorkingLocationId, WorkingContainerId
            FROM ModifySessions WHERE Id = @sessionId;
            """, new { sessionId = queued.SessionId });
        var recent = (await conn.QueryAsync<string>("""
            SELECT MatchedItemName FROM ModifyQueueItems
            WHERE SessionId = @sessionId AND Id < @id AND MatchedItemName IS NOT NULL
            ORDER BY Id DESC LIMIT @take;
            """, new
        {
            sessionId = queued.SessionId,
            id = queued.Id,
            take = Math.Clamp(count, 0, 25)
        })).ToList();
        var parts = new List<string>();
        if (session.WorkingContainerId is { } containerId)
            parts.Add($"This modify session is working from container id {containerId}.");
        else if (session.WorkingLocationId is { } locationId)
            parts.Add($"This modify session is working from location id {locationId}.");
        if (recent.Count > 0)
            parts.Add("Earlier confirmed modify items, newest first: " + string.Join(", ", recent));
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private async Task<int> InsertIdempotentlyAsync(ModifyQueueItem item, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        try
        {
            return await conn.ExecuteScalarAsync<int>("""
                INSERT INTO ModifyQueueItems
                    (SessionId, OriginalImageId, ImageId, IsMultiPhoto, BrowserUploadToken,
                     Status, CreatedAt)
                VALUES
                    (@SessionId, @OriginalImageId, @ImageId, @IsMultiPhoto, @BrowserUploadToken,
                     @Status, @CreatedAt);
                SELECT last_insert_rowid();
                """, new
            {
                item.SessionId,
                item.OriginalImageId,
                item.ImageId,
                IsMultiPhoto = item.IsMultiPhoto ? 1 : 0,
                item.BrowserUploadToken,
                Status = (int)item.Status,
                CreatedAt = item.CreatedAt.ToString("O")
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19
                                                               && item.BrowserUploadToken is not null)
        {
            var existing = await GetByBrowserUploadTokenAsync(item.BrowserUploadToken, ct);
            if (existing is not null) return existing.Id;
            throw;
        }
    }

    private async Task<ModifyQueueItem?> GetByBrowserUploadTokenAsync(
        string token, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<QueueRow>(
            SelectQueue + " WHERE q.BrowserUploadToken = @token", new { token });
        return row is null ? null : Map(row);
    }

    private async Task<int> GetOrCreateActiveSessionIdAsync(CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        var id = await conn.QueryFirstOrDefaultAsync<int?>(
            "SELECT Id FROM ModifySessions WHERE EndedAt IS NULL ORDER BY Id DESC LIMIT 1;",
            transaction: tx);
        if (id is null)
        {
            id = await conn.ExecuteScalarAsync<int>("""
                INSERT INTO ModifySessions (StartedAt) VALUES (@now);
                SELECT last_insert_rowid();
                """, new { now = DateTimeOffset.UtcNow.ToString("O") }, tx);
        }
        tx.Commit();
        return id.Value;
    }

    private async Task ReturnToPendingAsync(int id)
    {
        using var conn = await db.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE ModifyQueueItems SET Status = @pending, ProcessingStartedAt = NULL
            WHERE Id = @id AND Status = @processing;
            """, new
        {
            id,
            pending = (int)ModifyQueueStatus.Pending,
            processing = (int)ModifyQueueStatus.Processing
        });
    }

    private async Task MarkFailedAsync(int id, string error)
    {
        using var conn = await db.OpenAsync();
        await conn.ExecuteAsync("""
            UPDATE ModifyQueueItems SET Status = @failed, Error = @error, ProcessedAt = @now
            WHERE Id = @id;
            """, new
        {
            id,
            failed = (int)ModifyQueueStatus.Failed,
            error = error.Length > 1000 ? error[..1000] : error,
            now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private async Task MarkActionFailedAsync(int queueItemId, string error)
    {
        try
        {
            using var conn = await db.OpenAsync();
            using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("""
                UPDATE ModifyActionEvents SET Error = @error
                WHERE ModifyQueueItemId = @queueItemId AND AppliedAt IS NULL;
                """, new { queueItemId, error }, tx);
            await conn.ExecuteAsync("""
                UPDATE ModifyQueueItems SET Status = @failed, Error = @error
                WHERE Id = @queueItemId AND Status = @processing;
                """, new
            {
                queueItemId,
                failed = (int)ModifyQueueStatus.Failed,
                processing = (int)ModifyQueueStatus.Processing,
                error
            }, tx);
            tx.Commit();
        }
        catch
        {
            // Preserve the original action failure. Recovery will not apply the action twice.
        }
    }

    private static void ValidateRequest(Item item, ModifyActionRequest request)
    {
        switch (request.Action)
        {
            case ModifyAction.Decrement:
                if (request.Quantity <= 0 || request.Quantity > item.Quantity)
                    throw new InvalidOperationException(
                        $"Choose a quantity between 0 and {item.Quantity:0.##}.");
                break;
            case ModifyAction.Move:
                ValidateOnePlace(request.LocationId, request.ContainerId);
                if (request.LocationId is null && request.ContainerId is null)
                    throw new InvalidOperationException("Choose a destination for this move.");
                if (request.Quantity <= 0 || request.Quantity > item.Quantity)
                    throw new InvalidOperationException(
                        $"Choose a quantity between 0 and {item.Quantity:0.##}.");
                if (item.LocationId == request.LocationId && item.ContainerId == request.ContainerId)
                    throw new InvalidOperationException("Choose a different destination.");
                break;
            case ModifyAction.Delete:
            case ModifyAction.AttachImage:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Action));
        }
    }

    private static void ValidateOnePlace(int? locationId, int? containerId)
    {
        if (locationId is not null && containerId is not null)
            throw new InvalidOperationException("Choose either a location or a container, not both.");
    }

    private static async Task ValidateDestinationAsync(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        int? locationId, int? containerId)
    {
        if (locationId is { } location
            && await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Locations WHERE Id = @location;", new { location }, tx) == 0)
            throw new InvalidOperationException("The selected location no longer exists.");
        if (containerId is { } container
            && await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Containers WHERE Id = @container;", new { container }, tx) == 0)
            throw new InvalidOperationException("The selected container no longer exists.");
    }

    private static MatchConfidence ConfidenceFor(IntakeAction action, bool matched) =>
        !matched ? MatchConfidence.None : action switch
        {
            IntakeAction.ChooseCandidate => MatchConfidence.Medium,
            _ => MatchConfidence.High
        };

    private static string? SerializeIdentification(VisionIdentification? identification) =>
        identification is null ? null : JsonSerializer.Serialize(identification, Json);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ModifyQueueItem Map(QueueRow row) => new()
    {
        Id = row.Id,
        SessionId = row.SessionId,
        OriginalImageId = row.OriginalImageId,
        ImageId = row.ImageId,
        IsMultiPhoto = row.IsMultiPhoto != 0,
        BrowserUploadToken = row.BrowserUploadToken,
        Status = (ModifyQueueStatus)row.Status,
        Identification = string.IsNullOrWhiteSpace(row.IdentificationJson)
            ? null
            : JsonSerializer.Deserialize<VisionIdentification>(row.IdentificationJson, Json),
        MatchedItemId = row.MatchedItemId,
        MatchedItemName = row.MatchedItemName,
        MatchConfidence = (MatchConfidence)row.MatchConfidence,
        MatchReason = row.MatchReason,
        MatchedItemUpdatedAt = row.MatchedItemUpdatedAt,
        AppliedAction = row.AppliedAction is null ? null : (ModifyAction)row.AppliedAction,
        Error = row.Error,
        CreatedAt = DateTimeOffset.Parse(row.CreatedAt),
        ProcessingStartedAt = ParseOffset(row.ProcessingStartedAt),
        ProcessedAt = ParseOffset(row.ProcessedAt),
        ReviewedAt = ParseOffset(row.ReviewedAt)
    };

    private static ModifySession Map(SessionRow row) => new(
        row.Id,
        DateTimeOffset.Parse(row.StartedAt),
        ParseOffset(row.EndedAt),
        row.WorkingLocationId,
        row.WorkingContainerId);

    private static DateTimeOffset? ParseOffset(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.Parse(value);

    private const string SelectQueue = """
        SELECT q.Id, q.SessionId, q.OriginalImageId, q.ImageId, q.IsMultiPhoto,
               q.BrowserUploadToken, q.Status, q.IdentificationJson,
               q.MatchedItemId, q.MatchedItemName, q.MatchConfidence, q.MatchReason,
               q.MatchedItemUpdatedAt, q.AppliedAction, q.Error, q.CreatedAt,
               q.ProcessingStartedAt, q.ProcessedAt, q.ReviewedAt
        FROM ModifyQueueItems q
        """;

    private sealed record ResultRow(
        int ImageId,
        VisionIdentification? Identification,
        int? MatchedItemId,
        string? MatchedItemName,
        MatchConfidence Confidence,
        string? MatchReason,
        string? MatchedItemUpdatedAt);

    private sealed class QueueRow
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public int OriginalImageId { get; set; }
        public int ImageId { get; set; }
        public int IsMultiPhoto { get; set; }
        public string? BrowserUploadToken { get; set; }
        public int Status { get; set; }
        public string? IdentificationJson { get; set; }
        public int? MatchedItemId { get; set; }
        public string? MatchedItemName { get; set; }
        public int MatchConfidence { get; set; }
        public string? MatchReason { get; set; }
        public string? MatchedItemUpdatedAt { get; set; }
        public int? AppliedAction { get; set; }
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
        public int? WorkingLocationId { get; set; }
        public int? WorkingContainerId { get; set; }
    }
}
