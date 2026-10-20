using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Backends;

/// <summary>Document conversions through pandoc.</summary>
public static class PandocBackend
{
    public static string ReaderFor(string format) => format switch
    {
        "md" or "txt" => "markdown",
        "html" => "html",
        "docx" => "docx",
        "odt" => "odt",
        "epub" => "epub",
        "rtf" => "rtf",
        _ => throw new ConversionException(T($"Pandoc cannot read {format}.", $"Pandoc не поддерживает чтение {format}.")),
    };

    public static string WriterFor(string format) => format switch
    {
        "md" => "gfm",
        "txt" => "plain",
        "html" => "html5",
        "docx" => "docx",
        "odt" => "odt",
        "epub" => "epub3",
        "rtf" => "rtf",
        _ => throw new ConversionException(T($"Pandoc cannot write {format}.", $"Pandoc не поддерживает запись {format}.")),
    };

    public static async Task ConvertAsync(OperationContext ctx, string input, string inputFormat, string output, string format)
    {
        var pandoc = ctx.Tools.Require(ExternalTool.Pandoc);
        // Pandoc runs in the input's folder (for images the document links to), so relative paths would point elsewhere.
        input = Path.GetFullPath(input);
        output = Path.GetFullPath(output);
        var args = new List<string>
        {
            input,
            "-f", ReaderFor(inputFormat),
            "-t", WriterFor(format),
            "-o", output,
        };
        if (format is "html" or "rtf" or "epub" || inputFormat == "html")
            args.Add("--standalone");
        if (format == "html")
            args.Add("--embed-resources");
        if (format is "html" or "epub")
            args.AddRange(["--metadata", "title=" + Formats.BaseNameOf(input)]);
        if (format is "txt")
            args.Add("--wrap=none");
        var mediaDir = Path.GetDirectoryName(output)!;
        if (format == "md" && inputFormat is "docx" or "odt" or "epub")
        {
            // Pictures from the document land next to the Markdown file.
            args.Add("--extract-media=" + Path.Combine(mediaDir, Formats.BaseNameOf(input) + " - media"));
        }
        var inputDir = Path.GetDirectoryName(Path.GetFullPath(input));
        if (inputDir is not null)
            args.Add("--resource-path=" + inputDir);
        await ProcessRunner.RunAsync(pandoc, args, ctx.CancellationToken, workingDirectory: inputDir).ConfigureAwait(false);
    }
}
