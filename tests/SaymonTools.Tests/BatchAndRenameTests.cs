using SaymonTools.Core;
using SaymonTools.Core.Operations;
using SaymonTools.Core.Rename;

namespace SaymonTools.Tests;

public class ParallelBatchTests
{
    private sealed class SyncProgress : IProgress<double>
    {
        public double Last { get; private set; }

        public void Report(double value) => Last = value;
    }

    private static OperationContext Context(IProgress<double>? progress = null) =>
        new() { Tools = new ToolLocator(), Progress = progress ?? new SyncProgress() };

    [Fact]
    public async Task Files_run_side_by_side_and_results_keep_selection_order()
    {
        var running = 0;
        var peak = 0;
        var op = new PerFileOperation("t", WheelKind.Tools, "T", "", ["txt"], async (item, _, _) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            // Later files finish first, so the result order can't come from completion order.
            await Task.Delay(item.Name == "a.txt" ? 120 : 30);
            Interlocked.Decrement(ref running);
            return [item.Path + ".out"];
        })
        { MaxParallelism = 4 };
        var progress = new SyncProgress();
        var items = new[] { "a.txt", "b.txt", "c.txt", "d.txt" }.Select(n => new FileItem(n)).ToList();

        var result = await op.ExecuteAsync(items, Context(progress));

        Assert.Equal(["a.txt.out", "b.txt.out", "c.txt.out", "d.txt.out"], result.Outputs);
        Assert.True(peak > 1, $"expected parallel work, peak was {peak}");
        Assert.Equal(1, progress.Last, 3);
    }

    [Fact]
    public async Task One_failing_file_does_not_stop_the_batch()
    {
        var op = new PerFileOperation("t", WheelKind.Tools, "T", "", ["txt"], (item, _, _) =>
            item.Name == "bad.txt" ? throw new ConversionException("broken") : Task.FromResult<IEnumerable<string>>([item.Name]))
        { MaxParallelism = 3 };
        var result = await op.ExecuteAsync([new("ok1.txt"), new("bad.txt"), new("ok2.txt")], Context());
        Assert.Equal(["ok1.txt", "ok2.txt"], result.Outputs);
        Assert.Contains("bad.txt: broken", result.Message);
    }

    [Fact]
    public async Task A_single_file_failure_is_reported_as_is()
    {
        var op = new PerFileOperation("t", WheelKind.Tools, "T", "", ["txt"], (_, _, _) => throw new ConversionException("broken"))
        { MaxParallelism = 3 };
        var ex = await Assert.ThrowsAsync<ConversionException>(() => op.ExecuteAsync([new("x.txt")], Context()));
        Assert.Equal("broken", ex.Message);
    }

    [Fact]
    public async Task Parallel_outputs_with_the_same_name_get_distinct_files()
    {
        var dir = OutputPaths.TempDirectory();
        try
        {
            var outputs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                OutputPaths.ProduceAsync(dir, "photo", "png", tmp => File.WriteAllTextAsync(tmp, "x")))));
            Assert.Equal(8, outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(8, Directory.GetFiles(dir, "photo*.png").Length);
        }
        finally
        {
            OutputPaths.TryDelete(dir);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}

public class MixedSelectionTests
{
    private static readonly IReadOnlyList<FileItem> Mixed = [new("a.jpg"), new("b.png"), new("c.pdf")];

    [Fact]
    public void Per_file_operation_takes_the_part_it_fits()
    {
        var toWebp = Catalog.Build().Single(o => o.Id == "image.to.webp");
        Assert.False(toWebp.AppliesTo(Mixed));
        Assert.Equal(["a.jpg", "b.png"], toWebp.Applicable(Mixed).Select(i => i.Path));
    }

    [Fact]
    public void Target_format_files_alone_are_not_offered()
    {
        var toPng = Catalog.Build().Single(o => o.Id == "image.to.png");
        Assert.Empty(toPng.Applicable([new("b.png"), new("c.pdf")]));
    }

    [Fact]
    public void Merge_needs_two_fitting_files_in_a_mixed_selection()
    {
        var merge = Catalog.Build().Single(o => o.Id == "pdf.merge");
        Assert.Empty(merge.Applicable(Mixed));
        Assert.Equal(["c.pdf", "d.pdf"], merge.Applicable([.. Mixed, new("d.pdf")]).Select(i => i.Path));
    }

    [Fact]
    public async Task Partial_operation_says_how_much_it_covers_and_runs_on_that_part()
    {
        var seen = new List<string>();
        var inner = new PerFileOperation("t", WheelKind.Tools, "T", "", ["jpg", "png"], (item, _, _) =>
        {
            lock (seen)
                seen.Add(item.Path);
            return Task.FromResult<IEnumerable<string>>([]);
        }, subtitle: "sub");
        var partial = new PartialOperation(inner, inner.Applicable(Mixed), Mixed.Count);

        Assert.Equal("sub · 2 of 3", partial.Subtitle);
        // One kind of file: the subtitle names it, so "JPG from images" and "JPG from a PDF" differ.
        Assert.Equal("PDF · 1 of 3", new PartialOperation(inner, [new FileItem("c.pdf")], 3).Subtitle);
        Assert.Equal(inner.Id, partial.Id);
        await partial.ExecuteAsync(Mixed, new OperationContext { Tools = new ToolLocator() });
        Assert.Equal(["a.jpg", "b.png"], seen.Order());
    }
}

public class WheelPageTests
{
    [Fact]
    public void Small_wheels_have_one_page() => Assert.Equal([(0, 10)], WheelLayout.Pages(10, 10));

    [Fact]
    public void Large_wheels_leave_room_for_more_and_back()
    {
        Assert.Equal([(0, 9), (9, 2)], WheelLayout.Pages(11, 10));
        Assert.Equal([(0, 9), (9, 8), (17, 8)], WheelLayout.Pages(25, 10));
    }
}

public sealed class RenameTests : IDisposable
{
    private readonly string _dir = OutputPaths.TempDirectory();

    public void Dispose() => OutputPaths.TryDelete(_dir);

    private string Make(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, name);
        return path;
    }

    private static string[] Names(IEnumerable<RenameEntry> plan) => [.. plan.Select(e => e.NewName)];

    [Fact]
    public void Template_numbers_in_natural_name_order_and_keeps_extensions()
    {
        var files = new[] { Make("img10.JPG"), Make("img2.jpg"), Make("backup.tar.gz") };
        var plan = BatchRename.Plan(files, new RenameOptions { Template = "trip_{n:000}" });
        Assert.Equal(["trip_001.tar.gz", "trip_002.jpg", "trip_003.JPG"], Names(plan));
        Assert.All(plan, e => Assert.Null(e.Error));
    }

    [Fact]
    public void Start_and_step_and_selection_order()
    {
        var files = new[] { Make("b.txt"), Make("a.txt") };
        var plan = BatchRename.Plan(files, new RenameOptions { Template = "{name}-{n}", Start = 10, Step = 5, Order = RenameOrder.Selection });
        Assert.Equal(["b-10.txt", "a-15.txt"], Names(plan));
    }

    [Fact]
    public void Find_replace_plain_and_regex()
    {
        var file = Make("IMG_2024_0001.jpg");
        Assert.Equal(["photo_2024_0001.jpg"], Names(BatchRename.Plan([file], new RenameOptions { Find = "img", Replace = "photo" })));
        Assert.Equal(["IMG_2024_0001.jpg"], Names(BatchRename.Plan([file], new RenameOptions { Find = "img", Replace = "photo", MatchCase = true })));
        Assert.Equal(["0001-2024.jpg"], Names(BatchRename.Plan([file], new RenameOptions { Find = @"^IMG_(\d+)_(\d+)$", Replace = "$2-$1", UseRegex = true })));
    }

    [Fact]
    public void Invalid_regex_is_reported()
    {
        var plan = BatchRename.Plan([Make("a.txt")], new RenameOptions { Find = "(", UseRegex = true });
        Assert.StartsWith("Invalid regular expression", plan[0].Error);
    }

    [Fact]
    public void Case_transliteration_and_spaces()
    {
        var file = Make("Отпуск в Сочи.jpg");
        Assert.Equal(["Otpusk_v_Sochi.jpg"], Names(BatchRename.Plan([file], new RenameOptions { Transliterate = true, SpacesToUnderscores = true })));
        Assert.Equal(["отпуск в сочи.jpg"], Names(BatchRename.Plan([file], new RenameOptions { Case = RenameCase.Lower })));
        Assert.Equal("Shchuka Yozh", BatchRename.ToLatin("Щука Ёж"));
    }

    [Fact]
    public void Invalid_characters_become_underscores_and_empty_names_are_errors()
    {
        var file = Make("a.txt");
        Assert.Equal(["x_y.txt"], Names(BatchRename.Plan([file], new RenameOptions { Template = "x:y" })));
        Assert.NotNull(BatchRename.Plan([file], new RenameOptions { Template = "" })[0].Error);
        Assert.NotNull(BatchRename.Plan([file], new RenameOptions { Template = "CON" })[0].Error);
    }

    [Fact]
    public void Two_files_with_the_same_new_name_are_conflicts()
    {
        var plan = BatchRename.Plan([Make("a.txt"), Make("b.txt")], new RenameOptions { Template = "same" });
        Assert.All(plan, e => Assert.Equal("Another file gets the same name.", e.Error));
    }

    [Fact]
    public void Names_of_files_outside_the_batch_are_conflicts()
    {
        Make("taken.txt");
        var plan = BatchRename.Plan([Make("a.txt")], new RenameOptions { Template = "taken" });
        Assert.Equal("A file with this name already exists.", plan[0].Error);
    }

    [Fact]
    public void Swapping_names_works_and_can_be_undone()
    {
        var a = Make("a.txt");
        var b = Make("b.txt");
        var journal = BatchRename.Apply([new RenameEntry(a, b, null), new RenameEntry(b, a, null)]);
        Assert.Equal("b.txt", File.ReadAllText(Path.Combine(_dir, "a.txt")));
        Assert.Equal("a.txt", File.ReadAllText(Path.Combine(_dir, "b.txt")));

        BatchRename.Undo(RenameJournal.FromJson(journal.ToJson())!);
        Assert.Equal("a.txt", File.ReadAllText(Path.Combine(_dir, "a.txt")));
        Assert.Equal("b.txt", File.ReadAllText(Path.Combine(_dir, "b.txt")));
    }

    [Fact]
    public void Apply_renames_and_refuses_plans_with_errors()
    {
        var files = new[] { Make("one.txt"), Make("two.txt") };
        var journal = BatchRename.Apply(BatchRename.Plan(files, new RenameOptions { Template = "doc_{n}" }));
        Assert.Equal(2, journal.Moves.Count);
        Assert.True(File.Exists(Path.Combine(_dir, "doc_1.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "doc_2.txt")));

        var bad = BatchRename.Plan([Path.Combine(_dir, "doc_1.txt"), Path.Combine(_dir, "doc_2.txt")], new RenameOptions { Template = "same" });
        Assert.Throws<ConversionException>(() => BatchRename.Apply(bad));
        Assert.True(File.Exists(Path.Combine(_dir, "doc_1.txt")));
    }

    [Fact]
    public void Case_only_rename_works()
    {
        var file = Make("photo.txt");
        BatchRename.Apply(BatchRename.Plan([file], new RenameOptions { Case = RenameCase.Upper }));
        Assert.Equal(["PHOTO.txt"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Folders_are_renamed_whole()
    {
        var folder = Path.Combine(_dir, "My Folder.v2");
        Directory.CreateDirectory(folder);
        var plan = BatchRename.Plan([folder], new RenameOptions { SpacesToUnderscores = true });
        Assert.Equal(["My_Folder.v2"], Names(plan));
        BatchRename.Apply(plan);
        Assert.True(Directory.Exists(Path.Combine(_dir, "My_Folder.v2")));
    }
}
