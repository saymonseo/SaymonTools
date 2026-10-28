using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SaymonTools.Core;
using SaymonTools.Core.Backends;
using static SaymonTools.App.UI.Controls;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.UI;

/// <summary>
/// "Custom…" for video and images: every setting of the conversion in one window, with what the source
/// file is like and an option to keep the choice as a preset. Also edits the settings of such a preset.
/// </summary>
internal sealed class CustomOptionsDialog : Window
{
    private readonly bool _video;
    private readonly bool _presetMode;
    private readonly IReadOnlyList<FileItem> _items;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    // Text boxes survive rebuilds (every choice rebuilds the page), so typing isn't interrupted.
    private readonly TextBox _target;
    private readonly TextBox _width;
    private readonly TextBox _height;
    private readonly TextBox _presetName;

    private VideoSettings _videoSettings;
    private ImageSettings _imageSettings;
    private string _sourceInfo = "";
    private int? _sourceShortSide;
    private bool _saveAsPreset;

    /// <param name="presetMode">Editing a preset: no files, no "save as preset", the button says Save.</param>
    public CustomOptionsDialog(bool video, IReadOnlyList<FileItem> items, VideoSettings? video0, ImageSettings? image0, ToolLocator tools, bool presetMode = false)
    {
        _video = video;
        _presetMode = presetMode;
        _items = items;
        _videoSettings = video0 ?? new VideoSettings();
        _imageSettings = image0 ?? new ImageSettings();
        Theme.Refresh();
        Title = video ? T("Video settings", "Настройки видео") : T("Image settings", "Настройки изображения");
        Width = 700;
        Height = 820;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/SaymonTools.ico"));
        Background = Theme.Page;
        FontFamily = Theme.UiFont;
        Foreground = Theme.Text;
        Content = _scroll;

        _target = TextField(video ? (_videoSettings.TargetMb ?? 50).ToString(CultureInfo.InvariantCulture) : (_imageSettings.TargetKb ?? 500).ToString(CultureInfo.InvariantCulture), 6);
        _target.Width = 110;
        _target.TextChanged += (_, _) => ApplyTarget(rebuild: false);
        _width = TextField(_imageSettings.Width.ToString(CultureInfo.InvariantCulture), 5);
        _width.Width = 100;
        _width.TextChanged += (_, _) => { if (Number(_width) is { } w) _imageSettings = _imageSettings with { Width = w }; };
        _height = TextField(_imageSettings.Height.ToString(CultureInfo.InvariantCulture), 5);
        _height.Width = 100;
        _height.TextChanged += (_, _) => { if (Number(_height) is { } h) _imageSettings = _imageSettings with { Height = h }; };
        _presetName = TextField("", 40);

        DescribeSource(tools);
        Build();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                DialogResult = false;
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                Accept();
        };
    }

    public static bool Handles(string operationId) => operationId is VideoSettings.OperationId or ImageSettings.OperationId;

    public VideoSettings VideoResult => _videoSettings;

    public ImageSettings ImageResult => _imageSettings;

    public IReadOnlyDictionary<string, string> Parameters => _video ? _videoSettings.ToParameters() : _imageSettings.ToParameters();

    /// <summary>The name for a new preset when the user asked to keep the settings; null otherwise.</summary>
    public string? PresetName => _saveAsPreset && !_presetMode ? _presetName.Text.Trim() : null;

    // ---------------------------------------------------------------- source

    private void DescribeSource(ToolLocator tools)
    {
        if (_items.Count == 0)
            return;
        var first = _items[0];
        var count = _items.Count > 1 ? T($" (+{_items.Count - 1} more)", $" (и ещё {_items.Count - 1})") : "";
        var size = File.Exists(first.Path) ? " · " + FileSize(new FileInfo(first.Path).Length) : "";
        if (!_video)
        {
            var dims = ImageBackend.Dimensions(first.Path);
            _sourceShortSide = dims is { } d ? Math.Min(d.Width, d.Height) : null;
            _sourceInfo = first.Name + count + (dims is { } wh ? $" · {wh.Width}×{wh.Height}" : "") + size;
            return;
        }
        _sourceInfo = first.Name + count + size;
        if (!tools.Has(ExternalTool.Ffmpeg))
            return;
        // Reading the video's details takes a moment; the window shows them when they arrive.
        var ffmpeg = tools.Require(ExternalTool.Ffmpeg);
        _ = Task.Run(async () =>
        {
            var info = await FfmpegBackend.ProbeAsync(ffmpeg, first.Path, CancellationToken.None);
            if (info is null)
                return;
            await Dispatcher.InvokeAsync(() =>
            {
                _sourceInfo = first.Name + count + " · " + info.Describe() + size;
                _sourceShortSide = info.ShortSide;
                if (IsLoaded)
                    Rebuild();
            });
        });
    }

    private static string FileSize(long bytes) => bytes switch
    {
        >= 1L << 30 => T($"{bytes / (double)(1L << 30):0.0} GB", $"{bytes / (double)(1L << 30):0.0} ГБ"),
        >= 1L << 20 => T($"{bytes / (double)(1L << 20):0.0} MB", $"{bytes / (double)(1L << 20):0.0} МБ"),
        _ => T($"{Math.Max(1, bytes / 1024)} KB", $"{Math.Max(1, bytes / 1024)} КБ"),
    };

    // ---------------------------------------------------------------- layout

    private void Build()
    {
        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        var title = Text(Title, 24, FontWeights.SemiBold);
        title.FontFamily = Theme.DisplayFont;
        root.Children.Add(title);
        if (_sourceInfo.Length > 0)
        {
            var source = Text(T("Source: ", "Исходник: ") + _sourceInfo, 13, brush: Theme.Muted);
            source.Margin = new Thickness(0, 4, 0, 0);
            root.Children.Add(source);
        }

        if (_video)
            VideoOptions(root);
        else
            ImageOptions(root);

        var summary = Text(_video ? _videoSettings.Summary() : _imageSettings.Summary(), 13, FontWeights.SemiBold, Theme.Accent);
        summary.Margin = new Thickness(0, 22, 0, 0);
        root.Children.Add(summary);

        if (!_presetMode)
        {
            root.Children.Add(OptionRow(T("Save as a preset", "Сохранить как шаблон"), _saveAsPreset, v =>
            {
                _saveAsPreset = v;
                Rebuild();
            }));
            if (_saveAsPreset)
            {
                (_presetName.Parent as Panel)?.Children.Remove(_presetName);
                root.Children.Add(FieldLabel(T("Preset name (appears on the wheel)", "Название шаблона (появится в меню)"), 10));
                root.Children.Add(_presetName);
            }
        }

        var ok = Button(_presetMode ? T("Save", "Сохранить") : T("Convert", "Конвертировать"), Accept, primary: true, glyph: _presetMode ? "" : Glyphs.Play);
        var cancel = Button(T("Cancel", "Отмена"), () => DialogResult = false);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        _scroll.Content = root;
    }

    private void Rebuild()
    {
        var offset = _scroll.VerticalOffset;
        Build();
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(offset);
    }

    // ---------------------------------------------------------------- video

    private void VideoOptions(Panel root)
    {
        var s = _videoSettings;
        root.Children.Add(FieldLabel(T("Format", "Формат"), 20));
        root.Children.Add(Choice([.. VideoSettings.Containers.Select(f => (Formats.Display(f), f))], s.Format, v => SetVideo(s with { Format = v })));

        root.Children.Add(FieldLabel(T("Resolution", "Разрешение")));
        root.Children.Add(Choice<int?>(
            [(T("Original", "Исходное"), null), .. VideoSettings.Heights.Select(h => (h >= 1440 ? $"{VideoSettings.ResolutionName(h)} · {h}p" : $"{h}p", (int?)h))],
            s.Height, v => SetVideo(s with { Height = v })));
        root.Children.Add(Hint(_sourceShortSide is { } side
            ? T($"The shorter side of the frame; the source is {side}p. Video is never enlarged.", $"Короткая сторона кадра; у исходника {side}p. Видео не увеличивается.")
            : T("The shorter side of the frame. Video is never enlarged.", "Короткая сторона кадра. Видео не увеличивается.")));

        root.Children.Add(FieldLabel(T("Quality", "Качество")));
        root.Children.Add(Choice<VideoLevel?>(
            [(T("Best", "Максимальное"), VideoLevel.Best), (T("High", "Высокое"), VideoLevel.High), (T("Medium", "Среднее"), VideoLevel.Medium), (T("Compact", "Компактное"), VideoLevel.Small), (T("File size", "Размер файла"), null)],
            s.TargetMb is null ? s.Quality : null,
            v =>
            {
                if (v is { } level)
                    SetVideo(s with { Quality = level, TargetMb = null });
                else
                {
                    _videoSettings = s with { TargetMb = Number(_target) ?? 50 };
                    Rebuild();
                }
            }));
        if (s.TargetMb is not null)
        {
            root.Children.Add(TargetRow(T("MB", "МБ")));
            root.Children.Add(Hint(T("The bitrate is worked out from the length; the result is within about 10 %.", "Битрейт подбирается по длительности; итог может отличаться примерно на 10 %.")));
        }

        if (!s.IsWebm)
        {
            root.Children.Add(FieldLabel(T("Codec", "Кодек")));
            root.Children.Add(Choice([(T("H.264 — plays everywhere", "H.264 — играет везде"), VideoCodec.H264), (T("H.265 — smaller file", "H.265 — меньше файл"), VideoCodec.H265)],
                s.Codec, v => SetVideo(s with { Codec = v })));
        }

        root.Children.Add(FieldLabel(T("Frame rate", "Частота кадров")));
        root.Children.Add(Choice<int?>([(T("Original", "Исходная"), null), .. VideoSettings.FrameRates.Select(f => (T($"{f} fps", $"{f} к/с"), (int?)f))],
            s.Fps, v => SetVideo(s with { Fps = v })));

        root.Children.Add(FieldLabel(T("Sound", "Звук")));
        root.Children.Add(Choice([(T("Keep", "Оставить"), SoundMode.Keep), (T("Compress", "Сжать"), SoundMode.Compact), (T("Remove", "Без звука"), SoundMode.Remove)],
            s.Sound, v => SetVideo(s with { Sound = v })));

        root.Children.Add(FieldLabel(T("Encoding", "Кодирование")));
        root.Children.Add(Choice([(T("Fast", "Быстро"), EncodeSpeed.Fast), (T("Balanced", "Баланс"), EncodeSpeed.Balanced), (T("Thorough", "Тщательно"), EncodeSpeed.Thorough)],
            s.Speed, v => SetVideo(s with { Speed = v })));
        root.Children.Add(Hint(T("Thorough gives a smaller file at the same quality but takes longer.", "«Тщательно» даёт файл меньше при том же качестве, но кодирует дольше.")));
    }

    private void SetVideo(VideoSettings settings)
    {
        _videoSettings = settings;
        Rebuild();
    }

    // ---------------------------------------------------------------- images

    private void ImageOptions(Panel root)
    {
        var s = _imageSettings;
        root.Children.Add(FieldLabel(T("Format", "Формат"), 20));
        root.Children.Add(Choice([.. ImageSettings.OutputFormats.Select(f => (f == ImageSettings.SameFormat ? T("Same", "Тот же") : Formats.Display(f), f))],
            s.Format, v => SetImage(s with { Format = v })));

        root.Children.Add(FieldLabel(T("Size", "Размер")));
        root.Children.Add(Choice([(T("Original", "Исходный"), ImageResize.Original), (T("Longer side", "По длинной стороне"), ImageResize.LongSide), (T("Percent", "В процентах"), ImageResize.Percent), (T("Exact", "Точный"), ImageResize.Exact)],
            s.Resize, v => SetImage(s with { Resize = v })));
        switch (s.Resize)
        {
            case ImageResize.LongSide:
                root.Children.Add(Spaced(Choice([.. ImageSettings.LongSides.Select(px => ($"{px} px", px))], s.LongSide, v => SetImage(s with { LongSide = v }))));
                root.Children.Add(Hint(T("Smaller pictures are not enlarged.", "Изображения меньше этого размера не увеличиваются.")));
                break;
            case ImageResize.Percent:
                root.Children.Add(Spaced(Choice([.. ImageSettings.Percents.Select(p => ($"{p} %", p))], s.Percent, v => SetImage(s with { Percent = v }))));
                break;
            case ImageResize.Exact:
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                Detach(_width);
                Detach(_height);
                row.Children.Add(_width);
                var times = Text("×", 16, brush: Theme.Muted);
                times.Margin = new Thickness(10, 0, 10, 0);
                times.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(times);
                row.Children.Add(_height);
                var px = Text("px", 13, brush: Theme.Muted);
                px.Margin = new Thickness(8, 0, 0, 0);
                px.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(px);
                root.Children.Add(row);
                root.Children.Add(Spaced(Choice([(T("Fill and crop", "Заполнить и обрезать"), true), (T("Fit inside", "Вписать"), false)], s.Crop, v => SetImage(s with { Crop = v }))));
                break;
        }
        if (_sourceInfo.Length > 0 && _sourceShortSide is null && s.Resize != ImageResize.Original)
            root.Children.Add(Hint(T("Couldn't read the picture's size.", "Не удалось прочитать размер изображения.")));

        var lossy = s.Format == ImageSettings.SameFormat
            ? _items.Count == 0 || _items.Any(i => ImageSettings.IsLossy(ImageBackend.OutputFormatFor(i.Format)))
            : ImageSettings.IsLossy(s.Format);
        if (lossy)
        {
            root.Children.Add(OptionRow(T("Limit the file size", "Ограничить размер файла"), s.TargetKb is not null, v =>
                SetImage(s with { TargetKb = v ? Number(_target) ?? 500 : null })));
            if (s.TargetKb is not null)
            {
                root.Children.Add(TargetRow(T("KB", "КБ")));
                root.Children.Add(Hint(T("Quality goes down until the file fits; if that isn't enough, the picture gets smaller.", "Качество снижается, пока файл не уложится; если этого мало, изображение уменьшается.")));
            }
            else
            {
                root.Children.Add(FieldLabel(T("Quality", "Качество")));
                var slider = Slider(30, 100, s.Quality, v => _imageSettings = _imageSettings with { Quality = v }, Rebuild);
                slider.HorizontalAlignment = HorizontalAlignment.Left;
                root.Children.Add(slider);
                root.Children.Add(Hint(T("80–90 looks like the original; below 60 artefacts show.", "80–90 почти не отличить от оригинала; ниже 60 заметны артефакты.")));
            }
        }
        else
        {
            root.Children.Add(Hint(T("PNG is lossless: quality and file size limits don't apply.", "PNG сохраняется без потерь: качество и ограничение размера не применяются.")));
        }

        root.Children.Add(OptionRow(T("Grayscale", "Чёрно-белое"), s.Grayscale, v => SetImage(s with { Grayscale = v })));
        root.Children.Add(OptionRow(T("Remove metadata (GPS, camera, date)", "Удалить метаданные (GPS, камера, дата)"), s.StripMetadata, v => SetImage(s with { StripMetadata = v })));
    }

    private void SetImage(ImageSettings settings)
    {
        _imageSettings = settings;
        Rebuild();
    }

    // ---------------------------------------------------------------- shared pieces

    private FrameworkElement TargetRow(string unit)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        Detach(_target);
        row.Children.Add(_target);
        var label = Text(unit, 13, brush: Theme.Muted);
        label.Margin = new Thickness(8, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        return row;
    }

    private void ApplyTarget(bool rebuild)
    {
        var value = Number(_target);
        if (value is null)
            return;
        if (_video && _videoSettings.TargetMb is not null)
            _videoSettings = _videoSettings with { TargetMb = value };
        else if (!_video && _imageSettings.TargetKb is not null)
            _imageSettings = _imageSettings with { TargetKb = value };
        if (rebuild)
            Rebuild();
    }

    private void Accept()
    {
        ApplyTarget(rebuild: false);
        DialogResult = true;
    }

    private static int? Number(TextBox box) =>
        int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;

    private static void Detach(FrameworkElement element) => (element.Parent as Panel)?.Children.Remove(element);

    private static FrameworkElement Choice<TValue>(IReadOnlyList<(string Label, TValue Value)> options, TValue selected, Action<TValue> changed)
    {
        var choice = Segmented(options, selected, changed);
        choice.HorizontalAlignment = HorizontalAlignment.Left;
        return choice;
    }

    private static FrameworkElement Spaced(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 10, 0, 0);
        return element;
    }

    private static FrameworkElement OptionRow(string label, bool value, Action<bool> changed)
    {
        var row = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var toggle = Toggle(value, changed);
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        var text = Text(label, 14);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        return row;
    }

    private static TextBlock Hint(string text)
    {
        var hint = Text(text, 12, brush: Theme.Muted);
        hint.Margin = new Thickness(0, 6, 0, 0);
        return hint;
    }
}
