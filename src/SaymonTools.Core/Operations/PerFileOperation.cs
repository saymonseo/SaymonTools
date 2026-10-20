namespace SaymonTools.Core.Operations;

/// <summary>Processes each dropped file independently.</summary>
public sealed class PerFileOperation : Operation
{
    /// <summary>
    /// How many files CPU-bound operations (images, single-threaded audio encoders) process at once.
    /// Leaves a core free for the UI and the rest of the system.
    /// </summary>
    public static int CpuParallelism { get; } = Math.Clamp(Environment.ProcessorCount - 1, 1, 6);

    public delegate Task<IEnumerable<string>> FileHandler(FileItem item, OperationContext ctx, IProgress<double> progress);

    private readonly HashSet<string> _inputs;
    private readonly string? _targetFormat;
    private readonly FileHandler _handler;

    /// <param name="targetFormat">When set, the option is hidden for files that already have this format.</param>
    public PerFileOperation(
        string id,
        WheelKind wheel,
        string label,
        string glyph,
        IEnumerable<string> inputs,
        FileHandler handler,
        string? targetFormat = null,
        string? subtitle = null)
        : base(id, wheel, label, glyph, subtitle)
    {
        _inputs = new HashSet<string>(inputs, StringComparer.OrdinalIgnoreCase);
        _targetFormat = targetFormat;
        _handler = handler;
    }

    /// <summary>Also accept folders (e.g. "pack to ZIP").</summary>
    public bool AcceptsDirectories { get; init; }

    /// <summary>Accept any file regardless of its format.</summary>
    public bool AcceptsAnyFile { get; init; }

    /// <summary>Per-file tool check for operations whose backend depends on the input format.</summary>
    public Func<ToolLocator, FileItem, bool>? AvailableFor { get; init; }

    /// <summary>
    /// Files processed at the same time. 1 for tools that are multi-threaded themselves (x264, LibreOffice)
    /// or can't run twice; <see cref="CpuParallelism"/> for independent CPU-bound work.
    /// </summary>
    public int MaxParallelism { get; init; } = 1;

    public override bool IsAvailable(ToolLocator tools, IReadOnlyList<FileItem> items) =>
        base.IsAvailable(tools, items) && (AvailableFor is null || items.All(i => AvailableFor(tools, i)));

    private bool Accepts(FileItem item) =>
        item.IsDirectory ? AcceptsDirectories : AcceptsAnyFile || _inputs.Contains(item.Format);

    private bool NeedsWork(FileItem item) =>
        item.IsDirectory || _targetFormat is null || !string.Equals(item.Format, _targetFormat, StringComparison.OrdinalIgnoreCase);

    public override bool AppliesTo(IReadOnlyList<FileItem> items) =>
        items.Count > 0 && items.All(Accepts) && items.Any(NeedsWork);

    public override IReadOnlyList<FileItem> Applicable(IReadOnlyList<FileItem> items)
    {
        var fit = items.Where(Accepts).ToList();
        return fit.Any(NeedsWork) ? fit : [];
    }

    public override async Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx)
    {
        var work = items.Where(NeedsWork).ToList();
        var outputs = new IEnumerable<string>?[work.Count];
        var errors = new string?[work.Count];
        var progress = new BatchProgress(ctx.Progress, work.Count);
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, MaxParallelism),
            CancellationToken = ctx.CancellationToken,
        };
        await Parallel.ForEachAsync(Enumerable.Range(0, work.Count), options, async (i, _) =>
        {
            var itemProgress = progress.For(i);
            try
            {
                outputs[i] = await _handler(work[i], ctx, itemProgress).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && work.Count > 1)
            {
                // Keep going with the rest of a batch; report failures at the end.
                errors[i] = $"{work[i].Name}: {ex.Message}";
            }
            itemProgress.Report(1);
        }).ConfigureAwait(false);

        // Results keep the order of the selection, whichever file finished first.
        var produced = outputs.Where(o => o is not null).SelectMany(o => o!).ToList();
        var failed = errors.OfType<string>().ToList();
        if (failed.Count > 0 && produced.Count == 0)
            throw new ConversionException(string.Join(Environment.NewLine, failed));
        return new OperationResult(produced, failed.Count > 0 ? string.Join(Environment.NewLine, failed) : null);
    }

    /// <summary>Overall progress of files that run side by side: the average of their own progress.</summary>
    private sealed class BatchProgress(IProgress<double> parent, int count)
    {
        private readonly double[] _parts = new double[Math.Max(1, count)];
        private readonly Lock _gate = new();

        public IProgress<double> For(int index) => new Reporter(this, index);

        private void Report(int index, double value)
        {
            double total;
            lock (_gate)
            {
                _parts[index] = Math.Max(_parts[index], Math.Clamp(value, 0, 1));
                total = _parts.Sum() / _parts.Length;
            }
            parent.Report(total);
        }

        private sealed class Reporter(BatchProgress owner, int index) : IProgress<double>
        {
            public void Report(double value) => owner.Report(index, value);
        }
    }
}

/// <summary>Processes all dropped files together (merge, pack, …).</summary>
public sealed class BatchOperation : Operation
{
    public delegate Task<IEnumerable<string>> BatchHandler(IReadOnlyList<FileItem> items, OperationContext ctx);

    private readonly Func<IReadOnlyList<FileItem>, bool> _applies;
    private readonly BatchHandler _handler;

    public BatchOperation(
        string id,
        WheelKind wheel,
        string label,
        string glyph,
        Func<IReadOnlyList<FileItem>, bool> applies,
        BatchHandler handler,
        string? subtitle = null)
        : base(id, wheel, label, glyph, subtitle)
    {
        _applies = applies;
        _handler = handler;
    }

    /// <summary>Which single files the operation can take; lets it work on part of a mixed selection.</summary>
    public Func<FileItem, bool>? Accepts { get; init; }

    public override bool AppliesTo(IReadOnlyList<FileItem> items) => items.Count > 0 && _applies(items);

    public override IReadOnlyList<FileItem> Applicable(IReadOnlyList<FileItem> items)
    {
        if (AppliesTo(items))
            return items;
        if (Accepts is null)
            return [];
        var fit = items.Where(Accepts).ToList();
        return AppliesTo(fit) ? fit : [];
    }

    public override async Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx) =>
        OperationResult.Of(await _handler(items, ctx).ConfigureAwait(false));

    /// <summary>True when there are at least <paramref name="min"/> files and all match <paramref name="formats"/>.</summary>
    public static Func<IReadOnlyList<FileItem>, bool> AllOf(ISet<string> formats, int min = 1) =>
        items => items.Count >= min && items.All(FileOf(formats));

    /// <summary>A file (not a folder) in one of <paramref name="formats"/>.</summary>
    public static Func<FileItem, bool> FileOf(ISet<string> formats) =>
        item => !item.IsDirectory && formats.Contains(item.Format);
}

/// <summary>
/// An operation offered for part of a mixed selection, e.g. image conversions when 7 of 10 files are
/// images. Runs on that part only; the subtitle says how much of the selection it covers.
/// </summary>
public sealed class PartialOperation : Operation
{
    /// <param name="part">The files of the selection it fits (<see cref="Operation.Applicable"/>).</param>
    public PartialOperation(Operation inner, IReadOnlyList<FileItem> part, int total)
        : base(inner.Id, inner.Wheel, inner.Label, inner.Glyph, Describe(inner.Subtitle, part, total))
    {
        Inner = inner;
        Requires = inner.Requires;
        Parameters = inner.Parameters;
    }

    public Operation Inner { get; }

    /// <summary>
    /// "PDF · 1 of 4" when the part is one kind of file, so "JPG from images" and "JPG from a PDF" can be
    /// told apart; otherwise the operation's own subtitle and the count.
    /// </summary>
    private static string Describe(string? subtitle, IReadOnlyList<FileItem> part, int total)
    {
        var formats = part.Select(i => i.IsDirectory ? "" : i.Format).Distinct().ToList();
        var source = formats is [{ Length: > 0 } format] ? Formats.Display(format) : subtitle;
        var count = Loc.T($"{part.Count} of {total}", $"{part.Count} из {total}");
        return string.IsNullOrEmpty(source) ? count : $"{source} · {count}";
    }

    public override bool AppliesTo(IReadOnlyList<FileItem> items) => Inner.Applicable(items).Count > 0;

    public override IReadOnlyList<FileItem> Applicable(IReadOnlyList<FileItem> items) => Inner.Applicable(items);

    public override bool IsAvailable(ToolLocator tools, IReadOnlyList<FileItem> items) => Inner.IsAvailable(tools, Inner.Applicable(items));

    public override Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx) =>
        Inner.ExecuteAsync(Inner.Applicable(items), ctx);
}
