namespace Dwell.Core;

public sealed record JavaLaunch(string Target, string Name)
{
    public static JavaLaunch? FromArguments(IReadOnlyList<string> arguments, Func<string, IReadOnlyList<string>?>? readArgumentFile = null)
    {
        var canExpandFiles = readArgumentFile is not null;
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
                    if (arg == "--disable-@files") canExpandFiles = false;
                    if (arg.StartsWith('@'))
                    {
                        if (!canExpandFiles || readArgumentFile!(arg[1..]) is not { } expanded) return null;
                        var combined = arguments.Take(i).Concat(expanded).Concat(arguments.Skip(i + 1)).ToArray();
                        return FromArguments(combined);
                    }
                    if (arg.Length == 0 || arg.StartsWith('-')) break;
                    if (arg.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
                        return new("source:" + arg.Replace('/', '\\').ToUpperInvariant(), FileNameWithoutExtension(arg));
                    return IsClassName(arg) ? new("class:" + arg, arg) : null;
            }
        }
        return null;
    }

    public static IReadOnlyList<string> TokenizeArgumentFile(string contents)
    {
        var arguments = new List<string>();
        var token = new System.Text.StringBuilder();
        char quote = '\0';
        var hasToken = false;
        for (var i = 0; i < contents.Length; i++)
        {
            var ch = contents[i];
            if (quote == '\0' && ch == '#')
            {
                while (i + 1 < contents.Length && contents[i + 1] is not ('\r' or '\n')) i++;
                continue;
            }
            if (quote == '\0' && char.IsWhiteSpace(ch))
            {
                if (hasToken) { arguments.Add(token.ToString()); token.Clear(); hasToken = false; }
                continue;
            }
            hasToken = true;
            if (ch == quote) { quote = '\0'; continue; }
            if (quote == '\0' && ch is '\'' or '"') { quote = ch; continue; }
            if (quote != '\0' && ch == '\\' && i + 1 < contents.Length)
            {
                ch = contents[++i];
                if (ch is '\r' or '\n')
                {
                    while (i + 1 < contents.Length && char.IsWhiteSpace(contents[i + 1])) i++;
                    continue;
                }
                ch = ch switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'f' => '\f', _ => ch };
            }
            token.Append(ch);
        }
        if (hasToken) arguments.Add(token.ToString());
        return arguments;
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
