namespace OpenClaw.Launcher.Session;

/// <summary>Where the agent's <c>openclaw</c> command lives.</summary>
internal sealed record AgentTools(string DirectoryPath, string ShimPath);

/// <summary>Builds environment values consumed by the guest-side command shim.</summary>
internal static class AgentToolShim
{
    internal const string NodeVariable = "OPENCLAW_SHIM_NODE";
    internal const string EntryPointVariable = "OPENCLAW_SHIM_ENTRY";
    internal const string NativeApplicationRootVariable =
        "OPENCLAW_SHIM_NATIVE_APP_ROOT";
    internal const string NativeStagedRootVariable =
        "OPENCLAW_SHIM_NATIVE_STAGED_ROOT";
    internal const string NodeOptionsSuffixVariable =
        "OPENCLAW_SHIM_NODE_OPTIONS_SUFFIX";

    public static IReadOnlyDictionary<string, string> BuildEnvironment(
        string nodePath,
        string applicationDirectory,
        string? nativeRootPath = null,
        string? nodeOptionsSuffix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [NodeVariable] = nodePath,
            [EntryPointVariable] = Path.Combine(applicationDirectory, "openclaw.mjs"),
        };

        if (string.IsNullOrWhiteSpace(nativeRootPath))
        {
            return environment;
        }

        if (string.IsNullOrWhiteSpace(nodeOptionsSuffix))
        {
            throw new ArgumentException(
                "A native redirect preload is required with a staged native root.",
                nameof(nodeOptionsSuffix));
        }

        environment[NativeApplicationRootVariable] = applicationDirectory;
        environment[NativeStagedRootVariable] = nativeRootPath;
        environment[NodeOptionsSuffixVariable] = nodeOptionsSuffix;
        return environment;
    }
}
