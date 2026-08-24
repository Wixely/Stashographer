using Stashographer.Data.Entities;
using Stashographer.Services.Config;
using Stashographer.Services.Images;
using Stashographer.Services.Inventory;

namespace Stashographer.Services.Ai;

public enum IntakeAction
{
    /// <summary>High confidence product match: add another physical instance.</summary>
    IncrementExisting,

    /// <summary>Ambiguous: the user should pick between candidates (or create new).</summary>
    ChooseCandidate,

    /// <summary>No match: create a new item from the AI-filled draft.</summary>
    CreateNew,

    /// <summary>Another view of the same physical object: attach its image without changing quantity.</summary>
    AttachImage,

    /// <summary>Same product with a distinct observed expiry: create a linked stock lot.</summary>
    CreateStockLot
}

/// <summary>The pipeline's verdict for one photographed item.</summary>
public record IntakeProposal(
    IntakeAction Action,
    int? MatchedItemId,
    string? MatchedItemName,
    decimal IncrementBy,
    Item Draft);

/// <summary>Everything the UI needs to render/apply one intake: verdict + context.</summary>
public record IntakeResult(
    int ImageId,
    VisionIdentification? Identification,
    IntakeProposal Proposal,
    List<Item> Candidates);

/// <summary>What was applied, kept so the UI can offer Undo.</summary>
public record IntakeApplied(IntakeAction Action, int ItemId, string ItemName, decimal By);

/// <summary>
/// Orchestrates photo → identify → match → increment/create. Matching is name-first with an
/// LLM visual confirm; the confidence rules (barcode/exact-name short-circuits, when to ask
/// the model) live here in code, not in the model.
/// </summary>
public class PhotoIntakeService(
    IAiEnrichmentService ai,
    InventoryService inventory,
    ImageService images,
    ILogger<PhotoIntakeService> logger,
    SettingsService? settings = null)
{
    private const int ThumbnailWidthForMatching = 240;
    private const int MaxCandidateThumbnails = 4;
    private const int MultiConcurrency = 3;
    private const int MaxItemsPerPhoto = 30;

    // --- Single item ----------------------------------------------------------------

    public async Task<IntakeResult> ProcessSingleAsync(Stream content, string mediaType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var stored = await images.SaveAsync(new MemoryStream(bytes), mediaType, "photo-intake", null, ct);
        return await ProcessSingleStoredAsync(stored.Id, bytes, stored.ContentType, null, ct);
    }

    /// <summary>Processes an image that was already durably stored by the intake queue.</summary>
    public async Task<IntakeResult> ProcessStoredAsync(
        int imageId, string? intakeContext = null, CancellationToken ct = default)
    {
        var stored = await images.GetAsync(imageId, ct)
            ?? throw new InvalidOperationException("The queued image no longer exists.");
        var bytes = await images.ReadOriginalBytesAsync(imageId, ct)
            ?? throw new InvalidOperationException("The queued image file could not be read.");
        return await ProcessSingleStoredAsync(imageId, bytes, stored.ContentType, intakeContext, ct);
    }

    private async Task<IntakeResult> ProcessSingleStoredAsync(
        int imageId, byte[] bytes, string mediaType, string? intakeContext, CancellationToken ct)
    {
        var regional = await GetRegionalOptionsAsync(ct);
        var boxes = PrepareDetectedBoxes(await ai.DetectItemsAsync(bytes, mediaType, ct));
        var focus = PickSingleItemBox(boxes);
        if (focus is not null)
        {
            try
            {
                var crop = await images.CropAsync(imageId, focus.X, focus.Y, focus.W, focus.H,
                    padding: 0.08, targetAspectRatio: 1, ct: ct);
                if (crop is not null)
                {
                    var cropBytes = await images.ReadOriginalBytesAsync(crop.Id, ct);
                    if (cropBytes is not null)
                        return await ProcessBytesAsync(crop.Id, cropBytes, crop.ContentType, intakeContext, regional, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not crop the single-item photo; using the original image");
            }
        }

        return await ProcessBytesAsync(imageId, bytes, mediaType, intakeContext, regional, ct);
    }

    private async Task<IntakeResult> ProcessBytesAsync(
        int imageId, byte[] bytes, string mediaType, string? intakeContext,
        InventoryRegionalOptions regional, CancellationToken ct)
    {
        var kinds = await inventory.GetKindsAsync(ct);
        var identification = await ai.IdentifyItemAsync(
            bytes, mediaType, kinds.Select(k => k.Name).ToList(), ct, intakeContext,
            new AiRegionalContext(regional.DefaultCurrency, regional.DateOrder.ToString(),
                regional.CultureName, regional.TimeZoneId, regional.Today()));

        if (identification is null || string.IsNullOrWhiteSpace(identification.Name))
        {
            // Model couldn't tell — fall back to a blank create card so nothing is lost.
            return new IntakeResult(imageId, identification,
                new IntakeProposal(IntakeAction.CreateNew, null, null, 1,
                    BuildDraft(imageId, identification, kinds, regional)),
                new List<Item>());
        }

        var candidates = await inventory.FindCandidatesAsync(identification.Name, identification.Barcode, ct: ct);
        var draft = BuildDraft(imageId, identification, kinds, regional);
        var by = Math.Max(1, identification.Count);

        // Rule 1: exact barcode match → HIGH, no model call.
        if (identification.Barcode is { } code)
        {
            var barcodeHits = candidates.Where(c => c.Code == code).ToList();
            if (TryResolveObservedLot(barcodeHits, out var lotResult))
                return lotResult;
            if (barcodeHits.Count == 1)
                return Result(MatchAction(barcodeHits[0]), barcodeHits[0]);
            if (barcodeHits.Count > 1)
                return Result(IntakeAction.ChooseCandidate, null);
        }

        // Rule 2: exactly one candidate whose normalized name equals the identification → HIGH.
        var normName = InventoryService.NormalizeName(identification.Name);
        var exactNames = candidates.Where(c => InventoryService.NormalizeName(c.Name) == normName).ToList();
        if (TryResolveObservedLot(exactNames, out var exactLotResult))
            return exactLotResult;
        if (exactNames.Count == 1)
            return Result(MatchAction(exactNames[0]), exactNames[0]);
        if (exactNames.Count > 1
            && exactNames.Any(x => x.CollectionKey is not null)
            && exactNames.Select(x => x.CollectionKey).Distinct().Count() == 1)
            return Result(IntakeAction.ChooseCandidate, null);

        // Rule 3: nothing similar at all → create.
        if (candidates.Count == 0)
            return Result(IntakeAction.CreateNew, null);

        // Rule 4: ask the model to compare the photo against the candidates (with thumbnails).
        var pick = await ai.PickMatchAsync(bytes, mediaType, identification,
            await ToMatchCandidatesAsync(candidates, ct), ct);
        var picked = pick?.MatchedItemId is { } id ? candidates.FirstOrDefault(c => c.Id == id) : null;

        return (pick?.Confidence, picked) switch
        {
            (MatchConfidence.High, not null) => Result(MatchAction(picked), picked),
            (MatchConfidence.Medium, not null) => Result(IntakeAction.ChooseCandidate, picked),
            _ => Result(IntakeAction.CreateNew, null)
        };

        IntakeAction MatchAction(Item matched) =>
            InventoryService.RequiresSeparateStockLot(matched, draft)
                ? IntakeAction.CreateStockLot
                : IntakeAction.IncrementExisting;

        bool TryResolveObservedLot(List<Item> matches, out IntakeResult result)
        {
            result = null!;
            if (SpecialAttributeCatalog.GetExpiry(draft)?.DateValue is null || matches.Count <= 1)
                return false;

            var compatible = matches
                .Where(match => !InventoryService.RequiresSeparateStockLot(match, draft))
                .ToList();
            if (compatible.Count == 1)
            {
                result = Result(IntakeAction.IncrementExisting, compatible[0]);
                return true;
            }
            if (compatible.Count > 1) return false;

            var collectionKeys = matches.Select(match => match.CollectionKey).Distinct().ToList();
            if (collectionKeys.Count != 1 || string.IsNullOrWhiteSpace(collectionKeys[0]))
                return false;
            result = Result(IntakeAction.CreateStockLot, matches[0]);
            return true;
        }

        IntakeResult Result(IntakeAction action, Item? matched) => new(
            imageId, identification,
            new IntakeProposal(action, matched?.Id, matched?.Name, by, draft),
            candidates);
    }

    // --- Multi item -----------------------------------------------------------------

    public async Task<List<IntakeResult>> ProcessMultiAsync(Stream content, string mediaType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var fullPhoto = await images.SaveAsync(new MemoryStream(bytes), mediaType, "photo-intake-multi", null, ct);

        return await ProcessMultiStoredAsync(fullPhoto.Id, null, ct);
    }

    /// <summary>Runs multi-item detection against a photo already persisted by the queue.</summary>
    public async Task<List<IntakeResult>> ProcessMultiStoredAsync(
        int imageId, string? intakeContext = null, CancellationToken ct = default)
    {
        var fullPhoto = await images.GetAsync(imageId, ct)
            ?? throw new InvalidOperationException("The queued image no longer exists.");
        var bytes = await images.ReadOriginalBytesAsync(imageId, ct)
            ?? throw new InvalidOperationException("The queued image file could not be read.");

        var regional = await GetRegionalOptionsAsync(ct);
        var boxes = PrepareDetectedBoxes(await ai.DetectItemsAsync(bytes, fullPhoto.ContentType, ct));
        if (boxes.Count == 0)
        {
            // Nothing detected — treat the whole photo as one item rather than losing it.
            logger.LogInformation("Multi-item detection found nothing; falling back to single-item flow");
            return new List<IntakeResult>
            {
                await ProcessBytesAsync(fullPhoto.Id, bytes, fullPhoto.ContentType, intakeContext, regional, ct)
            };
        }

        var semaphore = new SemaphoreSlim(MultiConcurrency);
        var tasks = boxes.Select(async box =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                // Give every detected object its own square-ish source image. The crop expands
                // around the box rather than cutting into it, retaining a little visual context.
                var crop = await images.CropAsync(fullPhoto.Id, box.X, box.Y, box.W, box.H,
                    padding: 0.08, targetAspectRatio: 1, ct: ct);
                if (crop is null) return null;
                var cropBytes = await images.ReadOriginalBytesAsync(crop.Id, ct);
                if (cropBytes is null) return null;
                return await ProcessBytesAsync(crop.Id, cropBytes, crop.ContentType, intakeContext, regional, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Intake failed for detected box {Label}", box.Label);
                return null;
            }
            finally { semaphore.Release(); }
        });

        return (await Task.WhenAll(tasks)).Where(r => r is not null).Select(r => r!).ToList();
    }

    /// <summary>
    /// Clamps detector output, removes only near-identical duplicate boxes, limits runaway
    /// model output, and gives queue review a stable top-to-bottom/left-to-right order.
    /// Adjacent or identical products remain separate entries.
    /// </summary>
    internal static List<DetectedBox> PrepareDetectedBoxes(IEnumerable<DetectedBox> detected)
    {
        var prepared = detected
            .Select(box =>
            {
                var x = Math.Clamp(box.X, 0, 1);
                var y = Math.Clamp(box.Y, 0, 1);
                var w = Math.Clamp(box.W, 0, 1 - x);
                var h = Math.Clamp(box.H, 0, 1 - y);
                return new DetectedBox(box.Label, x, y, w, h);
            })
            .Where(box => box.W > 0.01 && box.H > 0.01)
            .OrderBy(box => Math.Round(box.Y / 0.1, MidpointRounding.AwayFromZero))
            .ThenBy(box => box.X)
            .ToList();

        var unique = new List<DetectedBox>();
        foreach (var box in prepared)
        {
            if (unique.Any(existing => IntersectionOverUnion(existing, box) >= 0.9)) continue;
            unique.Add(box);
            if (unique.Count == MaxItemsPerPhoto) break;
        }
        return unique;
    }

    /// <summary>Selects the dominant, then most central object when single-item mode sees several boxes.</summary>
    internal static DetectedBox? PickSingleItemBox(IReadOnlyList<DetectedBox> boxes) =>
        boxes
            .OrderByDescending(box => box.W * box.H)
            .ThenBy(box => Math.Pow(box.X + box.W / 2 - 0.5, 2)
                           + Math.Pow(box.Y + box.H / 2 - 0.5, 2))
            .FirstOrDefault();

    private static double IntersectionOverUnion(DetectedBox a, DetectedBox b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.W, b.X + b.W);
        var bottom = Math.Min(a.Y + a.H, b.Y + b.H);
        var intersection = Math.Max(0, right - left) * Math.Max(0, bottom - top);
        var union = a.W * a.H + b.W * b.H - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    // --- Apply / undo ----------------------------------------------------------------

    /// <summary>
    /// Applies a proposal (increment or create), optionally overriding the pipeline's verdict
    /// (used by the review board and candidate picker). Returns what happened, for Undo.
    /// </summary>
    public async Task<IntakeApplied> ApplyAsync(
        IntakeResult result, IntakeAction? actionOverride = null, int? matchedItemIdOverride = null,
        CancellationToken ct = default)
    {
        var proposal = result.Proposal;
        var action = actionOverride ?? proposal.Action;
        var matchedId = matchedItemIdOverride ?? proposal.MatchedItemId;

        if (action == IntakeAction.CreateStockLot && matchedId is { } lotTargetId)
        {
            var lot = await inventory.CreateStockLotAsync(lotTargetId, proposal.Draft, ct);
            return new IntakeApplied(IntakeAction.CreateStockLot, lot.Id, lot.Name, lot.Quantity);
        }

        if (action != IntakeAction.CreateNew && matchedId is { } id)
        {
            var existing = await inventory.GetAsync(id, ct)
                ?? throw new InvalidOperationException("The selected inventory item no longer exists.");
            if (InventoryService.RequiresSeparateStockLot(existing, proposal.Draft))
            {
                var lot = await inventory.CreateStockLotAsync(id, proposal.Draft, ct);
                return new IntakeApplied(IntakeAction.CreateStockLot, lot.Id, lot.Name, lot.Quantity);
            }
            if (SpecialAttributeCatalog.MergeMissing(existing, proposal.Draft))
                await inventory.SaveAsync(existing, ct);
            await inventory.AdjustQuantityAsync(id, proposal.IncrementBy, ct);
            var name = existing.Name;
            return new IntakeApplied(IntakeAction.IncrementExisting, id, name, proposal.IncrementBy);
        }

        var created = await inventory.SaveAsync(proposal.Draft, ct);
        return new IntakeApplied(IntakeAction.CreateNew, created.Id, created.Name, proposal.IncrementBy);
    }

    /// <summary>Reverses an applied intake: un-increments, or deletes the created item.</summary>
    public async Task UndoAsync(IntakeApplied applied, CancellationToken ct = default)
    {
        if (applied.Action == IntakeAction.IncrementExisting)
            await inventory.AdjustQuantityAsync(applied.ItemId, -applied.By, ct);
        else
            await inventory.DeleteAsync(applied.ItemId, ct);
    }

    // --- Helpers ----------------------------------------------------------------------

    private static Item BuildDraft(
        int imageId, VisionIdentification? ident, List<ItemKind> kinds, InventoryRegionalOptions regional)
    {
        var kindId = kinds.FirstOrDefault(k =>
                k.Name.Equals(ident?.Kind, StringComparison.OrdinalIgnoreCase))?.Id
            ?? kinds.FirstOrDefault(k => k.Name == "Other")?.Id
            ?? kinds.FirstOrDefault()?.Id
            ?? 7;

        var draft = new Item
        {
            Name = ident?.Name ?? string.Empty,
            Description = ident?.Description,
            ItemKindId = kindId,
            Quantity = Math.Max(1, ident?.Count ?? 1),
            Code = ident?.Barcode,
            ImageId = imageId,
            Attributes = ident is null ? new() : new(ident.Attributes)
        };
        if (ident?.PriceAmount is { } price)
        {
            var defaulted = string.IsNullOrWhiteSpace(ident.PriceCurrency);
            SpecialAttributeCatalog.SetPrice(draft, price,
                defaulted ? regional.DefaultCurrency : ident.PriceCurrency,
                new SpecialAttributeEvidence
                {
                    Source = "ai-vision",
                    SourceImageId = imageId,
                    Assumptions = defaulted ? ["currency:default"] : []
                });
        }
        if (ident?.Expiry is { } expiry)
        {
            var usedRegionalParser = RegionalDateParser.TryParseVisibleDate(
                expiry.RawText, regional.DateOrder, regional.Today(), out var parsedDate);
            var date = usedRegionalParser ? parsedDate : expiry.Date;
            if (date is not null)
                SpecialAttributeCatalog.SetExpiry(draft, date, ExpiryKind(expiry.Type),
                    new SpecialAttributeEvidence
                    {
                        Source = "ai-vision",
                        SourceImageId = imageId,
                        RawText = expiry.RawText,
                        Confidence = expiry.Confidence,
                        Convention = regional.DateOrder.ToString(),
                        Assumptions = usedRegionalParser ? [$"date-order:{regional.DateOrder}"] : []
                    });
        }
        return draft;
    }

    private static ExpiryDateKind ExpiryKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "use_by" or "useby" => ExpiryDateKind.UseBy,
        "best_before" or "bestbefore" => ExpiryDateKind.BestBefore,
        _ => ExpiryDateKind.Unknown
    };

    private async Task<InventoryRegionalOptions> GetRegionalOptionsAsync(CancellationToken ct) =>
        settings is null ? new InventoryRegionalOptions() : await settings.GetRegionalOptionsAsync(ct);

    private async Task<List<MatchCandidate>> ToMatchCandidatesAsync(List<Item> candidates, CancellationToken ct)
    {
        var result = new List<MatchCandidate>();
        var withThumbs = 0;
        foreach (var c in candidates)
        {
            byte[]? thumb = null;
            string? thumbType = null;
            if (c.ImageId is { } imgId && withThumbs < MaxCandidateThumbnails)
            {
                var t = await images.GetThumbnailAsync(imgId, ThumbnailWidthForMatching, ct);
                if (t is not null)
                {
                    thumb = t.Value.Bytes;
                    thumbType = t.Value.ContentType;
                    withThumbs++;
                }
            }
            result.Add(new MatchCandidate(c.Id, c.Name, c.Attributes, thumb, thumbType));
        }
        return result;
    }

    /// <summary>
    /// Compares a processed capture with recent same-session photos. The model is deliberately
    /// given instance-level evidence and its selected queue id is validated before use.
    /// </summary>
    public async Task<CaptureRelationshipPick?> ClassifyCaptureRelationshipAsync(
        IntakeResult result,
        IReadOnlyList<RecentCaptureCandidate> recentCaptures,
        CancellationToken ct = default)
    {
        if (!ai.IsEnabled || result.Identification is null || recentCaptures.Count == 0)
            return null;

        var stored = await images.GetAsync(result.ImageId, ct);
        var bytes = await images.ReadOriginalBytesAsync(result.ImageId, ct);
        if (stored is null || bytes is null) return null;

        var candidates = new List<CaptureMatchCandidate>();
        foreach (var recent in recentCaptures.Take(6))
        {
            var thumbnail = await images.GetThumbnailAsync(recent.ImageId, ThumbnailWidthForMatching, ct);
            if (thumbnail is null) continue;
            candidates.Add(new CaptureMatchCandidate(
                recent.QueueItemId,
                recent.Name,
                recent.Attributes,
                thumbnail.Value.Bytes,
                thumbnail.Value.ContentType));
        }
        if (candidates.Count == 0) return null;

        var pick = await ai.ClassifyCaptureRelationshipAsync(
            bytes, stored.ContentType, result.Identification, candidates, ct);
        return pick?.QueueItemId is { } selected
               && candidates.Any(candidate => candidate.QueueItemId == selected)
            ? pick
            : null;
    }
}
