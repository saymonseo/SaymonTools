using System.Runtime.CompilerServices;
using SaymonTools.Core;

namespace SaymonTools.Tests;

internal static class TestSetup
{
    /// <summary>Tests assert Czech messages and file names, whatever the machine's language.</summary>
    [ModuleInitializer]
    internal static void Init() => Loc.Current = Language.English;
}

[CollectionDefinition(nameof(LanguageSwitching), DisableParallelization = true)]
public class LanguageSwitching;

[Collection(nameof(LanguageSwitching))]
public class LocalizationTests
{
    [Fact]
    public void Russian_labels_and_plurals()
    {
        try
        {
            Loc.Current = Language.Russian;
            var ops = SaymonTools.Core.Operations.Catalog.Build();
            Assert.Equal("Сжать", ops.Single(o => o.Id == "image.compress").Label);
            Assert.Equal("Обрезать", ops.Single(o => o.Id == "video.trim").Label);
            Assert.Equal("Все страницы", Loc.T("All pages", "Все страницы"));
            foreach (var (n, expected) in new[] { (0, "0 файлов"), (1, "1 файл"), (2, "2 файла"), (5, "5 файлов"), (11, "11 файлов"), (21, "21 файл"), (24, "24 файла"), (112, "112 файлов") })
                Assert.Equal(expected, Loc.Items(n));
        }
        finally { Loc.Current = Language.English; }
    }

    [Fact]
    public void English_labels_and_output_names()
    {
        try
        {
            Loc.Current = Language.English;
            var ops = SaymonTools.Core.Operations.Catalog.Build();
            Assert.Equal("Compress", ops.Single(o => o.Id == "image.compress").Label);
            Assert.Equal("Trim", ops.Single(o => o.Id == "video.trim").Label);
            Assert.Equal("3 items", Loc.Items(3));
            Assert.DoesNotContain(ops, o => o.Label.Any(c => "ěščřžýáíéůúťďňĚŠČŘŽÝÁÍÉŮÚŤĎŇ".Contains(c)));
            Assert.DoesNotContain(ops, o => (o.Subtitle ?? "").Any(c => "ěščřžýáíéůúťďňĚŠČŘŽÝÁÍÉŮÚŤĎŇ".Contains(c)));
        }
        finally
        {
            Loc.Current = Language.English;
        }
    }

    [Fact]
    public void English_plurals()
    {
        Assert.Equal("1 item", Loc.Items(1));
        Assert.Equal("3 items", Loc.Items(3));
        Assert.Equal("5 items", Loc.Items(5));
    }
}
