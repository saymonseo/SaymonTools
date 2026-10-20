using System.IO;
using System.Text;
using SaymonTools.Core;
using SaymonTools.Core.Backends;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.Ocr;

/// <summary>
/// "Extract text": recognises text in images and scanned PDFs with the OCR engine built into Windows
/// (runs locally, uses the languages installed in Windows). Saves a .txt and copies the text.
/// </summary>
internal sealed class OcrOperation : Operation
{
    private static bool? _available;

    public OcrOperation()
        : base("tools.ocr", WheelKind.Tools, T("Extract text", "Извлечь текст"), Glyphs.Text, "OCR")
    {
    }

    /// <summary>Called on the UI thread to put the recognised text on the clipboard.</summary>
    public static Action<string>? CopyToClipboard { get; set; }

    public override bool AppliesTo(IReadOnlyList<FileItem> items) => items.Count > 0 && items.All(Fits);

    public override IReadOnlyList<FileItem> Applicable(IReadOnlyList<FileItem> items) => [.. items.Where(Fits)];

    private static bool Fits(FileItem item) => !item.IsDirectory && (Formats.Images.Contains(item.Format) || item.Format == "pdf");

    public override bool IsAvailable(ToolLocator tools, IReadOnlyList<FileItem> items) => Available;

    public static bool Available
    {
        get
        {
            try
            {
                return _available ??= OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch (Exception)
            {
                // Windows.Media.Ocr missing (e.g. Windows N editions without the media pack).
                return (_available = false).Value;
            }
        }
    }

    public override async Task<OperationResult> ExecuteAsync(IReadOnlyList<FileItem> items, OperationContext ctx)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e is not null)
            ?? throw new ConversionException(T("Windows has no text recognition installed. Add a language in Settings → Time & language → Language & region.", "В Windows не установлено распознавание текста. Добавьте язык: Параметры → Время и язык → Язык и регион."));

        var outputs = new List<string>();
        var all = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var temp = OutputPaths.TempDirectory();
            try
            {
                var pages = await Task.Run(() => PagesOf(item, temp, ctx), ctx.CancellationToken);
                var text = new StringBuilder();
                for (var p = 0; p < pages.Count; p++)
                {
                    ctx.CancellationToken.ThrowIfCancellationRequested();
                    var pageText = await RecognizeAsync(engine, pages[p]);
                    if (pageText.Length > 0)
                    {
                        if (text.Length > 0)
                            text.Append("\n\n");
                        text.Append(pageText);
                    }
                    ctx.Progress.Report((i + (p + 1.0) / pages.Count) / items.Count);
                }
                if (text.Length == 0)
                    throw new ConversionException(T("No text was found in the file.", "В файле не найден текст."));

                outputs.Add(await OutputPaths.ProduceAsync(ctx.OutputDirFor(item.Path), $"{item.BaseName} ({T("text", "текст")})", "txt",
                    tmp => File.WriteAllTextAsync(tmp, text.ToString(), new UTF8Encoding(false), ctx.CancellationToken)));
                if (all.Length > 0)
                    all.Append("\n\n");
                all.Append(text);
            }
            finally
            {
                OutputPaths.TryDelete(temp);
            }
        }

        CopyToClipboard?.Invoke(all.ToString());
        return new OperationResult(outputs, Note: T("The text is also on the clipboard.", "Текст также скопирован в буфер обмена."));
    }

    private static IReadOnlyList<string> PagesOf(FileItem item, string temp, OperationContext ctx)
    {
        if (item.Format == "pdf")
            return PdfBackend.RenderPagesToPng(item.Path, temp, 300, ctx.CancellationToken);
        // Magick reads every format we support (HEIC, AVIF, SVG…); hand Windows a plain PNG.
        var png = Path.Combine(temp, "image.png");
        ImageBackend.ConvertAsync(item.Path, png, "png", ctx.Settings, ctx.CancellationToken).GetAwaiter().GetResult();
        return [png];
    }

    public static async Task<string> RecognizeAsync(OcrEngine engine, string png)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(png));
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        // The engine rejects images above MaxImageDimension; scale those down.
        var max = OcrEngine.MaxImageDimension;
        var scale = Math.Min(1.0, (double)max / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
    }
}
