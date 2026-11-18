using Dwell.Core;

namespace Dwell.Services;

public sealed record AppGroup(string Key, AppRecord App, AppRecord[] Variants);
public sealed record ActivityEntry(Guid Id, AppGroup Group, DateTimeOffset Start, DateTimeOffset End)
{
    public double Seconds => (End - Start).TotalSeconds;
}

public static class UsageProjection
{
    public static AppGroup[] Groups(IEnumerable<AppRecord> apps, bool includeIgnored = false) => apps
        .Where(x => ApplicationPolicy.IsUserApplication(x.Exe))
        .GroupBy(x => ApplicationPolicy.Identity(x.Exe, x.Name))
        .Where(group => includeIgnored || !group.Any(x => x.Ignored))
        .Select(group => new AppGroup(group.Key, group.MaxBy(x => x.Id)!, group.ToArray())).ToArray();

    public static ActivityEntry[] Entries(UsageResult result, DateTimeOffset start, DateTimeOffset end)
    {
        var byId = Groups(result.Apps).SelectMany(group => group.Variants.Select(app => (app.Id, Group: group))).ToDictionary(x => x.Id, x => x.Group);
        var now = DateTimeOffset.UtcNow;
        if (end > now) end = now;
        return result.Sessions.Where(x => byId.ContainsKey(x.AppId) && x.End > start && x.Start < end)
            .Select(x => new ActivityEntry(x.Id, byId[x.AppId], x.Start > start ? x.Start : start, x.End < end ? x.End : end))
            .Where(x => x.End > x.Start).OrderByDescending(x => x.Start).ToArray();
    }

    public static double TargetSeconds(string target, IEnumerable<ActivityEntry> entries, IReadOnlySet<string> allowed) => entries
        .Where(x => !allowed.Contains(x.Group.Key) && Matches(target, x.Group))
        .Sum(x => x.Seconds);

    public static bool Matches(string target, AppGroup app) => target == "all" || target == "app:" + app.Key || target == "category:" + (int)app.App.Category;
    public static string TargetName(string target, string fallback) => target == "all" ? Strings.Get("WholePC") :
        target.StartsWith("category:", StringComparison.Ordinal) && int.TryParse(target.AsSpan(9), out var category) && category is >= 0 and <= 4
            ? Strings.Get("Category" + (AppCategory)category) : fallback;
}
