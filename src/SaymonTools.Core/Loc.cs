using System.Globalization;
namespace SaymonTools.Core;
public enum Language { English, Russian }
public static class Loc
{
    public static Language Current { get; set; } = Detect();
    public static Language Detect() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? Language.Russian : Language.English;
    public static string T(string english, string russian) => Current == Language.Russian ? russian : english;
    public static string Items(int n) => Current == Language.Russian
        ? $"{n} " + (n % 100 is >= 11 and <= 14 ? "файлов" : (n % 10) switch { 1 => "файл", >= 2 and <= 4 => "файла", _ => "файлов" })
        : n == 1 ? "1 item" : $"{n} items";
}
