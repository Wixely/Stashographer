using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Stashographer.Data.Entities;
using Stashographer.Services.Ai;
using Stashographer.Services.Config;
using Stashographer.Services.Images;
using Stashographer.Services.Intake;
using Stashographer.Services.Inventory;
using Stashographer.Services.Lookup;

namespace Stashographer.Tests;

public class IntakeQueueTests
{
    [Fact]
    public async Task Automation_draft_waits_in_queue_for_human_acceptance()
    {
        await using var harness = await Harness.CreateAsync();
        var draft = new Item
        {
            Name = "Agent-proposed cable",
            ItemKindId = 4,
            Quantity = 2,
            LocationId = 3
        };

        var queued = await harness.Queue.EnqueueDraftAsync(draft);
        var reloaded = await harness.Queue.GetAsync(queued.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(IntakeSourceType.Manual, reloaded!.SourceType);
        Assert.Equal(IntakeQueueStatus.ReadyForReview, reloaded.Status);
        Assert.Equal(IntakeAction.CreateNew, reloaded.ProposalAction);
        Assert.Equal("Agent-proposed cable", reloaded.Draft.Name);
        Assert.Empty(await harness.Inventory.QueryAsync(new ItemQuery(Search: "Agent-proposed cable")));
    }

    [Fact]
    public async Task Barcode_capture_is_persisted_then_review_accepts_it()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Lookup.Result = new LookupResult
        {
            Found = true,
            Code = "5012345678900",
            Name = "Test Cereal",
            SuggestedKind = "Grocery",
            Attributes = new() { ["Brand"] = "Example" }
        };

        var queued = await harness.Queue.EnqueueBarcodeAsync("5012345678900");
        Assert.Equal(IntakeQueueStatus.Pending, queued.Status);
        Assert.Single(await harness.Queue.GetOpenAsync());

        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: false);
        var ready = await harness.Queue.GetAsync(queued.Id);
        Assert.NotNull(ready);
        Assert.True(ready!.Status == IntakeQueueStatus.ReadyForReview, ready.Error);
        Assert.Equal("Test Cereal", ready.Draft.Name);
        Assert.Equal(1, ready.Draft.ItemKindId);

        var applied = await harness.Queue.AcceptAsync(ready.Id, ready.Draft, null);
        Assert.Equal(IntakeAction.CreateNew, applied.Action);
        Assert.Equal("Test Cereal", (await harness.Inventory.GetAsync(applied.ItemId))!.Name);
        Assert.Empty(await harness.Queue.GetOpenAsync());
    }

    [Fact]
    public async Task Existing_barcode_is_proposed_as_increment_but_waits_for_acceptance()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Baked Beans", Code = "5000157024671", ItemKindId = 1, Quantity = 2
        });
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = existing.Code, Name = existing.Name, SuggestedKind = "Grocery"
        };

        var queued = await harness.Queue.EnqueueBarcodeAsync(existing.Code!);
        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: false);

        var ready = (await harness.Queue.GetAsync(queued.Id))!;
        Assert.True(ready.Status == IntakeQueueStatus.ReadyForReview, ready.Error);
        Assert.Equal(IntakeAction.IncrementExisting, ready.ProposalAction);
        Assert.Equal(existing.Id, ready.MatchedItemId);
        Assert.Equal(2, (await harness.Inventory.GetAsync(existing.Id))!.Quantity);

        await harness.Queue.AcceptAsync(ready.Id, ready.Draft, ready.MatchedItemId);
        Assert.Equal(3, (await harness.Inventory.GetAsync(existing.Id))!.Quantity);
    }

    [Fact]
    public async Task Live_barcode_waits_for_repeat_window_and_uses_confirmed_quantity()
    {
        await using var harness = await Harness.CreateAsync();
        const string code = "5012345678999";
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = code, Name = "Bulk cereal", SuggestedKind = "Grocery"
        };

        var queued = await harness.Queue.EnqueueLiveBarcodeAsync(code, TimeSpan.FromMinutes(1));

        Assert.False(await harness.Queue.ProcessAsync(
            queued.Id, new IntakeOptions(), aiEnabled: false));
        await harness.Queue.UpdateLiveBarcodeQuantityAsync(
            queued.Id, code, 3, TimeSpan.FromMinutes(1));
        await harness.Queue.FinalizeLiveBarcodeAsync(queued.Id);
        Assert.True(await harness.Queue.ProcessAsync(
            queued.Id, new IntakeOptions(), aiEnabled: false));

        var ready = (await harness.Queue.GetAsync(queued.Id))!;
        Assert.Equal(IntakeQueueStatus.ReadyForReview, ready.Status);
        Assert.Equal(3, ready.CaptureQuantity);
        Assert.Equal(3, ready.Draft.Quantity);
        Assert.Equal(3, ready.IncrementBy);

        var applied = await harness.Queue.AcceptAsync(ready.Id, ready.Draft, null);
        Assert.Equal(3, (await harness.Inventory.GetAsync(applied.ItemId))!.Quantity);
    }

    [Fact]
    public async Task Finalized_live_barcode_auto_accepts_confirmed_increment_quantity()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Sparkling water", Code = "5012345678982", ItemKindId = 1, Quantity = 1
        });
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = existing.Code, Name = existing.Name, SuggestedKind = "Grocery"
        };

        var queued = await harness.Queue.EnqueueLiveBarcodeAsync(
            existing.Code!, TimeSpan.FromMinutes(1));
        await harness.Queue.UpdateLiveBarcodeQuantityAsync(
            queued.Id, existing.Code!, 4, TimeSpan.FromMinutes(1));
        await harness.Queue.FinalizeLiveBarcodeAsync(queued.Id);
        await harness.Queue.ProcessAsync(
            queued.Id, new IntakeOptions { RequireReview = false }, aiEnabled: false);

        Assert.Equal(IntakeQueueStatus.Accepted, (await harness.Queue.GetAsync(queued.Id))!.Status);
        Assert.Equal(5, (await harness.Inventory.GetAsync(existing.Id))!.Quantity);
    }

    [Fact]
    public async Task Queue_worker_skips_held_live_barcode_and_processes_next_capture()
    {
        await using var harness = await Harness.CreateAsync();
        var held = await harness.Queue.EnqueueLiveBarcodeAsync(
            "5012345678975", TimeSpan.FromMinutes(1));
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = "5012345678968", Name = "Next item", SuggestedKind = "Other"
        };
        var next = await harness.Queue.EnqueueBarcodeAsync("5012345678968");

        Assert.True(await harness.Queue.ProcessNextAsync(
            new IntakeOptions(), aiEnabled: false));

        Assert.Equal(IntakeQueueStatus.Pending, (await harness.Queue.GetAsync(held.Id))!.Status);
        Assert.Equal(IntakeQueueStatus.ReadyForReview, (await harness.Queue.GetAsync(next.Id))!.Status);
    }

    [Fact]
    public async Task Split_barcode_requires_location_choice_instead_of_incrementing_arbitrarily()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Storage tubs", Code = "77777777", ItemKindId = 7, Quantity = 2, LocationId = 1
        });
        await harness.Inventory.SplitAsync(existing.Id, 1, 3, null);
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = existing.Code, Name = existing.Name, SuggestedKind = "Other"
        };

        var queued = await harness.Queue.EnqueueBarcodeAsync(existing.Code!);
        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: false);

        var ready = (await harness.Queue.GetAsync(queued.Id))!;
        Assert.Equal(IntakeAction.ChooseCandidate, ready.ProposalAction);
        Assert.Null(ready.MatchedItemId);
        Assert.All(await harness.Inventory.FindCandidatesAsync(existing.Name, existing.Code),
            candidate => Assert.Equal(1, candidate.Quantity));
    }

    [Fact]
    public async Task Photo_agent_receives_prior_session_kind_and_location_context()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = "1111111111111", Name = "Pasta", SuggestedKind = "Grocery"
        };
        var first = await harness.Queue.EnqueueBarcodeAsync("1111111111111");
        await harness.Queue.ProcessAsync(first.Id, new IntakeOptions(), aiEnabled: false);
        var firstReady = (await harness.Queue.GetAsync(first.Id))!;
        Assert.True(firstReady.Status == IntakeQueueStatus.ReadyForReview, firstReady.Error);
        firstReady.Draft.LocationId = 1;
        await harness.Queue.AcceptAsync(first.Id, firstReady.Draft, null);

        harness.Ai.Identification = new VisionIdentification { Name = "Tomato Sauce", Kind = "Grocery" };
        await using var photo = await PhotoAsync();
        var second = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "sauce.png");
        await harness.Queue.ProcessAsync(second.Id, new IntakeOptions(), aiEnabled: true);

        var secondReady = (await harness.Queue.GetAsync(second.Id))!;
        Assert.Equal(IntakeQueueStatus.ReadyForReview, secondReady.Status);
        Assert.Equal(1, secondReady.Draft.LocationId);
        Assert.Contains("kind Grocery", harness.Ai.LastIntakeContext);
        Assert.Contains("stored in Kitchen", harness.Ai.LastIntakeContext);
    }

    [Fact]
    public async Task Starting_new_session_resets_agent_context()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = "2222222222222", Name = "Hammer", SuggestedKind = "Tool"
        };
        var first = await harness.Queue.EnqueueBarcodeAsync("2222222222222");
        await harness.Queue.ProcessAsync(first.Id, new IntakeOptions(), aiEnabled: false);
        await harness.Queue.StartNewSessionAsync();

        harness.Ai.Identification = new VisionIdentification { Name = "Unknown object", Kind = "Other" };
        await using var photo = await PhotoAsync();
        var second = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "object.png");
        await harness.Queue.ProcessAsync(second.Id, new IntakeOptions(), aiEnabled: true);

        Assert.Null(harness.Ai.LastIntakeContext);
    }

    [Fact]
    public async Task Review_can_be_disabled_for_complete_high_confidence_results()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Lookup.Result = new LookupResult
        {
            Found = true, Code = "3333333333333", Name = "Quick Add", SuggestedKind = "Other"
        };
        var queued = await harness.Queue.EnqueueBarcodeAsync("3333333333333");

        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions { RequireReview = false }, aiEnabled: false);

        var completed = (await harness.Queue.GetAsync(queued.Id))!;
        Assert.Equal(IntakeQueueStatus.Accepted, completed.Status);
        Assert.NotNull(completed.AppliedItemId);
        Assert.Equal("Quick Add", (await harness.Inventory.GetAsync(completed.AppliedItemId!.Value))!.Name);
    }

    [Fact]
    public async Task Multi_item_photo_fans_out_to_individual_review_records()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Ai.Boxes = new List<DetectedBox>
        {
            new("left", 0, 0, 0.45, 1),
            new("right", 0.55, 0, 0.45, 1)
        };
        harness.Ai.Identification = new VisionIdentification { Name = "Detected item", Kind = "Other" };
        await using var photo = await PhotoAsync();
        var queued = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "several.png", multipleItems: true);

        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: true);

        var open = await harness.Queue.GetOpenAsync();
        Assert.Equal(2, open.Count);
        Assert.All(open, x => Assert.Equal(IntakeQueueStatus.ReadyForReview, x.Status));
        Assert.All(open, x => Assert.False(x.IsMultiPhoto));
        Assert.All(open, x => Assert.Equal("Detected item", x.Draft.Name));
    }

    [Fact]
    public async Task Photo_capture_defaults_to_individual_item_splitting()
    {
        await using var harness = await Harness.CreateAsync();
        await using var photo = await PhotoAsync();

        var queued = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "several.png");

        Assert.True(queued.IsMultiPhoto);
        Assert.True((await harness.Queue.GetAsync(queued.Id))!.IsMultiPhoto);
    }

    [Fact]
    public async Task Queue_remembers_last_location_and_container_independently_per_session()
    {
        await using var harness = await Harness.CreateAsync();
        var session = await harness.Queue.GetCurrentSessionAsync();
        var first = await harness.Queue.EnqueueBarcodeAsync("4444444444444");
        await harness.Queue.AcceptAsync(first.Id, new Item
        {
            Name = "Loose item", ItemKindId = 7, LocationId = 1
        }, null);

        var box = await new ContainerService(harness.Db.Factory).SaveContainerAsync(new Container
        {
            Name = "Batch box", LocationId = 3
        });
        var second = await harness.Queue.EnqueueBarcodeAsync("5555555555555");
        await harness.Queue.AcceptAsync(second.Id, new Item
        {
            Name = "Boxed item", ItemKindId = 7, ContainerId = box.Id
        }, null);

        var remembered = await harness.Queue.GetRememberedDestinationsAsync(session.Id);
        Assert.Equal(1, remembered.LocationId);
        Assert.Equal(box.Id, remembered.ContainerId);

        var nextSession = await harness.Queue.StartNewSessionAsync();
        Assert.Equal(new RememberedDestinations(null, null),
            await harness.Queue.GetRememberedDestinationsAsync(nextSession.Id));
    }

    [Fact]
    public async Task Accepting_a_matched_item_applies_the_reviewed_destination()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = await harness.Inventory.SaveAsync(new Item
        {
            Name = "Drill", Code = "6666666666666", ItemKindId = 3, LocationId = 3
        });
        var queued = await harness.Queue.EnqueueBarcodeAsync(existing.Code!);
        await harness.Queue.AcceptAsync(queued.Id, new Item
        {
            Name = existing.Name, Code = existing.Code, ItemKindId = 3, LocationId = 5
        }, existing.Id);

        var moved = await harness.Inventory.GetAsync(existing.Id);
        Assert.Equal(5, moved!.LocationId);
        Assert.Equal(2, moved.Quantity);
    }

    [Fact]
    public async Task Accepting_a_match_with_new_expiry_keeps_existing_stock_unchanged()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = new Item { Name = "Coffee", ItemKindId = 1 };
        SpecialAttributeCatalog.SetPrice(existing, 8m, "GBP");
        await harness.Inventory.SaveAsync(existing);
        var queued = await harness.Queue.EnqueueBarcodeAsync("88888888");
        var draft = new Item { Name = existing.Name, ItemKindId = 1 };
        SpecialAttributeCatalog.SetPrice(draft, 6m, "GBP");
        SpecialAttributeCatalog.SetExpiry(draft, new DateOnly(2027, 1, 2), ExpiryDateKind.BestBefore);

        var applied = await harness.Queue.AcceptAsync(queued.Id, draft, existing.Id);
        var updated = await harness.Inventory.GetAsync(existing.Id);

        Assert.Equal(8m, SpecialAttributeCatalog.GetPrice(updated!)!.DecimalValue);
        Assert.Null(updated!.ExpiryDate);
        Assert.Equal(IntakeAction.CreateStockLot, applied.Action);
        var lot = (await harness.Inventory.GetAsync(applied.ItemId))!;
        Assert.Equal(6m, SpecialAttributeCatalog.GetPrice(lot)!.DecimalValue);
        Assert.Equal(new DateOnly(2027, 1, 2), lot.ExpiryDate);
    }

    [Fact]
    public async Task Same_physical_item_photo_waits_for_review_and_attaches_without_incrementing()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Ai.Identification = new VisionIdentification
        {
            Name = "Blue Note record",
            Kind = "Media",
            Attributes = new() { ["Format"] = "LP" }
        };
        await using var frontPhoto = await PhotoAsync(20);
        var front = await harness.Queue.EnqueuePhotoAsync(
            frontPhoto, "image/png", "front.png", multipleItems: false);
        await harness.Queue.ProcessAsync(
            front.Id, new IntakeOptions { RequireReview = false }, aiEnabled: true);

        var acceptedFront = (await harness.Queue.GetAsync(front.Id))!;
        Assert.Equal(IntakeQueueStatus.Accepted, acceptedFront.Status);
        var itemId = acceptedFront.AppliedItemId!.Value;
        Assert.Equal(1, (await harness.Inventory.GetAsync(itemId))!.Quantity);

        harness.Ai.Identification = harness.Ai.Identification with
        {
            Attributes = new() { ["Format"] = "LP", ["Catalogue number"] = "BST 84123" },
            Expiry = new VisionExpiry
            {
                Date = new DateOnly(2029, 4, 30),
                Type = "best_before",
                Confidence = 0.91m
            }
        };
        harness.Ai.RelationshipPick = new CaptureRelationshipPick(
            front.Id,
            CaptureRelationship.SamePhysicalItem,
            MatchConfidence.High,
            ItemImageRole.Back,
            "Matching sleeve wear and label placement.");
        await using var backPhoto = await PhotoAsync(80);
        var back = await harness.Queue.EnqueuePhotoAsync(
            backPhoto, "image/png", "back.png", multipleItems: false);
        await harness.Queue.ProcessAsync(
            back.Id, new IntakeOptions { RequireReview = false }, aiEnabled: true);

        var proposedBack = (await harness.Queue.GetAsync(back.Id))!;
        Assert.Equal(IntakeQueueStatus.ReadyForReview, proposedBack.Status);
        Assert.Equal(IntakeAction.AttachImage, proposedBack.ProposalAction);
        Assert.Equal(front.Id, proposedBack.MatchedQueueItemId);
        Assert.Equal(itemId, proposedBack.MatchedItemId);
        Assert.Equal(0, proposedBack.IncrementBy);
        Assert.Equal(ItemImageRole.Back, proposedBack.SuggestedImageRole);
        Assert.Contains(harness.Ai.LastCaptureCandidates, candidate => candidate.QueueItemId == front.Id);
        Assert.Equal(1, (await harness.Inventory.GetAsync(itemId))!.Quantity);

        var applied = await harness.Queue.AcceptImageAttachmentAsync(
            back.Id, proposedBack.Draft, ItemImageRole.Back);

        Assert.Equal(IntakeAction.AttachImage, applied.Action);
        var updated = (await harness.Inventory.GetAsync(itemId))!;
        Assert.Equal(1, updated.Quantity);
        Assert.Equal("BST 84123", updated.Attributes["Catalogue number"]);
        Assert.Equal(new DateOnly(2029, 4, 30), updated.ExpiryDate);
        var images = await harness.Inventory.GetImagesAsync(itemId);
        Assert.Equal(2, images.Count);
        Assert.Contains(images, image => image.ImageId == proposedBack.ImageId
                                         && image.Role == ItemImageRole.Back);
    }

    [Fact]
    public async Task Uncertain_recent_same_product_cannot_auto_increment_quantity()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Ai.Identification = new VisionIdentification { Name = "Identical mug", Kind = "Other" };
        await using var firstPhoto = await PhotoAsync(10);
        var first = await harness.Queue.EnqueuePhotoAsync(
            firstPhoto, "image/png", "one.png", multipleItems: false);
        await harness.Queue.ProcessAsync(
            first.Id, new IntakeOptions { RequireReview = false }, aiEnabled: true);
        var itemId = (await harness.Queue.GetAsync(first.Id))!.AppliedItemId!.Value;

        harness.Ai.RelationshipPick = new CaptureRelationshipPick(
            first.Id,
            CaptureRelationship.Uncertain,
            MatchConfidence.Low,
            ItemImageRole.Detail,
            "No instance-specific marks are visible.");
        await using var secondPhoto = await PhotoAsync(30);
        var second = await harness.Queue.EnqueuePhotoAsync(
            secondPhoto, "image/png", "two.png", multipleItems: false);
        await harness.Queue.ProcessAsync(
            second.Id, new IntakeOptions { RequireReview = false }, aiEnabled: true);

        var review = (await harness.Queue.GetAsync(second.Id))!;
        Assert.Equal(IntakeQueueStatus.ReadyForReview, review.Status);
        Assert.Equal(IntakeAction.ChooseCandidate, review.ProposalAction);
        Assert.Equal(CaptureRelationship.Uncertain, review.CaptureRelationship);
        Assert.Equal(1, (await harness.Inventory.GetAsync(itemId))!.Quantity);
    }

    [Fact]
    public async Task Queue_review_adds_a_different_expiry_as_a_linked_stock_lot()
    {
        await using var harness = await Harness.CreateAsync();
        var existing = new Item
        {
            Name = "Baked beans",
            Code = "5000157024671",
            ItemKindId = 1,
            Quantity = 4,
            LocationId = 1
        };
        SpecialAttributeCatalog.SetExpiry(
            existing, new DateOnly(2028, 1, 31), ExpiryDateKind.BestBefore);
        existing = await harness.Inventory.SaveAsync(existing);
        harness.Ai.Identification = new VisionIdentification
        {
            Name = existing.Name,
            Barcode = existing.Code,
            Kind = "Grocery",
            Count = 2,
            Expiry = new VisionExpiry
            {
                Date = new DateOnly(2027, 9, 30),
                Type = "best_before",
                Confidence = 0.94m
            }
        };
        await using var photo = await PhotoAsync(90);
        var queued = await harness.Queue.EnqueuePhotoAsync(
            photo, "image/png", "beans.png", multipleItems: false);

        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: true);
        var review = (await harness.Queue.GetAsync(queued.Id))!;

        Assert.Equal(IntakeAction.CreateStockLot, review.ProposalAction);
        Assert.Equal(existing.Id, review.MatchedItemId);
        Assert.Equal(4, (await harness.Inventory.GetAsync(existing.Id))!.Quantity);

        var applied = await harness.Queue.AcceptAsync(review.Id, review.Draft, review.MatchedItemId);

        Assert.Equal(IntakeAction.CreateStockLot, applied.Action);
        Assert.Equal(4, (await harness.Inventory.GetAsync(existing.Id))!.Quantity);
        var lot = (await harness.Inventory.GetAsync(applied.ItemId))!;
        Assert.Equal(2, lot.Quantity);
        Assert.Equal(new DateOnly(2027, 9, 30), lot.ExpiryDate);
        Assert.Equal(existing.LocationId, lot.LocationId);
        var updatedExisting = (await harness.Inventory.GetAsync(existing.Id))!;
        Assert.NotNull(updatedExisting.CollectionKey);
        Assert.Equal(updatedExisting.CollectionKey, lot.CollectionKey);
    }

    [Fact]
    public async Task Receipt_links_shared_purchase_evidence_without_changing_item_counts()
    {
        await using var harness = await Harness.CreateAsync();
        var firstQueue = await harness.Queue.EnqueueDraftAsync(new Item
        {
            Name = "Tomato soup", ItemKindId = 1, Quantity = 3
        });
        var firstApplied = await harness.Queue.AcceptAsync(firstQueue.Id, firstQueue.Draft, null);
        var secondQueue = await harness.Queue.EnqueueDraftAsync(new Item
        {
            Name = "Baked beans", ItemKindId = 1, Quantity = 5
        });
        var secondApplied = await harness.Queue.AcceptAsync(secondQueue.Id, secondQueue.Draft, null);
        harness.Ai.Receipt = new ReceiptExtraction
        {
            Merchant = "Example Market",
            PurchaseDate = new DateOnly(2026, 8, 23),
            Total = 3.50m,
            Lines =
            [
                new ReceiptLineSuggestion
                {
                    LineIndex = 0,
                    Description = "TOM SOUP",
                    Quantity = 2,
                    LineTotal = 2,
                    MatchedQueueItemId = firstQueue.Id,
                    Confidence = MatchConfidence.High,
                    Selected = true
                },
                new ReceiptLineSuggestion
                {
                    LineIndex = 1,
                    Description = "BEANS",
                    Quantity = 1,
                    UnitPrice = 1.50m,
                    LineTotal = 1.50m,
                    MatchedQueueItemId = secondQueue.Id,
                    Confidence = MatchConfidence.None,
                    Selected = true
                }
            ]
        };

        await using var photo = await PhotoAsync(90);
        var receiptQueue = await harness.Queue.EnqueueReceiptAsync(
            photo, "image/png", "receipt.png");
        await harness.Queue.ProcessAsync(receiptQueue.Id, new IntakeOptions(), aiEnabled: true);

        var review = (await harness.Queue.GetAsync(receiptQueue.Id))!;
        Assert.Equal(IntakeSourceType.Receipt, review.SourceType);
        Assert.True(review.SourceTypeOverride);
        Assert.Equal(IntakeQueueStatus.ReadyForReview, review.Status);
        Assert.Equal("GBP", review.Receipt!.Currency);
        Assert.Equal(2, review.Receipt.Lines.Count);

        var applied = await harness.Queue.AcceptReceiptAsync(review.Id, review.Receipt);

        Assert.Equal(new ReceiptApplied(2, 2), applied);
        Assert.Equal(3, (await harness.Inventory.GetAsync(firstApplied.ItemId))!.Quantity);
        Assert.Equal(5, (await harness.Inventory.GetAsync(secondApplied.ItemId))!.Quantity);
        var firstPurchase = Assert.Single(await harness.Queue.GetPurchasesAsync(firstApplied.ItemId));
        var secondPurchase = Assert.Single(await harness.Queue.GetPurchasesAsync(secondApplied.ItemId));
        Assert.Equal(receiptQueue.ImageId, firstPurchase.ImageId);
        Assert.Equal(receiptQueue.ImageId, secondPurchase.ImageId);
        Assert.Equal("GBP", firstPurchase.Currency);
        Assert.Equal(new DateOnly(2026, 8, 23), firstPurchase.PurchasedOn);
        Assert.Equal(ItemImageRole.Receipt,
            Assert.Single(await harness.Inventory.GetImagesAsync(firstApplied.ItemId)).Role);
        Assert.Equal(ItemImageRole.Receipt,
            Assert.Single(await harness.Inventory.GetImagesAsync(secondApplied.ItemId)).Role);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Queue.AcceptReceiptAsync(review.Id, review.Receipt));
    }

    [Fact]
    public async Task Ordinary_photo_is_automatically_routed_to_purchase_evidence_review()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Ai.CaptureKind = CaptureContentKind.PurchaseEvidence;
        harness.Ai.CaptureConfidence = MatchConfidence.High;
        harness.Ai.Receipt = new ReceiptExtraction
        {
            Merchant = "Example Shop",
            Lines =
            [
                new ReceiptLineSuggestion
                {
                    LineIndex = 0,
                    Description = "ORDERED ITEM",
                    Quantity = 1
                }
            ]
        };
        await using var screenshot = await PhotoAsync(92);
        var queued = await harness.Queue.EnqueuePhotoAsync(
            screenshot, "image/png", "order-screenshot.png");

        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: true);

        var review = (await harness.Queue.GetAsync(queued.Id))!;
        Assert.Equal(IntakeSourceType.Receipt, review.SourceType);
        Assert.False(review.SourceTypeOverride);
        Assert.Equal(IntakeQueueStatus.ReadyForReview, review.Status);
        Assert.Equal("Example Shop", review.Receipt!.Merchant);
        Assert.Null(review.ProposalAction);
    }

    [Fact]
    public async Task Manual_item_photo_correction_is_not_undone_by_ai_classification()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Ai.CaptureKind = CaptureContentKind.PurchaseEvidence;
        harness.Ai.CaptureConfidence = MatchConfidence.High;
        harness.Ai.Receipt = new ReceiptExtraction
        {
            Lines = [new ReceiptLineSuggestion { LineIndex = 0, Description = "FALSE POSITIVE" }]
        };
        await using var photo = await PhotoAsync(93);
        var queued = await harness.Queue.EnqueuePhotoAsync(photo, "image/png", "item.png");
        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: true);
        Assert.Equal(IntakeSourceType.Receipt, (await harness.Queue.GetAsync(queued.Id))!.SourceType);

        await harness.Queue.ReclassifyImageAsync(queued.Id, IntakeSourceType.Photo);
        harness.Ai.Identification = new VisionIdentification { Name = "Actual item", Kind = "Other" };
        await harness.Queue.ProcessAsync(queued.Id, new IntakeOptions(), aiEnabled: true);

        var review = (await harness.Queue.GetAsync(queued.Id))!;
        Assert.Equal(IntakeSourceType.Photo, review.SourceType);
        Assert.True(review.SourceTypeOverride);
        Assert.Equal(IntakeQueueStatus.ReadyForReview, review.Status);
        Assert.Equal("Actual item", review.Draft.Name);
        Assert.Null(review.Receipt);
    }

    [Fact]
    public async Task Receipt_match_requires_the_earlier_capture_to_be_accepted_first()
    {
        await using var harness = await Harness.CreateAsync();
        var itemQueue = await harness.Queue.EnqueueDraftAsync(new Item
        {
            Name = "Unreviewed milk", ItemKindId = 1
        });
        await using var photo = await PhotoAsync(91);
        var receiptQueue = await harness.Queue.EnqueueReceiptAsync(photo, "image/png", "receipt.png");
        var receipt = new ReceiptExtraction
        {
            Lines =
            [
                new ReceiptLineSuggestion
                {
                    LineIndex = 0,
                    Description = "MILK",
                    MatchedQueueItemId = itemQueue.Id,
                    Selected = true
                }
            ]
        };
        harness.Ai.Receipt = receipt;
        await harness.Queue.ProcessAsync(
            receiptQueue.Id, new IntakeOptions(), aiEnabled: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Queue.AcceptReceiptAsync(receiptQueue.Id, receipt));

        Assert.Contains("Choose an inventory item", error.Message);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required TestDb Db { get; init; }
        public required string ImageRoot { get; init; }
        public required FakeAi Ai { get; init; }
        public required FakeLookup Lookup { get; init; }
        public required InventoryService Inventory { get; init; }
        public required IntakeQueueService Queue { get; init; }

        public static async Task<Harness> CreateAsync()
        {
            var db = await TestDb.CreateAsync();
            var imageRoot = Path.Combine(Path.GetTempPath(), $"stash_queue_{Guid.NewGuid():N}");
            var images = new ImageService(db.Factory, new ImageOptions { RootPath = imageRoot },
                new StubHostEnvironment(), new StubHttpFactory(), NullLogger<ImageService>.Instance);
            var ai = new FakeAi();
            var lookup = new FakeLookup();
            var inventory = new InventoryService(db.Factory);
            var photoIntake = new PhotoIntakeService(
                ai, inventory, images, NullLogger<PhotoIntakeService>.Instance);
            var queue = new IntakeQueueService(
                db.Factory, images, lookup, photoIntake, inventory, new IntakeQueueSignal(),
                NullLogger<IntakeQueueService>.Instance);
            return new Harness
            {
                Db = db,
                ImageRoot = imageRoot,
                Ai = ai,
                Lookup = lookup,
                Inventory = inventory,
                Queue = queue
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            try { if (Directory.Exists(ImageRoot)) Directory.Delete(ImageRoot, true); } catch { }
        }
    }

    private sealed class FakeLookup : ILookupRouter
    {
        public LookupResult Result { get; set; } = LookupResult.NotFound(string.Empty, "test");
        public Task<LookupResult> LookupAsync(string code, CancellationToken ct = default) => Task.FromResult(Result);
    }

    private sealed class FakeAi : IAiEnrichmentService
    {
        public bool IsEnabled => true;
        public VisionIdentification? Identification { get; set; }
        public List<DetectedBox> Boxes { get; set; } = new();
        public CaptureContentKind CaptureKind { get; set; } = CaptureContentKind.InventoryItems;
        public MatchConfidence CaptureConfidence { get; set; } = MatchConfidence.High;
        public CaptureRelationshipPick? RelationshipPick { get; set; }
        public ReceiptExtraction? Receipt { get; set; }
        public IReadOnlyList<CaptureMatchCandidate> LastCaptureCandidates { get; private set; }
            = Array.Empty<CaptureMatchCandidate>();
        public IReadOnlyList<ReceiptMatchCandidate> LastReceiptCandidates { get; private set; }
            = Array.Empty<ReceiptMatchCandidate>();
        public string? LastIntakeContext { get; private set; }

        public Task<VisionIdentification?> IdentifyItemAsync(
            byte[] image, string mediaType, IReadOnlyList<string> knownKinds,
            CancellationToken ct = default, string? intakeContext = null,
            AiRegionalContext? regionalContext = null)
        {
            LastIntakeContext = intakeContext;
            return Task.FromResult(Identification);
        }

        public Task<CaptureAnalysis> AnalyzeCaptureAsync(
            byte[] image, string mediaType, CancellationToken ct = default) =>
            Task.FromResult(new CaptureAnalysis(CaptureKind, CaptureConfidence, Boxes));

        public Task<MatchPick?> PickMatchAsync(
            byte[] image, string mediaType, VisionIdentification identification,
            IReadOnlyList<MatchCandidate> candidates, CancellationToken ct = default) =>
            Task.FromResult<MatchPick?>(null);

        public Task<CaptureRelationshipPick?> ClassifyCaptureRelationshipAsync(
            byte[] image, string mediaType, VisionIdentification identification,
            IReadOnlyList<CaptureMatchCandidate> recentCaptures, CancellationToken ct = default)
        {
            LastCaptureCandidates = recentCaptures;
            return Task.FromResult(RelationshipPick);
        }

        public Task<ReceiptExtraction?> ExtractReceiptAsync(
            byte[] image, string mediaType, IReadOnlyList<ReceiptMatchCandidate> candidates,
            AiRegionalContext regionalContext, CancellationToken ct = default)
        {
            LastReceiptCandidates = candidates;
            return Task.FromResult(Receipt);
        }

        public Task<AiSuggestion?> EnrichAsync(
            string name, string? kind, IReadOnlyDictionary<string, string> known,
            CancellationToken ct = default) => Task.FromResult<AiSuggestion?>(null);

        public Task<AiBomSuggestion?> SuggestBomAsync(
            string request, BomKind kind, IReadOnlyList<AiBomInventoryItem> inventory,
            IReadOnlyList<string> canonicalAttributeNames, CancellationToken ct = default) =>
            Task.FromResult<AiBomSuggestion?>(null);

        public Task<AiMealPlanSuggestion?> SuggestMealPlanAsync(
            string? request, DateOnly startDate, int days,
            IReadOnlyList<AiMealPlanRecipe> recipes,
            IReadOnlyList<AiMealPlanInventoryItem> inventory,
            AiRegionalContext regionalContext, CancellationToken ct = default) =>
            Task.FromResult<AiMealPlanSuggestion?>(null);

        public Task<string?> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static async Task<MemoryStream> PhotoAsync(byte seed = 20)
    {
        using var image = new Image<Rgba32>(64, 64, new Rgba32(seed, 80, 120));
        var stream = new MemoryStream();
        await image.SaveAsPngAsync(stream);
        stream.Position = 0;
        return stream;
    }
}
