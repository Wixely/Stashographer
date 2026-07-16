using Stashographer.Data.Entities;

namespace Stashographer.Services.Inventory;

/// <summary>
/// Scoped (per-circuit) holder for an item being created, so the Scan page can hand a
/// pre-filled draft to the item editor across a navigation without query-string plumbing.
/// </summary>
public class ItemDraftState
{
    public Item? Pending { get; private set; }

    public void Set(Item draft) => Pending = draft;

    public Item? Take()
    {
        var draft = Pending;
        Pending = null;
        return draft;
    }
}
