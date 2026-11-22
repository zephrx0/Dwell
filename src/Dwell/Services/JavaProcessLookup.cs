using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Dwell.Core;

namespace Dwell.Services;

internal sealed class JavaProcessLookup
{
    private readonly Dictionary<uint, (long Started, string Key, string? Name)> _launches = [];

    public (string Key, string? Name) Resolve(uint pid, string exe)
    {
        if (!ApplicationPolicy.IsJavaLauncher(exe)) return (exe, null);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var started = process.StartTime.ToUniversalTime().Ticks;
            if (!_launches.TryGetValue(pid, out var entry) || entry.Started != started)
            {
                if (_launches.Count >= 128) _launches.Clear();
                var launch = ReadLaunch(pid);
                var key = exe;
                if (launch is { Name.Length: > 0 })
                    key += "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AbsoluteTarget(launch.Target, pid))));
                entry = (started, key, launch?.Name);
                _launches[pid] = entry;
            }
            return (entry.Key, entry.Name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ManagementException
            or UnauthorizedAccessException or Win32Exception or COMException) { }
        return (exe, null);
    }

    private static string AbsoluteTarget(string target, uint pid)
    {
        var prefix = target.StartsWith("jar:", StringComparison.Ordinal) ? 4 :
            target.StartsWith("source:", StringComparison.Ordinal) ? 7 : 0;
        if (prefix == 0) return target;
        var path = target[prefix..];
        if (Path.IsPathFullyQualified(path) || ProcessWorkingDirectory.Read(pid) is not { } directory) return target;
        try { return target[..prefix] + Path.GetFullPath(path, directory).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return target; }
    }

    private static JavaLaunch? ReadLaunch(uint pid)
    {
        using var query = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" +
            pid.ToString(CultureInfo.InvariantCulture));
        using var rows = query.Get();
        foreach (ManagementObject row in rows)
        {
            using (row)
                if (row["CommandLine"] is string { Length: > 0 } line)
                    return Parse(line, pid);
        }
        return null;
    }

    private static JavaLaunch? Parse(string line, uint pid)
    {
        var argv = CommandLineToArgvW(line, out var count);
        if (argv == 0) return null;
        try
        {
            var arguments = new string[count];
            for (var i = 0; i < count; i++)
                arguments[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return JavaLaunch.FromArguments(arguments, name => ReadArguments(name, pid));
        }
        finally { LocalFree(argv); }
    }

    private static IReadOnlyList<string>? ReadArguments(string name, uint pid)
    {
        try
        {
            if (!Path.IsPathFullyQualified(name))
            {
                if (ProcessWorkingDirectory.Read(pid) is not { } directory) return null;
                name = Path.GetFullPath(name, directory);
            }
            if (name.Length < 3 || name[1] != ':' || !char.IsAsciiLetter(name[0])) return null;
            using var source = new FileStream(name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (source.Length is <= 0 or > 65_536) return null;
            var data = new byte[(int)source.Length];
            source.ReadExactly(data);
            using var reader = new StreamReader(new MemoryStream(data), detectEncodingFromByteOrderMarks: true);
            return JavaLaunch.TokenizeArgumentFile(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint LocalFree(nint memory);
}
