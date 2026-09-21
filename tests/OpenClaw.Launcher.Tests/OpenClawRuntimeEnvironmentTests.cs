namespace OpenClaw.Launcher.Tests;

public sealed class OpenClawRuntimeEnvironmentTests
{
    /// <summary>
    /// The redirect roots remain separate from the agent's own Node.js options.
    /// </summary>
    [Fact]
    public void NativeRedirectNamesBothRootsAndLeavesNodeOptionsToTheAgent()
    {
        IReadOnlyDictionary<string, string> result =
            OpenClawRuntimeEnvironment.BuildNativeRedirect(
                @"C:\Package\app",
                @"C:\Users\agent\AppData\Local\OpenClawGatewayMSIX\agent-native\abc");

        Assert.Equal(@"C:\Package\app", result["OPENCLAW_NATIVE_APP_ROOT"]);
        Assert.Equal(
            @"C:\Users\agent\AppData\Local\OpenClawGatewayMSIX\agent-native\abc",
            result["OPENCLAW_NATIVE_STAGED_ROOT"]);

        // Assigned over the agent's environment, so a value composed here would
        // replace whatever the agent already set.
        Assert.False(result.ContainsKey("NODE_OPTIONS"));
    }

    [Fact]
    public void NativeRedirectNodeArgumentsImportThePreload()
    {
        Assert.Equal(
            ["--import", "file:///C:/Package/node/native-redirect.mjs"],
            OpenClawRuntimeEnvironment.BuildNativeRedirectNodeArguments(
                @"C:\Package\node\native-redirect.mjs"));
    }

    /// <summary>
    /// The package installs under "Program Files", so the option value must
    /// not contain a raw space that NODE_OPTIONS would split on.
    /// </summary>
    [Fact]
    public void NativeRedirectEncodesSpacesInThePreloadPath()
    {
        IReadOnlyList<string> arguments =
            OpenClawRuntimeEnvironment.BuildNativeRedirectNodeArguments(
            @"C:\Program Files\WindowsApps\OpenClaw\node\native-redirect.mjs");
        string preloadUrl = arguments[1];

        Assert.Equal("--import", arguments[0]);
        Assert.DoesNotContain("Program Files", preloadUrl, StringComparison.Ordinal);
        Assert.Contains("Program%20Files", preloadUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildInteractiveAddsOnlyMissingTerminalHints()
    {
        IReadOnlyDictionary<string, string> result = Build(true, new Dictionary<string, string?>());

        Assert.Equal("3", result["FORCE_COLOR"]);
        Assert.Equal("1", result["WT_SESSION"]);
        Assert.False(result.ContainsKey("NO_COLOR"));
    }

    [Fact]
    public void BuildPreservesExplicitTerminalValuesAndNoColor()
    {
        IReadOnlyDictionary<string, string> result = Build(
            true,
            new Dictionary<string, string?>
            {
                ["FORCE_COLOR"] = "0",
                ["WT_SESSION"] = "real-session",
                ["NO_COLOR"] = string.Empty,
            });

        Assert.Equal("0", result["FORCE_COLOR"]);
        Assert.Equal("real-session", result["WT_SESSION"]);
        Assert.Equal(string.Empty, result["NO_COLOR"]);
    }

    [Fact]
    public void BuildDetachedDoesNotSynthesizeTerminalHints()
    {
        IReadOnlyDictionary<string, string> result = Build(false, new Dictionary<string, string?>());

        Assert.DoesNotContain("FORCE_COLOR", result.Keys);
        Assert.DoesNotContain("WT_SESSION", result.Keys);
    }

    [Fact]
    public void BuildPerfAddsDefaultsButExplicitValuesWin()
    {
        IReadOnlyDictionary<string, string> result = Build(
            false,
            new Dictionary<string, string?>
            {
                ["OPENCLAW_PERF"] = "1",
                ["OPENCLAW_LOG_LEVEL"] = "trace",
                ["OPENCLAW_GATEWAY_STARTUP_TRACE"] = "0",
                ["OPENCLAW_HANDSHAKE_TIMEOUT_MS"] = "1200",
            });

        Assert.Equal("1", result["OPENCLAW_PERF"]);
        Assert.Equal("trace", result["OPENCLAW_LOG_LEVEL"]);
        Assert.Equal("0", result["OPENCLAW_GATEWAY_STARTUP_TRACE"]);
        Assert.Equal("1", result["OPENCLAW_DIAGNOSTICS"]);
        Assert.Equal("1", result["OPENCLAW_DIAGNOSTICS_EVENT_LOOP"]);
        Assert.Equal("1200", result["OPENCLAW_HANDSHAKE_TIMEOUT_MS"]);
    }

    [Fact]
    public void BuildLoggingAloneDoesNotEnablePerfBundle()
    {
        IReadOnlyDictionary<string, string> result = Build(
            false,
            new Dictionary<string, string?> { ["OPENCLAW_LOG_LEVEL"] = "debug" });

        Assert.Equal("debug", result["OPENCLAW_LOG_LEVEL"]);
        Assert.DoesNotContain("OPENCLAW_GATEWAY_STARTUP_TRACE", result.Keys);
        Assert.DoesNotContain("OPENCLAW_DIAGNOSTICS", result.Keys);
    }

    [Fact]
    public void BuildReadsEachSelectedEnvironmentVariableOnce()
    {
        Dictionary<string, int> reads = new(StringComparer.OrdinalIgnoreCase);

        IReadOnlyDictionary<string, string> result = OpenClawRuntimeEnvironment.Build(
            false,
            name =>
            {
                reads[name] = reads.GetValueOrDefault(name) + 1;
                return name == "OPENCLAW_PERF" ? "1" : null;
            });

        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.Equal("debug", result["OPENCLAW_LOG_LEVEL"]);
        Assert.Equal("60000", result["OPENCLAW_HANDSHAKE_TIMEOUT_MS"]);
    }

    [Fact]
    public void BuildCarriesSelectedValuesButExcludesUnrelatedSensitiveEnvironment()
    {
        IReadOnlyDictionary<string, string> result = Build(
            false,
            new Dictionary<string, string?>
            {
                ["OPENCLAW_DIAGNOSTICS"] = "0",
                ["OPENAI_API_KEY"] = "secret",
                ["PATH"] = "sensitive",
            });

        Assert.Equal("0", result["OPENCLAW_DIAGNOSTICS"]);
        Assert.DoesNotContain("OPENAI_API_KEY", result.Keys);
        Assert.DoesNotContain("PATH", result.Keys);
        Assert.Equal("external", result["OPENCLAW_SUPERVISOR_MODE"]);
    }

    [Fact]
    public void ApplyToLeavesUnrelatedInputAndAddsPolicy()
    {
        Dictionary<string, string?> environment = new()
        {
            ["PATH"] = "unchanged",
            ["CUSTOM"] = "value",
        };

        OpenClawRuntimeEnvironment.ApplyTo(environment, false, _ => null);

        Assert.Equal("unchanged", environment["PATH"]);
        Assert.Equal("value", environment["CUSTOM"]);
        Assert.Equal("external", environment["OPENCLAW_SERVICE_REPAIR_POLICY"]);
    }

    private static IReadOnlyDictionary<string, string> Build(
        bool isInteractive,
        Dictionary<string, string?> values) =>
        OpenClawRuntimeEnvironment.Build(
            isInteractive,
            name => values.TryGetValue(name, out string? value) ? value : null);
}
