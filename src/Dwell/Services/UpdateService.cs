using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Dwell.Services;

internal sealed record AvailableUpdate(Version Version, Uri DownloadUri, long Size, string Sha256);

public sealed class UpdateService
{
    private const string LatestRelease = "https://api.github.com/repos/zephrx0/Dwell/releases/latest";
    private const string InstallerName = "Dwell-Setup.exe";
    private const long MaxInstallerSize = 256L * 1024 * 1024;
    private static readonly HttpClient Client = new() { DefaultRequestHeaders = { UserAgent = { new("Dwell", null) } } };
    private readonly HttpClient _client;
    private readonly Version _currentVersion;
    private readonly string _directory;

    public UpdateService() : this(Client, typeof(UpdateService).Assembly.GetName().Version!,
        Path.Combine(SettingsService.DataDirectory, "updates")) { }

    internal UpdateService(HttpClient client, Version currentVersion, string directory)
    {
        _client = client;
        _currentVersion = currentVersion;
        _directory = directory;
    }

    internal async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using var check = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        check.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _client.GetAsync(LatestRelease, check.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: check.Token).ConfigureAwait(false);
        if (release is null || release.Draft || release.Prerelease ||
            release.Tag is not { } tag || !tag.StartsWith('v') ||
            !Version.TryParse(tag.AsSpan(1), out var version) || version.Build < 0 || version.Revision >= 0 ||
            tag != "v" + version) return null;

        var fileVersion = new Version(version.Major, version.Minor, version.Build, 0);
        if (fileVersion <= _currentVersion) return null;
        var asset = release.Assets?.FirstOrDefault(x => x.Name == InstallerName && x.State == "uploaded");
        if (asset is null) return null;
        if (asset.Size is <= 0 or > MaxInstallerSize ||
            asset.Digest is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !digest[7..].All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid installer metadata.");

        var uri = new Uri($"https://github.com/zephrx0/Dwell/releases/download/{tag}/{InstallerName}");
        return new(fileVersion, uri, asset.Size, digest[7..]);
    }

    internal async Task<string> DownloadAsync(AvailableUpdate update, CancellationToken cancellationToken)
    {
        using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        download.CancelAfter(TimeSpan.FromMinutes(15));
        var token = download.Token;
        Directory.CreateDirectory(_directory);
        var target = Path.Combine(_directory, InstallerName);
        var temporary = target + ".download";
        try
        {
            using var response = await _client.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                var remaining = update.Size;
                while (true)
                {
                    var count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    remaining -= count;
                    if (remaining < 0) throw new InvalidDataException("The installer is larger than expected.");
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (remaining != 0 || !Convert.ToHexString(hash.GetHashAndReset()).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The installer size or checksum does not match the release.");
            }
            var installer = FileVersionInfo.GetVersionInfo(temporary);
            if (installer.ProductName?.TrimEnd() != "Dwell" || !Version.TryParse(installer.FileVersion, out var version) || version != update.Version)
                throw new InvalidDataException("The installer version does not match the release.");
            UpdateSignature.Check(temporary);

            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void Launch(string installer)
    {
        var start = new ProcessStartInfo { FileName = installer, UseShellExecute = false };
        start.ArgumentList.Add("/SILENT");
        start.ArgumentList.Add("/NORESTART");
        start.ArgumentList.Add($"/DwellUpdatePID={Environment.ProcessId}");
        using var process = Process.Start(start) ?? throw new IOException("The installer did not start.");
    }

    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, bool Draft, bool Prerelease, Asset[]? Assets);
    private sealed record Asset(string Name, string State, long Size, string? Digest);
}
