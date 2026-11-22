namespace Dwell.Core;

public sealed record JavaLaunch(string Target, string Name)
{
    public static JavaLaunch? FromArguments(IReadOnlyList<string> arguments)
    {
        for (var i = 1; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            switch (arg)
            {
                case "-jar":
                    return i + 1 < arguments.Count ? Jar(arguments[i + 1]) : null;
                case "-m" or "--module":
                    return i + 1 < arguments.Count ? Module(arguments[i + 1]) : null;
                case "-cp" or "-classpath" or "--class-path" or "-p" or "--module-path" or
                    "--upgrade-module-path" or "--add-modules" or "--limit-modules" or "--add-reads" or
                    "--add-exports" or "--add-opens" or "--patch-module" or "--source":
                    i++;
                    break;
                default:
                    if (arg.StartsWith("--module=", StringComparison.Ordinal)) return Module(arg[9..]);
                    if (arg.StartsWith('@')) return null;
                    if (arg.Length == 0 || arg.StartsWith('-')) break;
                    if (arg.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                        return new("source:" + arg.Replace('/', '\\').ToUpperInvariant(), FileNameWithoutExtension(arg));
                    return IsClassName(arg) ? new("class:" + arg, arg) : null;
            }
        }
        return null;
    }

    private static JavaLaunch? Jar(string path) =>
        path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            ? new("jar:" + path.Replace('/', '\\').ToUpperInvariant(), FileNameWithoutExtension(path)) : null;

    private static JavaLaunch? Module(string target)
    {
        var slash = target.IndexOf('/');
        var name = slash < 0 ? target : target[..slash];
        return IsClassName(name) && (slash < 0 || IsClassName(target[(slash + 1)..]))
            ? new("module:" + target, name) : null;
    }

    private static string FileNameWithoutExtension(string path) => Path.GetFileNameWithoutExtension(path.Replace('\\', '/'));
    private static bool IsClassName(string name) =>
        name.Length > 0 && name.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '$');
}
