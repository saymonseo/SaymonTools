using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SaymonTools.Core;
using SaymonTools.Core.Backends;
using static SaymonTools.App.UI.Controls;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.UI;

/// <summary>
/// Trim with a preview: the video (or just its sound) plays in the window, the start and end are dragged
/// on a bar of frames and the picture follows the handle, so the cut points are visible before cutting.
/// </summary>
internal sealed class TrimDialog : Window
{
    private const int FilmstripFrames = 10;

    private readonly FileItem _item;
    private readonly bool _video;
    private readonly string? _ffmpeg;
    private readonly string _temp = OutputPaths.TempDirectory();
    private readonly CancellationTokenSource _closing = new();
    private readonly MediaElement _media = new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Close,
        ScrubbingEnabled = true,
        Stretch = Stretch.Uniform,
        Volume = 0.8,
    };
    private readonly Image _still = new() { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly TextBlock _notice = Text("", 13, brush: Theme.Muted);
    private readonly TrimTimeline _timeline = new();
    private readonly TextBox _startBox = TimeBox();
    private readonly TextBox _endBox = TimeBox();
    private readonly TextBlock _positionText = Text("0:00.0", 13, brush: Theme.Muted, wrap: false);
    private readonly TextBlock _lengthText = Text("", 13, FontWeights.SemiBold, Theme.Accent, wrap: false);
    private readonly TextBlock _playGlyph = new() { FontFamily = Theme.Icons, FontSize = 16, Foreground = Theme.TextOnAccent, Text = PlayGlyph };
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _stillTimer;
    private bool _mediaReady;
    private bool _playing;
    private bool _copy;
    private bool _stillBusy;
    private double _stillWanted = -1;
    private double _stillShown = -1;
    private int _framesLoaded;

    private const string PlayGlyph = "";
    private const string PauseGlyph = "";

    /// <summary>Show frames read by ffmpeg instead of playing (the self-test can't render a playing video).</summary>
    internal static bool StillsOnly { get; set; }

    public TrimDialog(FileItem item, ToolLocator tools)
    {
        _item = item;
        _video = Formats.Video.Contains(item.Format);
        _ffmpeg = tools.Has(ExternalTool.Ffmpeg) ? tools.Require(ExternalTool.Ffmpeg) : null;
        Theme.Refresh();
        Title = T($"Trim — {item.Name}", $"Обрезать — {item.Name}");
        Width = 920;
        Height = _video ? 760 : 430;
        MinWidth = 640;
        MinHeight = _video ? 560 : 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/SaymonTools.ico"));
        Background = Theme.Page;
        Foreground = Theme.Text;
        FontFamily = Theme.UiFont;
        Content = Layout();

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => FollowPlayback(), Dispatcher);
        _tick.Stop();
        _stillTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => ShowStill(), Dispatcher);
        _stillTimer.Stop();

        _timeline.RangeChanged += isStart =>
        {
            Pause();
            var t = isStart ? _timeline.Start : _timeline.End;
            _timeline.SetPosition(t);
            Seek(t);
            UpdateTexts();
        };
        _timeline.Seeked += () =>
        {
            Seek(_timeline.Position);
            UpdateTexts();
        };
        _media.MediaOpened += (_, _) =>
        {
            _mediaReady = true;
            if (_timeline.Duration <= TrimTimeline.MinLength && _media.NaturalDuration.HasTimeSpan)
                SetDuration(_media.NaturalDuration.TimeSpan.TotalSeconds);
            // Show the first frame without playing.
            _media.Play();
            _media.Pause();
            _media.Position = TimeSpan.FromSeconds(_timeline.Position);
        };
        _media.MediaFailed += (_, _) => UseStills(T("This format can't be played in the window; frames are shown instead.", "Этот формат нельзя воспроизвести в окне — показываются кадры."));
        _media.MediaEnded += (_, _) => Pause();

        KeyDown += OnKey;
        Loaded += (_, _) => Open();
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _tick.Stop();
            _media.Close();
            OutputPaths.TryDelete(_temp);
        };
    }

    /// <summary>The operation's parameters: start, end (empty = to the end) and whether to copy without re-encoding.</summary>
    public IReadOnlyDictionary<string, string> Values => new Dictionary<string, string>
    {
        ["start"] = Seconds(_timeline.Start),
        ["end"] = _timeline.End >= _timeline.Duration - 0.05 ? "" : Seconds(_timeline.End),
        ["copy"] = _copy ? "1" : "",
    };

    private static string Seconds(double t) => t.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- layout

    private UIElement Layout()
    {
        var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = Text(T("Trim", "Обрезать"), 22, FontWeights.SemiBold);
        title.FontFamily = Theme.DisplayFont;
        var name = Text(_item.Name, 13, brush: Theme.Muted, wrap: false);
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        head.Children.Add(title);
        head.Children.Add(name);
        root.Children.Add(head);

        // The picture (or, for sound, a plain panel that still hosts the player).
        var stage = new Grid();
        stage.Children.Add(_media);
        stage.Children.Add(_still);
        if (!_video)
        {
            _media.Visibility = Visibility.Collapsed;
            stage.Children.Add(new TextBlock
            {
                Text = Glyphs.Audio,
                FontFamily = Theme.Icons,
                FontSize = 46,
                Foreground = Theme.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        var screen = new Border
        {
            Background = _video ? Brushes.Black : Theme.Card,
            CornerRadius = Theme.CardCorners,
            ClipToBounds = true,
            Child = stage,
            MinHeight = _video ? 240 : 90,
        };
        screen.MouseLeftButtonUp += (_, _) => TogglePlay();
        Grid.SetRow(screen, 1);
        root.Children.Add(screen);

        _notice.Margin = new Thickness(0, 6, 0, 0);
        _notice.Visibility = Visibility.Collapsed;
        var timelineBox = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        timelineBox.Children.Add(_notice);
        timelineBox.Children.Add(_timeline);
        Grid.SetRow(timelineBox, 2);
        root.Children.Add(timelineBox);

        Grid.SetRow(Controls(), 3);
        root.Children.Add(Controls());

        var copy = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var toggle = Toggle(_copy, v => _copy = v);
        DockPanel.SetDock(toggle, Dock.Right);
        copy.Children.Add(toggle);
        var copyText = new StackPanel();
        copyText.Children.Add(Text(T("Fast, without re-encoding", "Быстро, без перекодирования"), 14));
        copyText.Children.Add(Text(T("Takes seconds and keeps the original quality, but the cut starts at the nearest keyframe (up to a few seconds earlier).",
            "Занимает секунды и сохраняет исходное качество, но начало сдвигается к ближайшему ключевому кадру (до нескольких секунд раньше)."), 12, brush: Theme.Muted));
        copy.Children.Add(copyText);
        Grid.SetRow(copy, 4);
        root.Children.Add(copy);

        var ok = Button(T("Trim", "Обрезать"), Accept, primary: true, glyph: Glyphs.Stopwatch);
        var cancel = Button(T("Cancel", "Отмена"), () => DialogResult = false);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 5);
        root.Children.Add(buttons);
        return root;
    }

    private FrameworkElement? _controls;

    /// <summary>Start box, play button with the time, end box; built once.</summary>
    private FrameworkElement Controls()
    {
        if (_controls is not null)
            return _controls;
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var start = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        start.Children.Add(Labeled(T("Start", "Начало"), _startBox));
        var setStart = Button(T("Here", "Отсюда"), () => SetStartHere(), glyph: "");
        setStart.ToolTip = T("Start at the playhead (I)", "Начало в текущей позиции (I)");
        setStart.VerticalAlignment = VerticalAlignment.Bottom;
        setStart.Margin = new Thickness(8, 0, 0, 0);
        start.Children.Add(setStart);
        grid.Children.Add(start);

        var middle = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
        var play = new PillBorder
        {
            Background = Theme.AccentGradient,
            Width = 64,
            Height = 40,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = T("Play the kept part (Space)", "Воспроизвести выбранный фрагмент (Пробел)"),
            Child = _playGlyph,
        };
        _playGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        _playGlyph.VerticalAlignment = VerticalAlignment.Center;
        play.MouseLeftButtonUp += (_, _) => TogglePlay();
        middle.Children.Add(play);
        _positionText.HorizontalAlignment = HorizontalAlignment.Center;
        _positionText.Margin = new Thickness(0, 6, 0, 0);
        middle.Children.Add(_positionText);
        _lengthText.HorizontalAlignment = HorizontalAlignment.Center;
        middle.Children.Add(_lengthText);
        Grid.SetColumn(middle, 1);
        grid.Children.Add(middle);

        var end = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var setEnd = Button(T("Here", "Досюда"), () => SetEndHere(), glyph: "");
        setEnd.ToolTip = T("End at the playhead (O)", "Конец в текущей позиции (O)");
        setEnd.VerticalAlignment = VerticalAlignment.Bottom;
        setEnd.Margin = new Thickness(0, 0, 8, 0);
        end.Children.Add(setEnd);
        end.Children.Add(Labeled(T("End", "Конец"), _endBox));
        Grid.SetColumn(end, 2);
        grid.Children.Add(end);

        _startBox.LostKeyboardFocus += (_, _) => CommitBox(_startBox, true);
        _endBox.LostKeyboardFocus += (_, _) => CommitBox(_endBox, false);
        _controls = grid;
        return grid;
    }

    private static TextBox TimeBox()
    {
        var box = TextField("", 12);
        box.Width = 120;
        box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        return box;
    }

    private static FrameworkElement Labeled(string label, FrameworkElement field)
    {
        var panel = new StackPanel();
        panel.Children.Add(FieldLabel(label, 0));
        panel.Children.Add(field);
        return panel;
    }

    // ---------------------------------------------------------------- media

    private void Open()
    {
        if (_ffmpeg is not null)
        {
            var ffmpeg = _ffmpeg;
            _ = Task.Run(async () =>
            {
                var info = await FfmpegBackend.ProbeAsync(ffmpeg, _item.Path, _closing.Token);
                if (info?.Duration is { } d)
                    await Dispatcher.InvokeAsync(() => SetDuration(d));
            });
        }
        if (StillsOnly && _video)
        {
            UseStills(null);
            return;
        }
        try
        {
            _media.Source = new Uri(_item.Path);
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
        {
            UseStills(T("The file can't be played in the window; frames are shown instead.", "Файл нельзя воспроизвести в окне — показываются кадры."));
        }
    }

    private void SetDuration(double seconds)
    {
        var first = _timeline.Duration <= TrimTimeline.MinLength;
        _timeline.SetDuration(seconds);
        if (first)
        {
            _timeline.SetStart(0);
            _timeline.SetEnd(seconds);
            if (_video)
                LoadFilmstrip(seconds);
            if (!_mediaReady)
                Seek(0);
        }
        UpdateTexts();
    }

    /// <summary>Frames along the bar, read one by one in the background.</summary>
    private void LoadFilmstrip(double duration)
    {
        if (_ffmpeg is not { } ffmpeg)
            return;
        _timeline.SetFrameCount(FilmstripFrames);
        var token = _closing.Token;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < FilmstripFrames && !token.IsCancellationRequested; i++)
            {
                var file = Path.Combine(_temp, $"strip{i}.jpg");
                try
                {
                    await FfmpegBackend.FrameAsync(ffmpeg, _item.Path, duration * (i + 0.5) / FilmstripFrames, 72, file, token);
                    var frame = Load(file);
                    var index = i;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _timeline.SetFrame(index, frame);
                        _framesLoaded++;
                    });
                }
                catch (Exception ex) when (ex is ConversionException or IOException or NotSupportedException or OperationCanceledException)
                {
                }
            }
        }, token);
    }

    private static BitmapImage Load(string file)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(file);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Stops trying to play and shows frames read by ffmpeg at the playhead.</summary>
    private void UseStills(string? notice)
    {
        _mediaReady = false;
        _media.Source = null;
        _media.Visibility = Visibility.Collapsed;
        if (_video)
            _still.Visibility = Visibility.Visible;
        if (notice is not null && _ffmpeg is not null)
        {
            _notice.Text = notice;
            _notice.Visibility = Visibility.Visible;
        }
        else if (_ffmpeg is null)
        {
            _notice.Text = T("Install FFmpeg (Settings → Helper programs) to see frames.", "Установите FFmpeg (Настройки → Доп. программы), чтобы видеть кадры.");
            _notice.Visibility = Visibility.Visible;
        }
        Seek(_timeline.Position);
    }

    private void Seek(double seconds)
    {
        if (_mediaReady)
        {
            _media.Position = TimeSpan.FromSeconds(seconds);
            return;
        }
        if (!_video || _ffmpeg is null)
            return;
        // Dragging produces many positions; only the latest one is read, a moment after the last move.
        _stillWanted = seconds;
        _stillTimer.Stop();
        _stillTimer.Start();
    }

    private async void ShowStill()
    {
        _stillTimer.Stop();
        if (_stillBusy || _stillWanted < 0 || _ffmpeg is not { } ffmpeg)
            return;
        _stillBusy = true;
        var wanted = _stillWanted;
        var file = Path.Combine(_temp, $"still-{Guid.NewGuid():N}.jpg");
        try
        {
            await FfmpegBackend.FrameAsync(ffmpeg, _item.Path, wanted, 540, file, _closing.Token);
            _still.Source = Load(file);
            _stillShown = wanted;
        }
        catch (Exception ex) when (ex is ConversionException or IOException or NotSupportedException or OperationCanceledException)
        {
        }
        finally
        {
            _stillBusy = false;
            OutputPaths.TryDelete(file);
        }
        // The handle moved on while this frame was read: catch up.
        if (Math.Abs(_stillWanted - wanted) > 0.001 && !_closing.IsCancellationRequested)
            _stillTimer.Start();
    }

    /// <summary>Plays the kept part: from the start handle (or the playhead inside it) up to the end handle.</summary>
    private void TogglePlay()
    {
        if (_playing)
        {
            Pause();
            return;
        }
        if (!_mediaReady)
            return;
        if (_timeline.Position < _timeline.Start || _timeline.Position >= _timeline.End - 0.05)
        {
            _timeline.SetPosition(_timeline.Start);
            _media.Position = TimeSpan.FromSeconds(_timeline.Start);
        }
        _media.Play();
        _playing = true;
        _playGlyph.Text = PauseGlyph;
        _tick.Start();
    }

    private void Pause()
    {
        if (!_playing)
            return;
        _media.Pause();
        _playing = false;
        _playGlyph.Text = PlayGlyph;
        _tick.Stop();
    }

    private void FollowPlayback()
    {
        var t = _media.Position.TotalSeconds;
        if (t >= _timeline.End)
        {
            Pause();
            t = _timeline.End;
            _media.Position = TimeSpan.FromSeconds(t);
        }
        _timeline.SetPosition(t);
        UpdateTexts(boxes: false);
    }

    // ---------------------------------------------------------------- editing

    private void SetStartHere()
    {
        _timeline.SetStart(_timeline.Position);
        UpdateTexts();
    }

    private void SetEndHere()
    {
        _timeline.SetEnd(_timeline.Position);
        UpdateTexts();
    }

    private void CommitBox(TextBox box, bool start)
    {
        try
        {
            var t = FfmpegBackend.ParseTime(box.Text);
            if (start)
                _timeline.SetStart(t);
            else
                _timeline.SetEnd(t);
            _timeline.SetPosition(start ? _timeline.Start : _timeline.End);
            Seek(_timeline.Position);
        }
        catch (ConversionException)
        {
        }
        UpdateTexts();
    }

    private void UpdateTexts(bool boxes = true)
    {
        if (boxes)
        {
            _startBox.Text = Format(_timeline.Start);
            _endBox.Text = Format(_timeline.End);
        }
        _positionText.Text = $"{Format(_timeline.Position)} / {Format(_timeline.Duration)}";
        _lengthText.Text = _timeline.Duration > TrimTimeline.MinLength
            ? T($"Kept: {Format(_timeline.End - _timeline.Start)}", $"Останется: {Format(_timeline.End - _timeline.Start)}")
            : "";
    }

    /// <summary>"1:02.5" or "1:02:03.5".</summary>
    internal static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds, 1)));
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            return;
        }
        if (Keyboard.FocusedElement is TextBox)
        {
            if (e.Key == Key.Enter)
            {
                CommitBox(_startBox, true);
                CommitBox(_endBox, false);
                e.Handled = true;
            }
            return;
        }
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 1;
        switch (e.Key)
        {
            case Key.Space:
                TogglePlay();
                break;
            case Key.Left or Key.Right:
                Pause();
                _timeline.SetPosition(_timeline.Position + (e.Key == Key.Left ? -step : step));
                Seek(_timeline.Position);
                UpdateTexts(boxes: false);
                break;
            case Key.I:
                SetStartHere();
                break;
            case Key.O:
                SetEndHere();
                break;
            case Key.Enter:
                Accept();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Accept()
    {
        CommitBox(_startBox, true);
        CommitBox(_endBox, false);
        if (_timeline.Duration <= TrimTimeline.MinLength && _startBox.Text.Length == 0)
            return;
        DialogResult = true;
    }

    /// <summary>For the self-test: wait until the length is known, then set a range.</summary>
    internal bool Ready => _timeline.Duration > TrimTimeline.MinLength;

    /// <summary>For the self-test, which never shows the window: start reading the file.</summary>
    internal void OpenForPreview() => Open();

    /// <summary>For the self-test: the length, all frames of the bar and the frame at the playhead are in.</summary>
    internal bool PreviewComplete => Ready && _framesLoaded == FilmstripFrames && Math.Abs(_stillShown - _stillWanted) < 0.001;

    internal void SelectForPreview(double start, double end)
    {
        _timeline.SetStart(start);
        _timeline.SetEnd(end);
        _timeline.SetPosition(start);
        Seek(start);
        UpdateTexts();
    }
}
