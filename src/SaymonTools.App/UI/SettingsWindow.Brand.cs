using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using static SaymonTools.App.UI.Controls;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.UI;

internal sealed partial class SettingsWindow
{
    private void AboutPage(Panel root)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new Image {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/SaymonTools.png")),
            Width = 72, Height = 72, Margin = new Thickness(0, 0, 20, 0)
        });
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(Text(Brand.Name, 30, FontWeights.SemiBold));
        heading.Children.Add(Text(T("Files in one gesture.", "Файлы — в один жест."), 14, brush: Theme.Muted));
        header.Children.Add(heading); root.Children.Add(header);
        root.Children.Add(Card(T("Author", "Автор"), Brand.Author, null, glyph: "\uE77B"));
        root.Children.Add(SectionHeader(T("Open source", "Открытый код")));
        root.Children.Add(Card(T("Based on Sweetch", "Основано на Sweetch"),
            T("SaymonTools is built on the MIT-licensed Sweetch project. Its copyright notices are kept in LICENSE.", "SaymonTools сделан на основе проекта Sweetch под лицензией MIT. Уведомления об авторских правах сохранены в LICENSE."),
            Button("Sweetch", () => Shell.Updater.Open(Brand.Upstream), glyph: "\uE8A7"), glyph: "\uE943"));
        root.Children.Add(SectionHeader(T("Updates", "Обновления")));
        UpdatesSection(root);
        var footer = Text(T("Local processing. Your files stay on your computer.", "Локальная обработка. Ваши файлы остаются на компьютере."), 12, brush: Theme.Muted);
        footer.Margin = new Thickness(2, 24, 0, 0); root.Children.Add(footer);
    }

    /// <summary>Automatic checks, a "Check now" button and what the last check found (rebuilt when the updater changes).</summary>
    private void UpdatesSection(Panel root)
    {
        root.Children.Add(Card(T("Check for updates automatically", "Проверять обновления автоматически"),
            T($"New versions come from GitHub ({Brand.Repository}).", $"Новые версии берутся с GitHub ({Brand.Repository})."),
            Toggle(_settings.CheckForUpdates, v =>
            {
                _settings.CheckForUpdates = v;
                Save();
            }), glyph: "\uE895"));

        var updater = Updater;
        string status;
        UIElement? action = null;
        if (updater is null)
        {
            status = "";
        }
        else if (updater.Installing)
        {
            status = T("Downloading the update…", "Загрузка обновления…");
        }
        else if (updater.Available is { } update)
        {
            status = Shell.Updater.IsInstalled
                ? T($"Version {update.Version} is available. Windows will ask for permission to install it.", $"Доступна версия {update.Version}. Windows попросит разрешение на установку.")
                : T($"Version {update.Version} is available. This copy isn't installed, so the download page opens.", $"Доступна версия {update.Version}. Эта копия не установлена, поэтому откроется страница загрузки.");
            action = Button(Shell.Updater.IsInstalled ? T("Install", "Установить") : T("Download", "Скачать"), () => _ = updater.InstallAsync(update), primary: true, glyph: "\uE896");
        }
        else
        {
            status = updater.LastError is { } error
                ? T("Couldn't check: ", "Не удалось проверить: ") + error
                : updater.LastChecked is { } at
                    ? T($"You have the latest version (checked {at:HH:mm}).", $"Установлена последняя версия (проверено в {at:HH:mm}).")
                    : T("Not checked yet.", "Ещё не проверялось.");
            action = Button(T("Check now", "Проверить сейчас"), () => _ = updater.CheckAsync(), glyph: "\uE72C");
        }
        root.Children.Add(Card(T($"Version {Shell.Updater.Current.ToString(3)}", $"Версия {Shell.Updater.Current.ToString(3)}"), status, action, glyph: "\uE946"));
    }
}
