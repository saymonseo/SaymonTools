using SaymonTools.Core;

namespace SaymonTools.App.Actions;

/// <summary>A file action that runs in the app (UI thread) instead of producing converted files.</summary>
internal sealed class AppAction : Operation
{
    private readonly Func<IReadOnlyList<FileItem>, Task> _run;
    private readonly Func<IReadOnlyList<FileItem>, bool> _applies;

    public AppAction(string id, string label, string glyph, Func<IReadOnlyList<FileItem>, Task> run, string? subtitle = null,
        Func<IReadOnlyList<FileItem>, bool>? applies = null)
        : base(id, WheelKind.Actions, label, glyph, subtitle)
    {
        _run = run;
        _applies = applies ?? (items => items.Count > 0);
    }

    public override bool AppliesTo(IReadOnlyList<FileItem> items) => _applies(items);

    public Task RunAsync(IReadOnlyList<FileItem> items) => _run(items);

    public override Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx) =>
        throw new NotSupportedException("App actions run through RunAsync.");
}
