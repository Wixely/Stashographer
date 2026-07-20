using Stashographer.Data.Entities;
using Stashographer.Services.Images;
using Stashographer.Services.Inventory;

namespace Stashographer.Services.Ai;

public enum IntakeAction
{
    /// <summary>High confidence: increment the matched item (auto-applied by the UI).</summary>
    IncrementExisting,

    /// <summary>Ambiguous: the user should pick between candidates (or create new).</summary>
    ChooseCandidate,

    /// <summary>No match: create a new item from the AI-filled draft.</summary>
    CreateNew
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
    ILogger<PhotoIntakeService> logger)
{
    private const int ThumbnailWidthForMatching = 240;
    private const int MaxCandidateThumbnails = 4;
    private const int MultiConcurrency = 3;

    // --- Single item ----------------------------------------------------------------

    public async Task<IntakeResult> ProcessSingleAsync(Stream content, string mediaType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var stored = await images.SaveAsync(new MemoryStream(bytes), mediaType, "photo-intake", null, ct);
        return await ProcessBytesAsync(stored.Id, bytes, stored.ContentType, ct);
    }

    private async Task<IntakeResult> ProcessBytesAsync(int imageId, byte[] bytes, string mediaType, CancellationToken ct)
    {
        var kinds = await inventory.GetKindsAsync(ct);
        var identification = await ai.IdentifyItemAsync(bytes, mediaType, kinds.Select(k => k.Name).ToList(), ct);

        if (identification is null || string.IsNullOrWhiteSpace(identification.Name))
        {
            // Model couldn't tell — fall back to a blank create card so nothing is lost.
            return new IntakeResult(imageId, identification,
                new IntakeProposal(IntakeAction.CreateNew, null, null, 1,
                    BuildDraft(imageId, identification, kinds)),
                new List<Item>());
        }

        var candidates = await inventory.FindCandidatesAsync(identification.Name, identification.Barcode, ct: ct);
        var draft = BuildDraft(imageId, identification, kinds);
        var by = Math.Max(1, identification.Count);

        // Rule 1: exact barcode match → HIGH, no model call.
        if (identification.Barcode is { } code)
        {
            var barcodeHit = candidates.FirstOrDefault(c => c.Code == code);
            if (barcodeHit is not null)
                return Result(IntakeAction.IncrementExisting, barcodeHit);
        }

        // Rule 2: exactly one candidate whose normalized name equals the identification → HIGH.
        var normName = InventoryService.NormalizeName(identification.Name);
        var exactNames = candidates.Where(c => InventoryService.NormalizeName(c.Name) == normName).ToList();
        if (exactNames.Count == 1)
            return Result(IntakeAction.IncrementExisting, exactNames[0]);

        // Rule 3: nothing similar at all → create.
        if (candidates.Count == 0)
            return Result(IntakeAction.CreateNew, null);

        // Rule 4: ask the model to compare the photo against the candidates (with thumbnails).
        var pick = await ai.PickMatchAsync(bytes, mediaType, identification,
            await ToMatchCandidatesAsync(candidates, ct), ct);
        var picked = pick?.MatchedItemId is { } id ? candidates.FirstOrDefault(c => c.Id == id) : null;

        return (pick?.Confidence, picked) switch
        {
            (MatchConfidence.High, not null) => Result(IntakeAction.IncrementExisting, picked),
            (MatchConfidence.Medium, not null) => Result(IntakeAction.ChooseCandidate, picked),
            _ => Result(IntakeAction.CreateNew, null)
        };

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

        var boxes = await ai.DetectItemsAsync(bytes, fullPhoto.ContentType, ct);
        if (boxes.Count == 0)
        {
            // Nothing detected — treat the whole photo as one item rather than losing it.
            logger.LogInformation("Multi-item detection found nothing; falling back to single-item flow");
            return new List<IntakeResult> { await ProcessBytesAsync(fullPhoto.Id, bytes, fullPhoto.ContentType, ct) };
        }

        var semaphore = new SemaphoreSlim(MultiConcurrency);
        var tasks = boxes.Select(async box =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var crop = await images.CropAsync(fullPhoto.Id, box.X, box.Y, box.W, box.H, ct: ct);
                if (crop is null) return null;
                var cropBytes = await images.ReadOriginalBytesAsync(crop.Id, ct);
                if (cropBytes is null) return null;
                return await ProcessBytesAsync(crop.Id, cropBytes, crop.ContentType, ct);
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

        if (action != IntakeAction.CreateNew && matchedId is { } id)
        {
            await inventory.AdjustQuantityAsync(id, proposal.IncrementBy, ct);
            var name = result.Candidates.FirstOrDefault(c => c.Id == id)?.Name ?? proposal.MatchedItemName ?? "item";
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

    private static Item BuildDraft(int imageId, VisionIdentification? ident, List<ItemKind> kinds)
    {
        var kindId = kinds.FirstOrDefault(k =>
                k.Name.Equals(ident?.Kind, StringComparison.OrdinalIgnoreCase))?.Id
            ?? kinds.FirstOrDefault(k => k.Name == "Other")?.Id
            ?? kinds.FirstOrDefault()?.Id
            ?? 7;

        return new Item
        {
            Name = ident?.Name ?? string.Empty,
            Description = ident?.Description,
            ItemKindId = kindId,
            Quantity = Math.Max(1, ident?.Count ?? 1),
            Code = ident?.Barcode,
            ImageId = imageId,
            Attributes = ident is null ? new() : new(ident.Attributes)
        };
    }

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
}
