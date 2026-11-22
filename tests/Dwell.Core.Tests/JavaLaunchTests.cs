using Dwell.Core;
using Xunit;

namespace Dwell.Core.Tests;

public sealed class JavaLaunchTests
{
    [Fact]
    public void DifferentJarsSharingAJavaExecutableHaveDifferentTargets()
    {
        var first = JavaLaunch.FromArguments(["javaw", "-Xmx1G", "-jar", @"C:\Apps\One\client.jar", "--token", "secret"]);
        var second = JavaLaunch.FromArguments(["javaw", "-jar", @"C:\Apps\Two\client.jar"]);

        Assert.Equal("client", first?.Name);
        Assert.Equal("client", second?.Name);
        Assert.NotEqual(first?.Target, second?.Target);
        Assert.DoesNotContain("secret", first!.Target);
    }

    [Fact]
    public void FindsTheEntryPointAfterJavaRuntimeOptions()
    {
        Assert.Equal("class:example.app.Main",
            JavaLaunch.FromArguments(["javaw", "-cp", @"C:\app\lib", "-Dprofile=test", "example.app.Main", "other.Main"])?.Target);
        Assert.Equal("module:example.app/example.Main",
            JavaLaunch.FromArguments(["javaw", "--module-path", "mods", "--module=example.app/example.Main"])?.Target);
        Assert.Null(JavaLaunch.FromArguments(["javaw", "@options.txt", "other.Main"]));
    }
}
