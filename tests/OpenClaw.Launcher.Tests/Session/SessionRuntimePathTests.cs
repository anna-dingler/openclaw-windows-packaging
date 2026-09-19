using OpenClaw.SessionHost;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Covers the agent's own <c>PATH</c>, which is what makes the packaged Node.js
/// runtime win over a machine-wide installation.
/// </summary>
public sealed class SessionRuntimePathTests
{
    private const string Runtime =
        @"C:\Users\agent\AppData\Local\OpenClaw\NodeJS\node-v24.15.0-win-x64";

    // The whole point is precedence. Appended, a machine-wide Node.js keeps
    // winning for anything that resolves `node` by name.
    [Fact]
    public void TheRuntimeComesFirst()
    {
        string result = SessionRuntimeInstaller.BuildPath(
            @"C:\Windows\system32;C:\Program Files\nodejs", Runtime);

        Assert.StartsWith(Runtime + ";", result, StringComparison.Ordinal);
        Assert.Contains(@"C:\Program Files\nodejs", result, StringComparison.Ordinal);
    }

    // Setup is idempotent, so re-running it must not grow the value every time.
    [Fact]
    public void RepeatedInstallsDoNotAccumulateEntries()
    {
        string once = SessionRuntimeInstaller.BuildPath(@"C:\Windows\system32", Runtime);
        string twice = SessionRuntimeInstaller.BuildPath(once, Runtime);

        Assert.Equal(once, twice);
    }

    // A package upgrade installs a new version beside the old one. Leaving the
    // old directory on PATH would keep resolving the runtime the installation
    // no longer supports.
    [Fact]
    public void APreviousRuntimeVersionIsRemoved()
    {
        const string previous =
            @"C:\Users\agent\AppData\Local\OpenClaw\NodeJS\node-v22.1.0-win-x64";

        string result = SessionRuntimeInstaller.BuildPath(
            $@"{previous};C:\Windows\system32", Runtime);

        Assert.DoesNotContain(previous, result, StringComparison.Ordinal);
        Assert.StartsWith(Runtime + ";", result, StringComparison.Ordinal);
        Assert.Contains(@"C:\Windows\system32", result, StringComparison.Ordinal);
    }

    // An unrelated directory that merely looks similar must survive: this runs
    // against a real account's PATH, not a fixture.
    [Fact]
    public void UnrelatedEntriesAreLeftAlone()
    {
        string result = SessionRuntimeInstaller.BuildPath(
            @"C:\tools\node-v24.15.0-win-x64;C:\Windows", Runtime);

        Assert.Contains(@"C:\tools\node-v24.15.0-win-x64", result, StringComparison.Ordinal);
        Assert.Contains(@"C:\Windows", result, StringComparison.Ordinal);
    }

    // An empty value is the normal first-run case for a fresh profile.
    [Fact]
    public void AnEmptyPathBecomesJustTheRuntime()
    {
        Assert.Equal(Runtime, SessionRuntimeInstaller.BuildPath(string.Empty, Runtime));
    }

    // Only the guest can resolve the child's PATH, because the host never sees
    // the agent account's environment.
    [Fact]
    public void ALaunchPutsTheRuntimeAheadOfTheInheritedPath()
    {
        System.Diagnostics.ProcessStartInfo startInfo = new();
        startInfo.Environment["PATH"] = @"C:\Program Files\nodejs";

        SessionProcessLauncher.PrependPath(startInfo, Runtime);

        Assert.Equal(
            $@"{Runtime};C:\Program Files\nodejs",
            startInfo.Environment["PATH"]);
    }

    [Fact]
    public void ALaunchWithoutARuntimeLeavesThePathAlone()
    {
        System.Diagnostics.ProcessStartInfo startInfo = new();
        startInfo.Environment["PATH"] = @"C:\Program Files\nodejs";

        SessionProcessLauncher.PrependPath(startInfo, null);

        Assert.Equal(@"C:\Program Files\nodejs", startInfo.Environment["PATH"]);
    }

    /// <summary>
    /// Only the guest can compose the agent's <c>NODE_OPTIONS</c>. The host's
    /// environment values are assigned over the agent's, so a value composed
    /// on the host would drop whatever the agent set - and the host's own
    /// <c>NODE_OPTIONS</c> is not the agent's to begin with.
    /// </summary>
    [Fact]
    public void ALaunchAppendsToTheAgentsOwnNodeOptions()
    {
        System.Diagnostics.ProcessStartInfo startInfo = new();
        startInfo.Environment["NODE_OPTIONS"] = "--max-old-space-size=4096";

        SessionProcessLauncher.AppendNodeOptions(startInfo, "--import file:///C:/p/r.mjs");

        Assert.Equal(
            "--max-old-space-size=4096 --import file:///C:/p/r.mjs",
            startInfo.Environment["NODE_OPTIONS"]);
    }

    [Fact]
    public void ALaunchWithoutAgentNodeOptionsUsesJustTheSuffix()
    {
        System.Diagnostics.ProcessStartInfo startInfo = new();

        SessionProcessLauncher.AppendNodeOptions(startInfo, "--import file:///C:/p/r.mjs");

        Assert.Equal("--import file:///C:/p/r.mjs", startInfo.Environment["NODE_OPTIONS"]);
    }

    // A package that stages nothing must leave the agent's value untouched
    // rather than blanking it.
    [Fact]
    public void ALaunchWithoutASuffixLeavesTheAgentsNodeOptionsAlone()
    {
        System.Diagnostics.ProcessStartInfo startInfo = new();
        startInfo.Environment["NODE_OPTIONS"] = "--max-old-space-size=4096";

        SessionProcessLauncher.AppendNodeOptions(startInfo, null);

        Assert.Equal("--max-old-space-size=4096", startInfo.Environment["NODE_OPTIONS"]);
    }
}
