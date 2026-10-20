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
        heading.Children.Add(Text($"{Brand.Name} {Shell.Updater.Current.ToString(2)}", 30, FontWeights.SemiBold));
        heading.Children.Add(Text(T("A new shape for your files.", "Новая форма ваших файлов."), 14, brush: Theme.Muted));
        header.Children.Add(heading); root.Children.Add(header);
        root.Children.Add(Card(T("Author", "Автор"), Brand.Author, null, glyph: ""));
        root.Children.Add(SectionHeader(T("Open source", "Открытый код")));
        root.Children.Add(Card(T("Based on Sweetch", "Основано на Sweetch"),
            T("SaymonTools is built on the MIT-licensed Sweetch project. Its copyright notices are kept in LICENSE.", "SaymonTools сделан на основе проекта Sweetch под лицензией MIT. Уведомления об авторских правах сохранены в LICENSE."),
            Button("GitHub", () => Shell.Updater.Open(Brand.Upstream), glyph: ""), glyph: ""));
        root.Children.Add(SectionHeader(T("Updates", "Обновления")));
        root.Children.Add(Card($"{Brand.Name} {Shell.Updater.Current.ToString(2)}", T("Automatic updates are disabled.", "Автообновления отключены."), null, glyph: ""));
        var footer = Text(T("Local processing. Your files stay on your computer.", "Локальная обработка. Ваши файлы остаются на компьютере."), 12, brush: Theme.Muted);
        footer.Margin = new Thickness(2, 24, 0, 0); root.Children.Add(footer);
    }
}
