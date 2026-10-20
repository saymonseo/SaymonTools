using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SaymonTools.Core;
using SaymonTools.Core.Backends;
using SaymonTools.Core.Rename;
using static SaymonTools.App.UI.Controls;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.UI;

/// <summary>Batch rename: a name template, find &amp; replace, case and numbering, with a live preview.</summary>
internal sealed class RenameDialog : Window
{
    /// <summary>Rows drawn in the preview; the rest are summarised (the plan itself covers every file).</summary>
    private const int PreviewRows = 400;

    private readonly IReadOnlyList<string> _paths;
    private readonly Dictionary<string, DateTime?> _taken = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refresh;
    private readonly TextBox _template;
    private readonly TextBox _find;
    private readonly TextBox _replace;
    private readonly TextBox _start;
    private readonly TextBox _step;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _summary;
    private readonly Border _apply;
    private RenameOptions _options;
    private IReadOnlyList<RenameEntry> _plan = [];

    public RenameDialog(IReadOnlyList<FileItem> items, RenameOptions? last = null)
    {
        _paths = [.. items.Select(i => i.Path)];
        // The numbering order is part of the choice, but a template the user typed for another batch is kept.
        _options = last ?? new RenameOptions { Template = items.Count > 1 ? "{name}_{n:00}" : "{name}" };
        Theme.Refresh();
        Title = items.Count == 1 ? T("Rename", "Переименовать") : T($"Rename {items.Count} files", $"Переименовать файлы: {items.Count}");
        Width = 1000;
        Height = 700;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/SaymonTools.ico"));
        Background = Theme.Page;
        FontFamily = Theme.UiFont;
        Foreground = Theme.Text;

        _refresh = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _refresh.Stop();
        _template = Field(_options.Template, (o, v) => o with { Template = v });
        _find = Field(_options.Find, (o, v) => o with { Find = v });
        _replace = Field(_options.Replace, (o, v) => o with { Replace = v });
        _start = Number(_options.Start, (o, v) => o with { Start = v });
        _step = Number(_options.Step, (o, v) => o with { Step = v });
        _summary = Text("", 13, brush: Theme.Muted);
        _apply = Button(T("Rename", "Переименовать"), Accept, primary: true, glyph: Glyphs.Rename);

        var grid = new Grid { Margin = new Thickness(28, 24, 28, 24) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var options = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = OptionsPanel() };
        grid.Children.Add(options);
        var preview = PreviewPanel();
        Grid.SetColumn(preview, 2);
        grid.Children.Add(preview);
        Content = grid;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                DialogResult = false;
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
                Accept();
        };
        Loaded += (_, _) =>
        {
            Keyboard.Focus(_template);
            _template.SelectAll();
        };
        Refresh();
    }

    /// <summary>The checked plan; valid after the dialog returned true.</summary>
    public IReadOnlyList<RenameEntry> Plan => _plan;

    /// <summary>The settings used, to start from next time.</summary>
    public RenameOptions Options => _options;

    private FrameworkElement OptionsPanel()
    {
        var root = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var title = Text(Title, 24, FontWeights.SemiBold);
        title.FontFamily = Theme.DisplayFont;
        root.Children.Add(title);
        var intro = Text(T("Extensions stay as they are. Nothing changes until you press Rename, and it can be undone.", "Расширения не меняются. Ничего не произойдёт, пока вы не нажмёте «Переименовать», и это можно отменить."), 13, brush: Theme.Muted);
        intro.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(intro);

        root.Children.Add(FieldLabel(T("New name", "Новое имя")));
        root.Children.Add(_template);
        var tokens = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (token, tip) in new[]
        {
            ("{name}", T("current name", "текущее имя")),
            ("{n:00}", T("number 01, 02…", "номер 01, 02…")),
            ("{date}", T("date modified", "дата изменения")),
            ("{taken}", T("date taken (photos)", "дата съёмки (фото)")),
            ("{parent}", T("folder name", "имя папки")),
        })
        {
            var chip = Button(token, () => Insert(token));
            chip.ToolTip = tip;
            chip.Padding = new Thickness(9, 3, 9, 4);
            chip.Margin = new Thickness(0, 0, 6, 6);
            tokens.Children.Add(chip);
        }
        root.Children.Add(tokens);
        root.Children.Add(Hint(T("Formats: {n:000} → 001, {date:dd.MM.yyyy}, {taken:yyyy-MM-dd HH-mm}.", "Форматы: {n:000} → 001, {date:dd.MM.yyyy}, {taken:yyyy-MM-dd HH-mm}.")));

        root.Children.Add(FieldLabel(T("Find", "Найти")));
        root.Children.Add(_find);
        root.Children.Add(FieldLabel(T("Replace with", "Заменить на"), 10));
        root.Children.Add(_replace);
        root.Children.Add(OptionRow(T("Regular expression", "Регулярное выражение"), _options.UseRegex, v => Change(o => o with { UseRegex = v })));
        root.Children.Add(OptionRow(T("Match case", "С учётом регистра"), _options.MatchCase, v => Change(o => o with { MatchCase = v })));

        root.Children.Add(FieldLabel(T("Letter case", "Регистр букв")));
        root.Children.Add(AlignLeft(Segmented(
            [(T("Keep", "Как есть"), RenameCase.Keep), (T("lower", "строчные"), RenameCase.Lower), (T("UPPER", "ПРОПИСНЫЕ"), RenameCase.Upper), (T("Title", "Заглавные"), RenameCase.Title)],
            _options.Case, v => Change(o => o with { Case = v }))));
        root.Children.Add(OptionRow(T("Transliterate (Cyrillic → Latin)", "Транслитерация (кириллица → латиница)"), _options.Transliterate, v => Change(o => o with { Transliterate = v })));
        root.Children.Add(OptionRow(T("Spaces → _", "Пробелы → _"), _options.SpacesToUnderscores, v => Change(o => o with { SpacesToUnderscores = v })));

        root.Children.Add(FieldLabel(T("Numbering order", "Порядок нумерации")));
        root.Children.Add(AlignLeft(Segmented(
            [(T("Name", "Имя"), RenameOrder.Name), (T("Modified", "Изменён"), RenameOrder.Modified), (T("Taken", "Снят"), RenameOrder.Taken), (T("Selection", "Как выбрано"), RenameOrder.Selection)],
            _options.Order, v => Change(o => o with { Order = v }))));
        var numbers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        numbers.Children.Add(Labeled(T("Start", "Начать с"), _start));
        numbers.Children.Add(Labeled(T("Step", "Шаг"), _step));
        root.Children.Add(numbers);
        return root;
    }

    private FrameworkElement PreviewPanel()
    {
        var panel = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 6, 0, 10) };
        var label = Text(T("Preview", "Предпросмотр"), 15, FontWeights.SemiBold);
        label.FontFamily = Theme.DisplayFont;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(_summary, Dock.Right);
        head.Children.Add(_summary);
        head.Children.Add(label);
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);

        var cancel = Button(T("Cancel", "Отмена"), () => DialogResult = false);
        cancel.Margin = new Thickness(8, 0, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_apply);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);

        panel.Children.Add(new Border
        {
            Background = Theme.Card,
            BorderBrush = Theme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(4),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _rows },
        });
        return panel;
    }

    private void Refresh()
    {
        _refresh.Stop();
        _plan = BatchRename.Plan(_paths, _options, TakenAt);
        _rows.Children.Clear();
        foreach (var entry in _plan.Take(PreviewRows))
            _rows.Children.Add(Row(entry));
        if (_plan.Count > PreviewRows)
        {
            var more = Text(T($"…and {_plan.Count - PreviewRows} more", $"…и ещё {_plan.Count - PreviewRows}"), 12, brush: Theme.Muted);
            more.Margin = new Thickness(12, 8, 12, 8);
            _rows.Children.Add(more);
        }

        var changed = _plan.Count(e => e.Changed);
        var errors = _plan.Count(e => e.Error is not null);
        _summary.Text = errors > 0
            ? T($"Changes: {changed} · problems: {errors}", $"Изменится: {changed} · проблем: {errors}")
            : T($"Changes: {changed} of {_plan.Count}", $"Изменится: {changed} из {_plan.Count}");
        _summary.Foreground = errors > 0 ? Theme.Error : Theme.Muted;
        var ready = errors == 0 && changed > 0;
        _apply.Opacity = ready ? 1 : 0.45;
        _apply.IsHitTestVisible = ready;
    }

    private DateTime? TakenAt(string path)
    {
        if (!_taken.TryGetValue(path, out var taken))
            _taken[path] = taken = ImageBackend.TakenAt(path);
        return taken;
    }

    private static Border Row(RenameEntry entry)
    {
        var bad = entry.Error is not null;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var oldName = Text(entry.OldName, 13, brush: Theme.Muted, wrap: false);
        oldName.ToolTip = entry.Source;
        grid.Children.Add(oldName);
        var arrow = new TextBlock { Text = "", FontFamily = Theme.Icons, FontSize = 11, Foreground = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);
        var newName = new StackPanel();
        newName.Children.Add(Text(entry.NewName, 13, entry.Changed ? FontWeights.SemiBold : FontWeights.Normal, bad ? Theme.Error : entry.Changed ? Theme.Text : Theme.Muted, wrap: false));
        if (bad)
            newName.Children.Add(Text(entry.Error!, 11.5, brush: Theme.Error));
        Grid.SetColumn(newName, 2);
        grid.Children.Add(newName);
        return new Border
        {
            Padding = new Thickness(12, 7, 12, 7),
            CornerRadius = new CornerRadius(6),
            Background = bad ? new SolidColorBrush(Color.FromArgb(0x1A, 0xF0, 0x5A, 0x5A)) : Brushes.Transparent,
            Child = grid,
        };
    }

    private void Accept()
    {
        Refresh();
        if (_plan.Any(e => e.Error is not null) || !_plan.Any(e => e.Changed))
            return;
        DialogResult = true;
    }

    private void Change(Func<RenameOptions, RenameOptions> update)
    {
        _options = update(_options);
        _refresh.Stop();
        _refresh.Start();
    }

    private void Insert(string token)
    {
        var caret = _template.CaretIndex;
        _template.Text = _template.Text.Insert(caret, token);
        _template.CaretIndex = caret + token.Length;
        Keyboard.Focus(_template);
    }

    private TextBox Field(string text, Func<RenameOptions, string, RenameOptions> update)
    {
        var box = TextField(text, 260);
        box.TextChanged += (_, _) => Change(o => update(o, box.Text));
        return box;
    }

    private TextBox Number(int value, Func<RenameOptions, int, RenameOptions> update)
    {
        var box = TextField(value.ToString(CultureInfo.InvariantCulture), 6);
        box.Width = 90;
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                Change(o => update(o, v));
        };
        return box;
    }

    private static FrameworkElement Labeled(string label, FrameworkElement field)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        panel.Children.Add(FieldLabel(label, 0));
        panel.Children.Add(field);
        return panel;
    }

    private static FrameworkElement OptionRow(string label, bool value, Action<bool> changed)
    {
        var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var toggle = Toggle(value, changed);
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        var text = Text(label, 14);
        text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text);
        return row;
    }

    private static FrameworkElement AlignLeft(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        return element;
    }

    private static TextBlock Hint(string text)
    {
        var hint = Text(text, 12, brush: Theme.Muted);
        hint.Margin = new Thickness(0, 4, 0, 0);
        return hint;
    }
}
