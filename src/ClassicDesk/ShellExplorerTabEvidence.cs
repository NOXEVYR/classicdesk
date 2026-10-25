namespace ClassicDesk;

public static class ShellExplorerTabEvidence
{
    // ItemContainer enumerates logical items, including offscreen placeholders.
    // Bound this to two reads: a second item (even the same/cyclic one) rejects
    // the single-tab claim without ever realizing or selecting it.
    public static bool HasOnlyLogicalItem<T>(T expected, Func<T?, T?> next, Func<T, T, bool> same) where T : class
    {
        var first = next(null);
        return first is not null && same(first, expected) && next(first) is null;
    }

    // Tab identity and navigation health are separate facts. A broken classic
    // navigation row must not prevent a verified single-folder window from
    // being offered for replacement. Modern/mixed/unknown topology stays blocked.
    public static bool IsSingleClassicFolder(bool complete, int folderHosts,
        bool visibleRibbon, bool modernHeader, bool tabScaffold) =>
        complete && folderHosts == 1 && visibleRibbon && !modernHeader && !tabScaffold;
}
