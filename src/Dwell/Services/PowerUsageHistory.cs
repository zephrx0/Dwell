using System.IO;
using System.Text.Json;
using Dwell.Core;
using Microsoft.Data.Sqlite;

namespace Dwell.Services;

internal sealed record PowerReading(long At, PowerTotal Total, Dictionary<string, double> Watts,
    bool CpuGpuOnly = false)
{
    public PowerReading ForCpuAndGpu(IReadOnlySet<string> currentIds, IReadOnlyDictionary<string, string> labels)
    {
        if (CpuGpuOnly) return this;

        var watts = Watts.Where(x => IsCpuOrGpu(x.Key, currentIds, labels)).ToDictionary(StringComparer.Ordinal);
        var included = Total.SensorIds.Where(watts.ContainsKey).ToArray();
        var total = included.Length == 0 ? PowerTotal.Unavailable :
            new PowerTotal(PowerSource.Components, included.Sum(id => watts[id]), included, Total.IsCpuPartial);
        return new(At, total, watts, CpuGpuOnly: true);
    }

    private static bool IsCpuOrGpu(string id, IReadOnlySet<string> currentIds, IReadOnlyDictionary<string, string> labels)
    {
        if (currentIds.Contains(id)) return true;
        var label = labels.GetValueOrDefault(id);
        var sensor = label is null ? "" : label[(label.LastIndexOf('/') + 1)..].Trim();
        if (sensor is "CPU Memory" or "CPU Platform") return false;
        if (id.StartsWith("/gpu-", StringComparison.Ordinal) || id.StartsWith("/intelcpu/", StringComparison.Ordinal) ||
            id.StartsWith("/amdcpu/", StringComparison.Ordinal) || id.StartsWith("/cpu/", StringComparison.Ordinal)) return true;
        if (!id.StartsWith("emi:", StringComparison.Ordinal)) return false;
        return sensor is "CPU Package" or "CPU Cores" or "CPU Graphics" ||
            sensor.StartsWith("Core", StringComparison.Ordinal) && sensor.EndsWith(" (Windows)", StringComparison.Ordinal);
    }
}
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
            {
                while (reader.Read())
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<PowerReading>(reader.GetString(0)) is { } point) previous.Add(point);
                    }
                    catch (JsonException ex) { System.Diagnostics.Trace.WriteLine(ex); }
                }
            }
            command.Parameters.Clear();
            command.CommandText = "SELECT id, label FROM sensor_labels";
            using var labelReader = command.ExecuteReader();
            while (labelReader.Read()) savedLabels[labelReader.GetString(0)] = labelReader.GetString(1);
        }
        transaction.Commit();
        return new(previous, savedLabels);
    }
}
