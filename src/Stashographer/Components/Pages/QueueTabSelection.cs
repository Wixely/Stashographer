namespace Stashographer.Components.Pages;

internal static class QueueTabSelection
{
    public static int InitialIndex(string? requestedTab, int intakeOpen, int modifyOpen) =>
        string.Equals(requestedTab, "modify", StringComparison.OrdinalIgnoreCase)
        || (!string.Equals(requestedTab, "intake", StringComparison.OrdinalIgnoreCase)
            && intakeOpen == 0 && modifyOpen > 0)
            ? 1
            : 0;
}
