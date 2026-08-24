using System.Text.Json;
using Dapper;
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

    public async Task<IntakeQueueItem> EnqueueBarcodeAsync(string code, CancellationToken ct = default)
    {
        code = code.Trim();
        if (code.Length == 0) throw new ArgumentException("A barcode or ISBN is required.", nameof(code));

        var item = new IntakeQueueItem
        {
            SessionId = await GetOrCreateActiveSessionIdAsync(ct),
            SourceType = IntakeSourceType.Barcode,
            SourceCode = code,
            Status = IntakeQueueStatus.Pending,
            Draft = new Item { Name = string.Empty, Code = code, ItemKindId = 7 },
            CreatedAt = DateTimeOffset.UtcNow
        };
        item.Id = await InsertAsync(item, ct);
        signal.Pulse();
        return item;
    }

    public async Task<IntakeQueueItem> EnqueuePhotoAsync(
        Stream content, string mediaType, string? originalName, bool multipleItems = true,
        CancellationToken ct = default)
    {
        var stored = await images.SaveAsync(content, mediaType, originalName, null, ct);
        var item = new IntakeQueueItem
        {
            SessionId = await GetOrCreateActiveSessionIdAsync(ct),
            SourceType = IntakeSourceType.Photo,
            ImageId = stored.Id,
            IsMultiPhoto = multipleItems,
            Status = IntakeQueueStatus.Pending,
            Draft = new Item { Name = string.Empty, ItemKindId = 7, ImageId = stored.Id },
            CreatedAt = DateTimeOffset.UtcNow
        };
        item.Id = await InsertAsync(item, ct);
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
                WHERE Id = @id AND Status IN (@pending, @failed);
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
            List<ProcessedCapture> processed;
            if (queued.SourceType == IntakeSourceType.Barcode)
            {
                processed = new List<ProcessedCapture> { await ProcessBarcodeAsync(queued, ct) };
            }
            else if (queued.SourceType == IntakeSourceType.Photo)
            {
                if (!aiEnabled)
                    throw new InvalidOperationException("Configure an AI vision model, or enter this item manually.");
                processed = await ProcessPhotoAsync(queued, options.ContextItemCount, ct);
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
                             && x.Capture.Action != IntakeAction.ChooseCandidate))
                {
                    await AcceptAsync(entry.Id, entry.Capture.Draft,
                        entry.Capture.Action == IntakeAction.IncrementExisting
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
              AND ((SourceType = @barcode AND @barcodes = 1)
                OR (SourceType = @photo AND @photos = 1 AND @ai = 1))
            ORDER BY Id LIMIT 1;
            """, new
        {
            pending = (int)IntakeQueueStatus.Pending,
            barcode = (int)IntakeSourceType.Barcode,
            photo = (int)IntakeSourceType.Photo,
            barcodes = options.AutoProcessBarcodes ? 1 : 0,
            photos = options.AutoProcessPhotos ? 1 : 0,
            ai = aiEnabled ? 1 : 0
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
            UPDATE IntakeQueueItems SET Status = @pending, Error = NULL,
                ProcessingStartedAt = NULL, ProcessedAt = NULL
            WHERE Id = @id AND Status = @failed;
            """, new { id, pending = (int)IntakeQueueStatus.Pending, failed = (int)IntakeQueueStatus.Failed });
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

    public async Task<IntakeApplied> AcceptAsync(
        int id, Item draft, int? matchedItemId, CancellationToken ct = default)
    {
        var queued = await GetAsync(id) ?? throw new InvalidOperationException("Queue item was not found.");
        if (queued.Status == IntakeQueueStatus.Accepted)
            throw new InvalidOperationException("This queue item was already accepted.");
        if (queued.Status == IntakeQueueStatus.Rejected)
            throw new InvalidOperationException("This queue item was rejected.");

        IntakeApplied applied;
        if (matchedItemId is { } existingId)
        {
            var existing = await inventory.GetAsync(existingId)
                ?? throw new InvalidOperationException("The selected inventory item no longer exists.");
            if (SpecialAttributeCatalog.MergeMissing(existing, draft))
                await inventory.SaveAsync(existing, ct);
            await inventory.AdjustQuantityAsync(existingId, queued.IncrementBy, ct);
            if (draft.LocationId is not null || draft.ContainerId is not null)
                await inventory.MoveItemsAsync([existingId], draft.LocationId, draft.ContainerId, ct);
            applied = new IntakeApplied(IntakeAction.IncrementExisting, existingId, existing.Name, queued.IncrementBy);
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
        var result = await lookup.LookupAsync(code, ct);
        var draft = result.Found
            ? new Item
            {
                Name = result.Name ?? string.Empty,
                Code = result.Code,
                Description = result.Description,
                ThumbnailUrl = result.ThumbnailUrl,
                Attributes = new(result.Attributes),
                ItemKindId = KindId(result.SuggestedKind)
            }
            : new Item { Name = string.Empty, Code = code, ItemKindId = 7 };

        var candidates = await inventory.FindCandidatesAsync(draft.Name, code, ct: ct);
        var exact = candidates
            .Where(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return exact.Count switch
        {
            0 => new ProcessedCapture(draft, IntakeAction.CreateNew, null, null, 1),
            1 => new ProcessedCapture(draft, IntakeAction.IncrementExisting, exact[0].Id, exact[0].Name, 1),
            _ => new ProcessedCapture(draft, IntakeAction.ChooseCandidate, null, null, 1)
        };
    }

    private async Task<List<ProcessedCapture>> ProcessPhotoAsync(
        IntakeQueueItem queued, int contextCount, CancellationToken ct)
    {
        if (queued.ImageId is not { } imageId)
            throw new InvalidOperationException("Queued photo has no stored image.");

        var recent = await GetRecentDraftsAsync(queued, Math.Clamp(contextCount, 0, 25), ct);
        string? context = null;
        if (recent.Count > 0)
        {
            var descriptions = new List<string>();
            foreach (var item in recent) descriptions.Add(await DescribeAsync(item, ct));
            context = "Recent captures (newest first):\n" + string.Join("\n", descriptions);
        }
        var results = queued.IsMultiPhoto
            ? await photoIntake.ProcessMultiStoredAsync(imageId, context, ct)
            : new List<IntakeResult> { await photoIntake.ProcessStoredAsync(imageId, context, ct) };
        return results.Select(result => new ProcessedCapture(
                result.Proposal.Draft,
                result.Proposal.Action,
                result.Proposal.MatchedItemId,
                result.Proposal.MatchedItemName,
                result.Proposal.IncrementBy))
            .ToList();
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
            UPDATE IntakeQueueItems SET Status = @Ready, DraftJson = @Draft, ImageId = @ImageId,
                IsMultiPhoto = 0,
                ProposalAction = @Action, MatchedItemId = @MatchedId,
                MatchedItemName = @MatchedName, IncrementBy = @IncrementBy,
                ProcessedAt = @Now, Error = NULL
            WHERE Id = @Id;
            """, new
        {
            Id = id,
            Ready = (int)IntakeQueueStatus.ReadyForReview,
            Draft = JsonSerializer.Serialize(processed.Draft, Json),
            ImageId = processed.Draft.ImageId,
            Action = (int)processed.Action,
            MatchedId = processed.MatchedItemId,
            MatchedName = processed.MatchedItemName,
            IncrementBy = processed.IncrementBy,
            Now = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private async Task<int> InsertProcessedAsync(
        IntakeQueueItem source, ProcessedCapture processed, CancellationToken ct)
    {
        using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("""
            INSERT INTO IntakeQueueItems
                (SessionId, SourceType, ImageId, IsMultiPhoto, Status, DraftJson, ProposalAction,
                 MatchedItemId, MatchedItemName, IncrementBy, CreatedAt, ProcessedAt)
            VALUES
                (@SessionId, @SourceType, @ImageId, 0, @Status, @Draft, @Action,
                 @MatchedId, @MatchedName, @IncrementBy, @CreatedAt, @ProcessedAt);
            SELECT last_insert_rowid();
            """, new
        {
            SessionId = source.SessionId,
            SourceType = (int)IntakeSourceType.Photo,
            ImageId = processed.Draft.ImageId,
            Status = (int)IntakeQueueStatus.ReadyForReview,
            Draft = JsonSerializer.Serialize(processed.Draft, Json),
            Action = (int)processed.Action,
            MatchedId = processed.MatchedItemId,
            MatchedName = processed.MatchedItemName,
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
                (SessionId, SourceType, SourceCode, ImageId, IsMultiPhoto, Status, DraftJson,
                 ProposalAction, IncrementBy, CreatedAt, ProcessedAt)
            VALUES
                (@SessionId, @SourceType, @SourceCode, @ImageId, @IsMultiPhoto, @Status, @DraftJson,
                 @ProposalAction, @IncrementBy, @CreatedAt, @ProcessedAt);
            SELECT last_insert_rowid();
            """, new
        {
            item.SessionId,
            SourceType = (int)item.SourceType,
            item.SourceCode,
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

    private static IntakeQueueItem Map(QueueRow row) => new()
    {
        Id = row.Id,
        SessionId = row.SessionId,
        SourceType = (IntakeSourceType)row.SourceType,
        SourceCode = row.SourceCode,
        ImageId = row.ImageId,
        IsMultiPhoto = row.IsMultiPhoto,
        Status = (IntakeQueueStatus)row.Status,
        Draft = DeserializeDraft(row.DraftJson),
        ProposalAction = row.ProposalAction is { } action ? (IntakeAction)action : null,
        MatchedItemId = row.MatchedItemId,
        MatchedItemName = row.MatchedItemName,
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
        SELECT q.Id, q.SessionId, q.SourceType, q.SourceCode, q.ImageId, q.IsMultiPhoto, q.Status,
               q.DraftJson, q.ProposalAction, q.MatchedItemId, q.MatchedItemName,
               q.IncrementBy, q.AppliedItemId, q.Error, q.CreatedAt,
               q.ProcessingStartedAt, q.ProcessedAt, q.ReviewedAt
        FROM IntakeQueueItems q
        """;

    private sealed class QueueRow
    {
        public int Id { get; set; }
        public int SessionId { get; set; }
        public int SourceType { get; set; }
        public string? SourceCode { get; set; }
        public int? ImageId { get; set; }
        public bool IsMultiPhoto { get; set; }
        public int Status { get; set; }
        public string? DraftJson { get; set; }
        public int? ProposalAction { get; set; }
        public int? MatchedItemId { get; set; }
        public string? MatchedItemName { get; set; }
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

    private record ProcessedCapture(
        Item Draft,
        IntakeAction Action,
        int? MatchedItemId,
        string? MatchedItemName,
        decimal IncrementBy);
}
