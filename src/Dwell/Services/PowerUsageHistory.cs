using System.IO;
using System.Text.Json;
using Dwell.Core;
using Microsoft.Data.Sqlite;

namespace Dwell.Services;

internal sealed record PowerReading(long At, PowerTotal Total, Dictionary<string, double> Watts,
    Dictionary<string, double>? DiskActivity = null);
internal sealed record SavedPowerHistory(List<PowerReading> Readings, Dictionary<string, string> Labels);

internal static class PowerUsageHistory
{
    public const long WindowMilliseconds = 86_400_000;

    public static SavedPowerHistory Save(PowerReading reading, Dictionary<string, string> labels, bool load)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(SettingsService.DataDirectory, "power.db") }.ToString());
        db.Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS samples(time INTEGER PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sensor_labels(id TEXT PRIMARY KEY, label TEXT NOT NULL);
            DELETE FROM samples WHERE time < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", reading.At - WindowMilliseconds);
        command.ExecuteNonQuery();
        command.Parameters.Clear();

        command.CommandText = "INSERT OR REPLACE INTO samples VALUES ($time, $data)";
        command.Parameters.AddWithValue("$time", reading.At);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(reading));
        command.ExecuteNonQuery();
        command.Parameters.Clear();

        command.CommandText = "INSERT OR REPLACE INTO sensor_labels VALUES ($id, $label)";
        var idParameter = command.Parameters.Add("$id", SqliteType.Text);
        var labelParameter = command.Parameters.Add("$label", SqliteType.Text);
        foreach (var (id, label) in labels)
        {
            idParameter.Value = id;
            labelParameter.Value = label;
            command.ExecuteNonQuery();
        }
        command.Parameters.Clear();

        var previous = new List<PowerReading>();
        var savedLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (load)
        {
            command.CommandText = "SELECT data FROM samples WHERE time < $now ORDER BY time";
            command.Parameters.AddWithValue("$now", reading.At);
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (JsonSerializer.Deserialize<PowerReading>(reader.GetString(0)) is { } point) previous.Add(point);
            command.Parameters.Clear();
            command.CommandText = "SELECT id, label FROM sensor_labels";
            using var labelReader = command.ExecuteReader();
            while (labelReader.Read()) savedLabels[labelReader.GetString(0)] = labelReader.GetString(1);
        }
        transaction.Commit();
        return new(previous, savedLabels);
    }
}
