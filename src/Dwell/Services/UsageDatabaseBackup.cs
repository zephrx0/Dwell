using Microsoft.Data.Sqlite;

namespace Dwell.Services;

internal sealed record DatabaseRecovery(string Folder, bool RestoredBackup);

internal static class UsageDatabaseBackup
{
    private static string BackupPath(string path) => path + ".backup";

    public static bool HasBackup(string path) => File.Exists(BackupPath(path));

    public static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = mode }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    public static DatabaseRecovery Recover(string path)
    {
        var folder = Path.Combine(Path.GetDirectoryName(path)!, $"dwell-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var original = path + suffix;
            if (File.Exists(original)) File.Move(original, Path.Combine(folder, Path.GetFileName(original)));
        }
        var backup = BackupPath(path);
        if (!File.Exists(backup)) return new(folder, false);
        try
        {
            using var db = Open(backup, SqliteOpenMode.ReadOnly);
            Check(db);
            var temporary = Path.Combine(folder, "restored.db");
            File.Copy(backup, temporary);
            File.Move(temporary, path);
            return new(folder, true);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            System.Diagnostics.Trace.WriteLine(ex);
            return new(folder, false);
        }
    }

    public static void SaveIfDue(SqliteConnection source)
    {
        var backup = BackupPath(source.DataSource);
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(backup) < TimeSpan.FromDays(1)) return;
        var temporary = backup + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (var copy = Open(temporary))
            {
                source.BackupDatabase(copy);
                using var command = copy.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE";
                command.ExecuteScalar();
                Check(copy);
            }
            File.Move(temporary, backup, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void Check(SqliteConnection database)
    {
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (command.ExecuteScalar() is not "ok") throw new SqliteException("SQLite integrity check failed.", 11);
    }
}
