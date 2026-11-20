using Dwell.Core;
using Microsoft.Data.Sqlite;

namespace Dwell.Services;

public sealed class AppRecord
{
    public long Id { get; init; }
    public required string Exe { get; init; }
    public required string Name { get; init; }
    public string Icon { get; set; } = "";
    public AppCategory Category { get; set; }
    private volatile bool _ignored;
    public bool Ignored { get => _ignored; set => _ignored = value; }
}

public sealed record UsageResult(IReadOnlyList<AppRecord> Apps, IReadOnlyList<Session> Sessions);
public sealed record ResourcePeak(double Cpu, long Memory);

public sealed class AppRepository : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _sync = new();
    private readonly Dictionary<string, AppRecord> _apps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, AppCategory> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["telegram.exe"] = AppCategory.Communication, ["discord.exe"] = AppCategory.Communication,
        ["ms-teams.exe"] = AppCategory.Communication, ["slack.exe"] = AppCategory.Communication,
        ["outlook.exe"] = AppCategory.Communication, ["code.exe"] = AppCategory.Productivity,
        ["devenv.exe"] = AppCategory.Productivity, ["winword.exe"] = AppCategory.Productivity,
        ["excel.exe"] = AppCategory.Productivity, ["notepad.exe"] = AppCategory.Productivity,
        ["obsidian.exe"] = AppCategory.Productivity, ["spotify.exe"] = AppCategory.Entertainment,
        ["vlc.exe"] = AppCategory.Entertainment, ["steam.exe"] = AppCategory.Games,
        ["epicgameslauncher.exe"] = AppCategory.Games, ["minecraft.exe"] = AppCategory.Games
    };

    public AppRepository()
    {
        Directory.CreateDirectory(SettingsService.DataDirectory);
        _connection = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(SettingsService.DataDirectory, "dwell.db"), Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS Apps (
              id INTEGER PRIMARY KEY, exe TEXT NOT NULL UNIQUE COLLATE NOCASE,
              name TEXT NOT NULL, icon TEXT NOT NULL DEFAULT '', category INTEGER NOT NULL DEFAULT 4,
              ignored INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Sessions (
              id TEXT PRIMARY KEY, app_id INTEGER NOT NULL REFERENCES Apps(id),
              start INTEGER NOT NULL, end INTEGER NOT NULL, CHECK(end >= start));
            CREATE INDEX IF NOT EXISTS IX_Sessions_End_Start ON Sessions(end, start);
            CREATE TABLE IF NOT EXISTS BackgroundSessions (
              id TEXT PRIMARY KEY, app_id INTEGER NOT NULL REFERENCES Apps(id),
              start INTEGER NOT NULL, end INTEGER NOT NULL, CHECK(end >= start));
            CREATE INDEX IF NOT EXISTS IX_BackgroundSessions_End_Start ON BackgroundSessions(end, start);
            CREATE TABLE IF NOT EXISTS ResourcePeaks (
              app_id INTEGER PRIMARY KEY REFERENCES Apps(id),
              cpu REAL NOT NULL DEFAULT 0, memory INTEGER NOT NULL DEFAULT 0);
            PRAGMA user_version=3;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT id,exe,name,icon,category,ignored FROM Apps";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var app = new AppRecord { Id = reader.GetInt64(0), Exe = reader.GetString(1), Name = reader.GetString(2), Icon = reader.GetString(3), Category = (AppCategory)reader.GetInt32(4), Ignored = reader.GetBoolean(5) };
            _apps[app.Exe] = app;
        }
    }

    public AppRecord[] KnownApps() { lock (_sync) return _apps.Values.ToArray(); }

    public AppRecord? Find(string path) { lock (_sync) return _apps.GetValueOrDefault(path); }

    public void RecordResourcePeaks(IEnumerable<(long AppId, double Cpu, long Memory)> values)
    {
        var peaks = values.Where(x => x.Cpu >= 0 && x.Memory >= 0).ToArray();
        if (peaks.Length == 0) return;
        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ResourcePeaks(app_id,cpu,memory) VALUES($id,$cpu,$memory)
                ON CONFLICT(app_id) DO UPDATE SET cpu=MAX(cpu,excluded.cpu), memory=MAX(memory,excluded.memory)
                WHERE excluded.cpu > cpu OR excluded.memory > memory
                """;
            var id = command.Parameters.Add("$id", SqliteType.Integer);
            var cpu = command.Parameters.Add("$cpu", SqliteType.Real);
            var memory = command.Parameters.Add("$memory", SqliteType.Integer);
            command.Prepare();
            foreach (var peak in peaks) { id.Value = peak.AppId; cpu.Value = peak.Cpu; memory.Value = peak.Memory; command.ExecuteNonQuery(); }
            transaction.Commit();
        }
    }

    public IReadOnlyDictionary<long, ResourcePeak> ResourcePeaks()
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT app_id,cpu,memory FROM ResourcePeaks";
            using var reader = command.ExecuteReader();
            var result = new Dictionary<long, ResourcePeak>();
            while (reader.Read()) result[reader.GetInt64(0)] = new(reader.GetDouble(1), reader.GetInt64(2));
            return result;
        }
    }

    public AppRecord Add(string path, string name, string icon)
    {
        lock (_sync)
        {
            if (_apps.TryGetValue(path, out var known)) return known;
            var identity = ApplicationPolicy.Identity(path, name);
            var variants = _apps.Values.Where(x => ApplicationPolicy.Identity(x.Exe, x.Name) == identity).ToArray();
            var category = variants.FirstOrDefault()?.Category ?? Categories.GetValueOrDefault(Path.GetFileName(path), AppCategory.Other);
            var ignored = variants.Any(x => x.Ignored);
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO Apps(exe,name,icon,category,ignored) VALUES($exe,$name,$icon,$category,$ignored) RETURNING id";
            command.Parameters.AddWithValue("$exe", path);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$icon", icon);
            command.Parameters.AddWithValue("$category", (int)category);
            command.Parameters.AddWithValue("$ignored", ignored);
            var app = new AppRecord { Id = (long)command.ExecuteScalar()!, Exe = path, Name = name, Icon = icon, Category = category, Ignored = ignored };
            _apps[path] = app;
            return app;
        }
    }

    public void SetIgnored(string key, bool ignored)
    {
        lock (_sync)
        {
            var variants = _apps.Values.Where(x => ApplicationPolicy.Identity(x.Exe, x.Name) == key && x.Ignored != ignored).ToArray();
            if (variants.Length == 0) return;
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Apps SET ignored=$ignored WHERE id=$id";
            command.Parameters.AddWithValue("$ignored", ignored);
            var id = command.Parameters.Add("$id", SqliteType.Integer);
            foreach (var app in variants) { id.Value = app.Id; command.ExecuteNonQuery(); }
            transaction.Commit();
            foreach (var app in variants) app.Ignored = ignored;
        }
    }

    public void UpdateCategory(IReadOnlyList<long> ids, AppCategory category)
    {
        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE Apps SET category=$category WHERE id=$id";
            command.Parameters.AddWithValue("$category", (int)category);
            var parameter = command.Parameters.Add("$id", SqliteType.Integer);
            foreach (var id in ids) { parameter.Value = id; command.ExecuteNonQuery(); }
            transaction.Commit();
            foreach (var app in _apps.Values.Where(x => ids.Contains(x.Id))) app.Category = category;
        }
    }

    public void WriteBatch(IReadOnlyList<Session> sessions, bool background = false)
    {
        if (sessions.Count == 0) return;
        lock (_sync)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {(background ? "BackgroundSessions" : "Sessions")}(id,app_id,start,end) VALUES($id,$app,$start,$end) ON CONFLICT(id) DO UPDATE SET end=excluded.end";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var app = command.Parameters.Add("$app", SqliteType.Integer);
            var start = command.Parameters.Add("$start", SqliteType.Integer);
            var end = command.Parameters.Add("$end", SqliteType.Integer);
            command.Prepare();
            foreach (var session in sessions)
            {
                id.Value = session.Id.ToString("N"); app.Value = session.AppId;
                start.Value = session.Start.ToUnixTimeMilliseconds(); end.Value = session.End.ToUnixTimeMilliseconds();
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public UsageResult Read(DateTimeOffset start, DateTimeOffset end, IReadOnlyList<Session> pending, bool background = false)
    {
        lock (_sync)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT id,app_id,start,end FROM {(background ? "BackgroundSessions" : "Sessions")} WHERE end>$start AND start<$end";
            command.Parameters.AddWithValue("$start", start.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$end", end.ToUnixTimeMilliseconds());
            var sessions = new Dictionary<Guid, Session>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var session = new Session(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
                sessions[session.Id] = session;
            }
            foreach (var session in pending.Where(x => x.End > start && x.Start < end))
                if (!sessions.TryGetValue(session.Id, out var persisted) || session.End > persisted.End) sessions[session.Id] = session;
            // Copies prevent the UI from seeing a category changed halfway through aggregation.
            var apps = _apps.Values.Select(x => new AppRecord { Id = x.Id, Exe = x.Exe, Name = x.Name, Icon = x.Icon, Category = x.Category, Ignored = x.Ignored }).ToArray();
            return new(apps, sessions.Values.ToArray());
        }
    }

    public void Dispose() => _connection.Dispose();
}
