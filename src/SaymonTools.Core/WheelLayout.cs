namespace SaymonTools.Core;

/// <summary>Applies the user's wheel customisation: hidden slices and favourites placed first.</summary>
public static class WheelLayout
{
    public static IReadOnlyList<Operation> Arrange(IEnumerable<Operation> operations, ICollection<string> hidden, IReadOnlyList<string> favorites) =>
        operations
            .Where(o => !hidden.Contains(o.Id))
            .Select((o, i) => (Op: o, Index: i, Favorite: IndexOf(favorites, o.Id)))
            .OrderBy(x => x.Favorite < 0 ? int.MaxValue : x.Favorite)
            .ThenBy(x => x.Index)
            .Select(x => x.Op)
            .ToList();

    /// <summary>
    /// Splits <paramref name="count"/> slices into pages of at most <paramref name="maxSlices"/>: the first page
    /// keeps one slot for "More", middle pages two ("Back" and "More"), the last page one ("Back").
    /// </summary>
    /// <returns>The range of operations on each page.</returns>
    public static IReadOnlyList<(int Start, int Count)> Pages(int count, int maxSlices)
    {
        if (count <= maxSlices)
            return [(0, count)];
        var pages = new List<(int Start, int Count)> { (0, maxSlices - 1) };
        var next = maxSlices - 1;
        while (count - next > maxSlices - 1)
        {
            pages.Add((next, maxSlices - 2));
            next += maxSlices - 2;
        }
        pages.Add((next, count - next));
        return pages;
    }

    private static int IndexOf(IReadOnlyList<string> list, string id)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == id)
                return i;
        }
        return -1;
    }
}
