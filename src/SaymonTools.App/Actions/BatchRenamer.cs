using System.IO;
using SaymonTools.App.Jobs;
using SaymonTools.App.Shell;
using SaymonTools.App.UI;
using SaymonTools.Core;
using SaymonTools.Core.Rename;
using static SaymonTools.Core.Loc;

namespace SaymonTools.App.Actions;

/// <summary>The "Rename" action: dialog with preview, the rename itself and undo of the last one.</summary>
internal static class BatchRenamer
{
    /// <summary>The last rename, kept on disk so it can be undone later from the tray menu too.</summary>
    private static string JournalPath => Path.Combine(AppSettings.Folder, "last-rename.json");

    public static bool CanUndo => File.Exists(JournalPath);

    public static Task RenameAsync(IReadOnlyList<FileItem> items, AppSettings settings, Action save, JobRunner? jobs)
    {
        // A background app's dialog opens behind Explorer without a foreground owner.
        var owner = ForegroundHelper.Host();
        RenameDialog dialog;
        try
        {
            dialog = new RenameDialog(items, settings.LastRename) { Owner = owner };
            if (dialog.ShowDialog() != true)
                return Task.CompletedTask;
        }
        finally
        {
            owner.Close();
        }
        settings.LastRename = dialog.Options;
        save();

        RenameJournal journal;
        try
        {
            journal = BatchRename.Apply(dialog.Plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConversionException)
        {
            Notifications.Show(T("Rename failed", "Не удалось переименовать"), ex.Message + Environment.NewLine + T("Nothing was changed.", "Ничего не изменено."), error: true);
            return Task.CompletedTask;
        }
        Save(journal);

        var count = journal.Moves.Count;
        var status = count == 1
            ? $"{Path.GetFileName(journal.Moves[0].From)} → {Path.GetFileName(journal.Moves[0].To)}"
            : T($"Renamed {count} files", $"Переименовано файлов: {count}");
        if (jobs is null)
            Notifications.Show(T("Renamed", "Переименовано"), status);
        else
            jobs.ShowDone(T("Rename", "Переименование"), Items(count), journal.Moves.FirstOrDefault()?.To, status, T("Undo", "Отменить"), Glyphs.Undo, UndoLast);
        return Task.CompletedTask;
    }

    /// <summary>Gives the files of the last rename their old names back.</summary>
    public static void UndoLast()
    {
        var journal = File.Exists(JournalPath) ? RenameJournal.FromJson(File.ReadAllText(JournalPath)) : null;
        if (journal is null || journal.Moves.Count == 0)
        {
            Notifications.Show(T("Nothing to undo", "Нечего отменять"), T("There is no recent rename.", "Недавних переименований нет."));
            return;
        }
        try
        {
            BatchRename.Undo(journal);
            OutputPaths.TryDelete(JournalPath);
            Notifications.Show(T("Rename undone", "Переименование отменено"), Items(journal.Moves.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConversionException)
        {
            Notifications.Show(T("Couldn't undo the rename", "Не удалось отменить переименование"), ex.Message, error: true);
        }
    }

    private static void Save(RenameJournal journal)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(JournalPath, journal.ToJson());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Undo reads the journal from disk, so without it there is nothing to undo with.
            Notifications.Show(T("Renamed, but undo is unavailable", "Переименовано, но отмена недоступна"), ex.Message, error: true);
        }
    }
}
