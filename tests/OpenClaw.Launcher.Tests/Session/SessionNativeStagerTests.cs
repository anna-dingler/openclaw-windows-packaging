using OpenClaw.SessionHost;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Covers the mirroring that makes packaged native addons loadable by the
/// isolated-session identity.
/// </summary>
/// <remarks>
/// These tests use ordinary files rather than real addons: the staging
/// contract is about which directories are copied and when, not about loading
/// native code.
/// </remarks>
public sealed class SessionNativeStagerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"koffi\index.js", "koffi")]
    [InlineData(@"koffi\node_modules\@koromix\koffi-win32-x64\koffi.node", "koffi")]
    [InlineData(@"@openclaw\fs-safe\native.node", @"@openclaw\fs-safe")]
    [InlineData(@"@openclaw\fs-safe\node_modules\@openclaw\inner\x.node", @"@openclaw\fs-safe")]
    public void OwningPackageIsTheOutermostPackageDirectory(
        string relativePath,
        string expected)
    {
        Assert.True(
            SessionNativeStager.TryGetOwningPackage(relativePath, out string package));
        Assert.Equal(expected, package);
    }

    [Theory]
    [InlineData("koffi")]
    [InlineData(@"@openclaw")]
    [InlineData("")]
    public void LooseEntriesHaveNoOwningPackage(string relativePath)
    {
        Assert.False(
            SessionNativeStager.TryGetOwningPackage(relativePath, out _));
    }

    [Fact]
    public void DiscoveryFindsOnlyPackagesCarryingNativeArtifacts()
    {
        string modules = Path.Combine(_root, "app", "node_modules");
        WriteFile(modules, @"koffi\node_modules\@koromix\koffi-win32-x64\koffi.node");
        WriteFile(modules, @"@openclaw\fs-safe\native.node");
        WriteFile(modules, @"sqlite-vec\node_modules\sqlite-vec-windows-x64\vec0.dll");
        WriteFile(modules, @"node-pty\prebuilds\conpty\OpenConsole.exe");
        WriteFile(modules, @"lodash\index.js");
        WriteFile(modules, @"react\index.js");

        IReadOnlyList<string> discovered =
            SessionNativeStager.DiscoverNativePackages(modules);

        Assert.Equal(
            [@"@openclaw\fs-safe", "koffi", "node-pty", "sqlite-vec"],
            discovered);
    }

    /// <summary>
    /// The discovery contract that keeps this fix from going stale: a package
    /// nobody listed anywhere is staged because it carries a native artifact.
    /// </summary>
    [Fact]
    public void DiscoveryFindsPackagesNoListNames()
    {
        string modules = Path.Combine(_root, "app", "node_modules");
        WriteFile(modules, @"brand-new-upstream-dep\build\Release\addon.node");

        Assert.Equal(
            ["brand-new-upstream-dep"],
            SessionNativeStager.DiscoverNativePackages(modules));
    }

    [Fact]
    public void StageMirrorsTheWholeOwningPackageAndLeavesOtherPackagesBehind()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\index.js", "loader");
        WriteFile(modules, @"koffi\node_modules\@koromix\koffi-win32-x64\koffi.node", "native");
        WriteFile(modules, @"lodash\index.js", "js only");

        string staged = SessionNativeStager.Stage(application, Path.Combine(_root, "local"))!;

        // The sibling JavaScript comes along, because the package's own files
        // locate the binaries next to them.
        Assert.Equal(
            "loader",
            File.ReadAllText(Path.Combine(staged, "node_modules", "koffi", "index.js")));
        Assert.Equal(
            "native",
            File.ReadAllText(Path.Combine(
                staged,
                @"node_modules\koffi\node_modules\@koromix\koffi-win32-x64\koffi.node")));
        Assert.False(
            Directory.Exists(Path.Combine(staged, "node_modules", "lodash")),
            "A package without native artifacts must keep running from the package.");
    }

    [Fact]
    public void StageIsIdempotentForUnchangedPackageContent()
    {
        string application = Path.Combine(_root, "app");
        WriteFile(Path.Combine(application, "node_modules"), @"koffi\koffi.node", "native");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;
        string marker = Path.Combine(first, "node_modules", "koffi", "marker.txt");
        File.WriteAllText(marker, "untouched");

        string second = SessionNativeStager.Stage(application, local)!;

        Assert.Equal(first, second);
        Assert.True(File.Exists(marker), "Unchanged content must not be recopied.");
    }

    [Fact]
    public void ChangedPackageContentStagesToANewRootAndRemovesTheOldOne()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\koffi.node", "native");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;
        WriteFile(modules, @"koffi\koffi.node", "a different native payload");
        string second = SessionNativeStager.Stage(application, local)!;

        Assert.NotEqual(first, second);
        Assert.False(
            Directory.Exists(first),
            "An upgrade must not leave the superseded copy behind.");
        Assert.Equal(
            "a different native payload",
            File.ReadAllText(Path.Combine(second, "node_modules", "koffi", "koffi.node")));
    }

    /// <summary>
    /// Content identity must come from bytes. Size and write time are not
    /// enough: a republished package can keep both.
    /// </summary>
    [Fact]
    public void ChangedContentAtTheSameLengthAndTimestampStagesAgain()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        string native = Path.Combine(modules, @"koffi\koffi.node");
        WriteFile(modules, @"koffi\koffi.node", "native-aaa");
        string local = Path.Combine(_root, "local");
        DateTime written = File.GetLastWriteTimeUtc(native);

        string first = SessionNativeStager.Stage(application, local)!;

        File.WriteAllText(native, "native-bbb");
        File.SetLastWriteTimeUtc(native, written);
        Assert.Equal(written, File.GetLastWriteTimeUtc(native));

        string second = SessionNativeStager.Stage(application, local)!;

        Assert.NotEqual(first, second);
        Assert.Equal(
            "native-bbb",
            File.ReadAllText(Path.Combine(second, @"node_modules\koffi\koffi.node")));
    }

    /// <summary>
    /// A superseded root can still be the one a running gateway loads from.
    /// Reclaiming it must be all-or-nothing, never a partial delete that
    /// removes that process's unlocked files.
    /// </summary>
    [Fact]
    public void ASupersededRootStillInUseIsLeftIntact()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\koffi.node", "native");
        WriteFile(modules, @"koffi\index.js", "loader");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;
        string stagedNative = Path.Combine(first, @"node_modules\koffi\koffi.node");
        string stagedLoader = Path.Combine(first, @"node_modules\koffi\index.js");

        // Stands in for the mapped native image a running process holds.
        using (File.Open(stagedNative, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            WriteFile(modules, @"koffi\koffi.node", "a different native payload");
            string second = SessionNativeStager.Stage(application, local)!;

            Assert.NotEqual(first, second);
            Assert.True(
                File.Exists(stagedLoader),
                "Reclaiming an in-use root must not delete the files around the "
                    + "locked one.");
            Assert.Equal("loader", File.ReadAllText(stagedLoader));
            Assert.Equal("native", File.ReadAllText(stagedNative));
        }
    }

    /// <summary>
    /// A consumer need not have any staged file open. An agent shell sitting
    /// at a prompt has the redirect in its environment and nothing mapped, yet
    /// every OpenClaw it later runs resolves through that root.
    /// </summary>
    [Fact]
    public void ASupersededRootWithALiveConsumerAndNoOpenFilesIsLeftIntact()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\koffi.node", "native");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;

        using (FileStream? lease = SessionNativeStager.OpenConsumerLease(first))
        {
            Assert.NotNull(lease);

            WriteFile(modules, @"koffi\koffi.node", "a different native payload");
            string second = SessionNativeStager.Stage(application, local)!;

            Assert.NotEqual(first, second);
            Assert.Equal(
                "native",
                File.ReadAllText(Path.Combine(first, @"node_modules\koffi\koffi.node")));
        }
    }

    /// <summary>
    /// The retry the reclamation promises. After the upgrade, every later setup
    /// sees unchanged content, so reclamation has to run on that path too or
    /// releasing the consumer never frees the root.
    /// </summary>
    [Fact]
    public void AReleasedRootIsReclaimedByTheNextUnchangedContentSetup()
    {
        string application = Path.Combine(_root, "app");
        string modules = Path.Combine(application, "node_modules");
        WriteFile(modules, @"koffi\koffi.node", "native");
        string local = Path.Combine(_root, "local");

        string first = SessionNativeStager.Stage(application, local)!;
        string second;
        using (SessionNativeStager.OpenConsumerLease(first))
        {
            WriteFile(modules, @"koffi\koffi.node", "a different native payload");
            second = SessionNativeStager.Stage(application, local)!;
            Assert.True(Directory.Exists(first));
        }

        // Content is unchanged now, so this is the reuse path.
        Assert.Equal(second, SessionNativeStager.Stage(application, local));
        Assert.False(
            Directory.Exists(first),
            "Releasing the consumer must let the next setup reclaim the root.");
    }

    [Fact]
    public void AConsumerLeaseIsNotRequiredForALaunchToProceed()
    {
        Assert.Null(SessionNativeStager.OpenConsumerLease(null));
        Assert.Null(SessionNativeStager.OpenConsumerLease(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void AnApplicationWithoutNativeArtifactsStagesNothing()
    {
        string application = Path.Combine(_root, "app");
        WriteFile(Path.Combine(application, "node_modules"), @"lodash\index.js");

        Assert.Null(SessionNativeStager.Stage(application, Path.Combine(_root, "local")));
    }

    [Fact]
    public void AnApplicationWithoutModulesStagesNothing()
    {
        string application = Path.Combine(_root, "app");
        Directory.CreateDirectory(application);

        Assert.Null(SessionNativeStager.Stage(application, Path.Combine(_root, "local")));
    }

    private static void WriteFile(
        string modulesDirectory,
        string relativePath,
        string content = "x")
    {
        string path = Path.Combine(modulesDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
