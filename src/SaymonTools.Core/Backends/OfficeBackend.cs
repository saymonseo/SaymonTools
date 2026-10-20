using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Backends;

/// <summary>Office formats (DOC/DOCX/PPTX/XLSX/ODF → PDF and between each other) through headless LibreOffice.</summary>
public static class OfficeBackend
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>LibreOffice filter name for --convert-to, so e.g. "pdf" picks the right exporter.</summary>
    private static string Target(string format, string inputFormat) => format switch
    {
        "pdf" => "pdf",
        "docx" => "docx:\"MS Word 2007 XML\"",
        "odt" => "odt",
        "pptx" => "pptx:\"Impress MS PowerPoint 2007 XML\"",
        "odp" => "odp",
        "xlsx" => "xlsx:\"Calc MS Excel 2007 XML\"",
        "ods" => "ods",
        "csv" => "csv",
        "html" => "html",
        "rtf" => "rtf",
        "txt" => "txt:Text",
        _ => format,
    };

    public static async Task<string> ConvertToTempAsync(OperationContext ctx, string input, string format, string? inputFilter = null)
    {
        var soffice = ctx.Tools.Require(ExternalTool.LibreOffice);
        var outDir = OutputPaths.TempDirectory();
        var profile = Path.Combine(Path.GetTempPath(), "SaymonTools", "lo-profile");
        var args = new List<string>
        {
            "-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri,
            "--headless",
            "--norestore",
            "--nolockcheck",
            "--nodefault",
        };
        if (inputFilter is not null)
            args.Add("--infilter=" + inputFilter);
        args.AddRange(["--convert-to", Target(format, Formats.KeyOf(input)).Replace("\"", ""), "--outdir", outDir, input]);

        // One LibreOffice instance per profile at a time.
        await Gate.WaitAsync(ctx.CancellationToken).ConfigureAwait(false);
        try
        {
            await ProcessRunner.RunAsync(soffice, args, ctx.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }

        var produced = Directory.GetFiles(outDir).FirstOrDefault();
        if (produced is null)
        {
            OutputPaths.TryDelete(outDir);
            throw new ConversionException(T("LibreOffice could not convert the file (is it intact and not password-protected?).", "LibreOffice не удалось конвертировать файл. Возможно, он повреждён или защищён паролем."));
        }
        return produced;
    }

    public static async Task<string> ConvertAsync(FileItem item, string format, OperationContext ctx, string? inputFilter = null)
    {
        var temp = await ConvertToTempAsync(ctx, item.Path, format, inputFilter).ConfigureAwait(false);
        try
        {
            var dir = ctx.OutputDirFor(item.Path);
            var final = OutputPaths.Unique(dir, item.BaseName, format);
            File.Move(temp, final);
            return final;
        }
        finally
        {
            OutputPaths.TryDelete(Path.GetDirectoryName(temp)!);
        }
    }
}
