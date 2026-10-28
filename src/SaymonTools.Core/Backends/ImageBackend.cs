using ImageMagick;
using static SaymonTools.Core.Loc;

namespace SaymonTools.Core.Backends;

/// <summary>Image conversions and edits through Magick.NET (ImageMagick, runs in-process).</summary>
public static class ImageBackend
{
    private static MagickImage Load(string path)
    {
        var settings = new MagickReadSettings();
        if (Formats.KeyOf(path) == "svg")
        {
            settings.Density = new Density(192);
            settings.BackgroundColor = MagickColors.Transparent;
        }
        // Only the first frame for animated GIF/WebP/multi-page TIFF.
        settings.FrameIndex = 0;
        settings.FrameCount = 1;
        var image = new MagickImage(path, settings);
        image.AutoOrient();
        return image;
    }

    /// <summary>When a photo was taken, from its EXIF data (reads only the header); null when unknown.</summary>
    public static DateTime? TakenAt(string path)
    {
        if (!Formats.Images.Contains(Formats.KeyOf(path)))
            return null;
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            var exif = image.GetExifProfile();
            var text = exif?.GetValue(ExifTag.DateTimeOriginal)?.Value ?? exif?.GetValue(ExifTag.DateTime)?.Value;
            return DateTime.TryParseExact(text?.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var taken) ? taken : null;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    public static MagickFormat ToMagickFormat(string format) => format switch
    {
        "jpg" => MagickFormat.Jpeg,
        "png" => MagickFormat.Png,
        "webp" => MagickFormat.WebP,
        "heic" => MagickFormat.Heic,
        "avif" => MagickFormat.Avif,
        "tiff" => MagickFormat.Tiff,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "ico" => MagickFormat.Ico,
        "pdf" => MagickFormat.Pdf,
        _ => throw new ConversionException(T($"Unsupported image format: {format}", $"Неподдерживаемый формат изображения: {format}")),
    };

    private static void PrepareFor(MagickImage image, string format, int quality)
    {
        image.Quality = (uint)Math.Clamp(quality, 1, 100);
        if (format is "jpg" or "bmp" or "pdf" && image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
        }
        if (format == "ico" && (image.Width > 256 || image.Height > 256))
            image.Resize(new MagickGeometry(256, 256));
        if (format == "ico")
            image.Settings.SetDefine(MagickFormat.Ico, "auto-resize", "256,128,64,48,32,16");
    }

    public static Task ConvertAsync(string input, string output, string format, Settings settings, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var image = Load(input);
            ct.ThrowIfCancellationRequested();
            PrepareFor(image, format, settings.JpegQuality);
            image.Write(output, ToMagickFormat(format));
        }, ct);

    /// <summary>Applies <paramref name="edit"/> and saves in the source format.</summary>
    public static Task EditAsync(string input, string output, Action<MagickImage> edit, int? quality, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var image = Load(input);
            var format = OutputFormatFor(Formats.KeyOf(input));
            edit(image);
            ct.ThrowIfCancellationRequested();
            PrepareFor(image, format, quality ?? 92);
            image.Write(output, ToMagickFormat(format));
        }, ct);

    /// <summary>Formats we can read but not write (SVG) are saved as PNG.</summary>
    public static string OutputFormatFor(string inputFormat) => inputFormat switch
    {
        "svg" => "png",
        _ => inputFormat,
    };

    public static Task CompressAsync(string input, string output, int quality, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var image = Load(input);
            var format = OutputFormatFor(Formats.KeyOf(input));
            image.Strip();
            image.Quality = (uint)Math.Clamp(quality, 1, 100);
            if (format == "png")
            {
                // Lossy palette reduction gives the biggest win for PNG.
                if (image.TotalColors > 256)
                    image.Quantize(new QuantizeSettings { Colors = 256, DitherMethod = DitherMethod.FloydSteinberg });
                image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
            }
            else if (format == "jpg")
            {
                image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:2:0");
                image.Settings.Interlace = Interlace.Jpeg;
            }
            PrepareFor(image, format, quality);
            image.Write(output, ToMagickFormat(format));
        }, ct);

    /// <summary>A user preset: optional resize, grayscale and metadata removal, saved in <paramref name="format"/>.</summary>
    public static Task PresetAsync(string input, string output, string format, int quality, int? maxSide, int? percent,
        bool grayscale, bool strip, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var image = Load(input);
            if (maxSide is > 0)
                ResizeMax(image, (uint)maxSide.Value);
            else if (percent is > 0 and < 100)
                ResizePercent(image, percent.Value);
            if (grayscale)
                Grayscale(image);
            if (strip)
                image.Strip();
            ct.ThrowIfCancellationRequested();
            PrepareFor(image, format, quality);
            image.Write(output, ToMagickFormat(format));
        }, ct);

    /// <summary>Width and height without decoding the pixels; null when the file can't be read.</summary>
    public static (int Width, int Height)? Dimensions(string path)
    {
        try
        {
            using var image = new MagickImage();
            image.Ping(path);
            // Phone photos are often stored sideways with an EXIF hint; report them the way they are shown.
            // EXIF orientation 5–8 means rotated by 90° or 270°.
            var sideways = image.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value is >= 5 and <= 8;
            return sideways ? ((int)image.Height, (int)image.Width) : ((int)image.Width, (int)image.Height);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>The "Custom…" conversion: size, grayscale, metadata, then quality or a target file size.</summary>
    public static Task CustomAsync(string input, string output, ImageSettings s, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var image = Load(input);
            switch (s.Resize)
            {
                case ImageResize.LongSide:
                    ResizeMax(image, (uint)s.LongSide);
                    break;
                case ImageResize.Percent when s.Percent != 100:
                    ResizePercent(image, s.Percent);
                    break;
                case ImageResize.Exact when s.Crop:
                    // Cover the frame, then cut the overhang evenly from both sides.
                    image.Resize(new MagickGeometry((uint)s.Width, (uint)s.Height) { FillArea = true });
                    image.Crop((uint)s.Width, (uint)s.Height, Gravity.Center);
                    image.ResetPage();
                    break;
                case ImageResize.Exact:
                    image.Resize(new MagickGeometry((uint)s.Width, (uint)s.Height));
                    break;
            }
            if (s.Grayscale)
                Grayscale(image);
            if (s.StripMetadata)
                image.Strip();
            ct.ThrowIfCancellationRequested();

            var format = s.OutputFor(Formats.KeyOf(input));
            if (s.TargetKb is { } kb && ImageSettings.IsLossy(format))
            {
                File.WriteAllBytes(output, FitToSize(image, format, kb * 1024L, s.Quality, ct));
                return;
            }
            PrepareFor(image, format, s.Quality);
            image.Write(output, ToMagickFormat(format));
        }, ct);

    /// <summary>
    /// The best quality (at most <paramref name="maxQuality"/>) that fits in <paramref name="maxBytes"/>;
    /// if even low quality is too big, the picture is made smaller step by step.
    /// </summary>
    internal static byte[] FitToSize(MagickImage image, string format, long maxBytes, int maxQuality, CancellationToken ct)
    {
        var magick = ToMagickFormat(format);
        byte[] Encode(MagickImage img, int quality)
        {
            ct.ThrowIfCancellationRequested();
            PrepareFor(img, format, quality);
            return img.ToByteArray(magick);
        }

        var current = (MagickImage)image.Clone();
        try
        {
            for (var shrink = 0; shrink < 8; shrink++)
            {
                byte[]? best = null;
                int low = 10, high = Math.Clamp(maxQuality, 10, 100);
                while (low <= high)
                {
                    var mid = (low + high) / 2;
                    var bytes = Encode(current, mid);
                    if (bytes.LongLength <= maxBytes)
                    {
                        best = bytes;
                        low = mid + 1;
                    }
                    else
                    {
                        high = mid - 1;
                    }
                }
                if (best is not null)
                    return best;
                current.Resize(new Percentage(80));
            }
            throw new ConversionException(T("Couldn't make the picture that small.", "Не удалось сжать изображение до такого размера."));
        }
        finally
        {
            current.Dispose();
        }
    }

    public static Task ImagesToPdfAsync(IReadOnlyList<string> inputs, string output, int quality, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var collection = new MagickImageCollection();
            foreach (var input in inputs)
            {
                ct.ThrowIfCancellationRequested();
                var image = Load(input);
                PrepareFor(image, "pdf", quality);
                collection.Add(image);
            }
            collection.Write(output, MagickFormat.Pdf);
        }, ct);

    public static void ResizePercent(MagickImage image, double percent) =>
        image.Resize(new Percentage(percent));

    public static void ResizeMax(MagickImage image, uint maxSide)
    {
        if (image.Width <= maxSide && image.Height <= maxSide)
            return;
        image.Resize(new MagickGeometry(maxSide, maxSide));
    }

    public static void CropSquare(MagickImage image)
    {
        var side = Math.Min(image.Width, image.Height);
        image.Crop(side, side, Gravity.Center);
        image.ResetPage();
    }

    public static void Grayscale(MagickImage image) => image.Grayscale(PixelIntensityMethod.Rec709Luminance);
}
