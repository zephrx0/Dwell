using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dwell.Services;

public sealed record UpdateManifest(string Product, string Version, string Runtime, Dictionary<string, string> Files);
public sealed record PreparedUpdate(string Directory, string Version);

public sealed class UpdateService
{
    private static readonly string UpdateRoot = Path.Combine(SettingsService.DataDirectory, "updates");
    private static readonly string[] RequiredFiles = ["Dwell.exe", "Dwell.dll", "Dwell.deps.json", "Dwell.runtimeconfig.json", "Dwell.pri", "App.xbf", "MainWindow.xbf", "Views/UsageChart.xbf", "Assets/Dwell.ico", "Update.ps1"];

    private static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version!;
    private static string InstallDirectory => Path.GetDirectoryName(Environment.ProcessPath)!;

    public Task<PreparedUpdate> PrepareAsync(string path) => Task.Run(() =>
    {
        var probe = Path.Combine(InstallDirectory, ".dwell-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        var directory = Path.Combine(UpdateRoot, Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(directory, "stage");
        Directory.CreateDirectory(stage);
        try
        {
            var (version, files) = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? StageExecutable(path, stage)
                : StageArchive(path, stage);
            File.WriteAllText(Path.Combine(directory, "request.json"), JsonSerializer.Serialize(new
            {
                ParentId = Environment.ProcessId, Target = InstallDirectory, Stage = stage,
                Files = files, FailureText = Strings.Get("UpdateFailedRollback")
            }));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Update.ps1"), Path.Combine(directory, "apply.ps1"));
            return new PreparedUpdate(directory, version);
        }
        catch { Directory.Delete(directory, true); throw; }
    });

    private static (string Version, string[] Files) StageExecutable(string path, string stage)
    {
        const string executable = "Dwell.exe";
        var target = Path.Combine(stage, executable);
        File.Copy(path, target);
        var info = FileVersionInfo.GetVersionInfo(target);
        if (info.ProductName != "Dwell" || info.FileDescription != "Dwell Portable" ||
            !Version.TryParse(info.FileVersion, out var version) || version <= CurrentVersion)
            throw new InvalidDataException();
        return (version.ToString(), [executable]);
    }

    private static (string Version, string[] Files) StageArchive(string zipPath, string stage)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = archive.GetEntry("dwell-update.json") ?? throw new InvalidDataException();
        if (manifestEntry.Length > 1024 * 1024 || archive.Entries.Count > 10000 || archive.Entries.Sum(x => x.Length) > 1024L * 1024 * 1024)
            throw new InvalidDataException();
        using var stream = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(stream) ?? throw new InvalidDataException();
        if (manifest.Product != "Dwell" || manifest.Runtime != "win-x64" || manifest.Files is null ||
            !Version.TryParse(manifest.Version, out var version) || version <= CurrentVersion ||
            RequiredFiles.Any(file => !manifest.Files.ContainsKey(file))) throw new InvalidDataException();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name == "dwell-update.json") continue;
            if (!manifest.Files.TryGetValue(name, out var hash) || !seen.Add(name)) throw new InvalidDataException();
            var destination = SafePath(stage, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination);
            using var file = File.OpenRead(destination);
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        }
        if (seen.Count != manifest.Files.Count) throw new InvalidDataException();
        var assembly = AssemblyName.GetAssemblyName(Path.Combine(stage, "Dwell.dll"));
        if (assembly.Name != "Dwell" || assembly.Version != version) throw new InvalidDataException();
        return (manifest.Version, seen.ToArray());
    }

    private static string SafePath(string root, string entry)
    {
        if (string.IsNullOrEmpty(entry) || entry.Contains(':') || entry.Contains('\\') || entry.Split('/').Any(x => x is ".." or "." or "")) throw new InvalidDataException();
        var resolved = Path.GetFullPath(Path.Combine(root, entry));
        if (!resolved.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        return resolved;
    }

    public void Launch(PreparedUpdate update)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", Path.Combine(update.Directory, "apply.ps1"), "-Request", Path.Combine(update.Directory, "request.json") }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException();
    }

    public void Discard(PreparedUpdate update)
    {
        var resolved = Path.GetFullPath(update.Directory);
        if (resolved.StartsWith(Path.GetFullPath(UpdateRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
    }
}
