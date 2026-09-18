using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenClaw.SessionHost;

/// <summary>
/// Mirrors the application's native dependency packages into this account's
/// own profile.
/// </summary>
/// <remarks>
/// <para>
/// The agent identity may read package content but may not map it as an
/// executable image: loading any packaged <c>.node</c> fails with
/// <c>ERR_DLOPEN_FAILED</c> / access denied even though the same bytes load
/// from a writable location. Node.js resolves a native addon through the
/// package directory that owns it, and the packages themselves derive sibling
/// DLLs and helper executables from their own directory, so whole owning
/// package directories are mirrored rather than individual binaries.
/// </para>
/// <para>
/// Only packages that actually carry native artifacts are copied. The rest of
/// the application, which is the overwhelming majority of it, keeps running
/// directly from the immutable package.
/// </para>
/// <para>
/// The set is discovered by scanning, never hard-coded. An upstream revision
/// that introduces a new native dependency is then staged automatically
/// instead of failing at runtime once something happens to call it.
/// </para>
/// <para>
/// The destination is the agent's own profile, not the shared workspace. The
/// agent already controls every process that loads these files, so this moves
/// nothing across a trust boundary; staging into the guest-writable workspace
/// would.
/// </para>
/// </remarks>
internal static class SessionNativeStager
{
    internal const string DirectoryName = "agent-native";

    private const string ModulesDirectoryName = "node_modules";
    private const string MarkerFileName = ".staged-content-id";

    /// <summary>
    /// File kinds that cannot be loaded from the package by this identity.
    /// </summary>
    /// <remarks>
    /// <c>.node</c> is the addon itself; <c>.dll</c> and <c>.exe</c> cover the
    /// dependent libraries and helper processes an addon starts, which fail the
    /// same way and are located relative to the addon.
    /// </remarks>
    private static readonly string[] NativeExtensions = [".node", ".dll", ".exe"];

    /// <summary>
    /// Mirrors every native-bearing package and returns the staged root, or
    /// <see langword="null"/> when the application carries no native code.
    /// </summary>
    public static string? Stage(string applicationDirectory, string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);

        string modulesDirectory = Path.Combine(applicationDirectory, ModulesDirectoryName);
        if (!Directory.Exists(modulesDirectory))
        {
            return null;
        }

        IReadOnlyList<string> packages = DiscoverNativePackages(modulesDirectory);
        if (packages.Count == 0)
        {
            return null;
        }

        string contentId = ComputeContentId(modulesDirectory, packages);
        string root = Path.Combine(
            localApplicationData,
            "OpenClawGatewayMSIX",
            DirectoryName,
            contentId);

        string marker = Path.Combine(root, MarkerFileName);
        if (File.Exists(marker) &&
            string.Equals(
                File.ReadAllText(marker).Trim(),
                contentId,
                StringComparison.Ordinal))
        {
            return root;
        }

        string staging = $"{root}.stage-{Guid.NewGuid():N}";
        try
        {
            foreach (string package in packages)
            {
                CopyDirectory(
                    Path.Combine(modulesDirectory, package),
                    Path.Combine(staging, ModulesDirectoryName, package));
            }

            // Written last: a marker only ever appears over a complete copy, so
            // an interrupted staging is redone rather than trusted.
            File.WriteAllText(Path.Combine(staging, MarkerFileName), contentId);

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            Directory.Move(staging, root);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }

        RemoveSupersededRoots(Path.GetDirectoryName(root)!, contentId);
        return root;
    }

    /// <summary>
    /// Returns the package directories, relative to <c>node_modules</c>, that
    /// contain at least one native artifact.
    /// </summary>
    /// <remarks>
    /// The owning directory is the outermost package under the application's
    /// own <c>node_modules</c>, so a native reached through a nested
    /// <c>node_modules</c> keeps the layout its resolver expects.
    /// </remarks>
    internal static IReadOnlyList<string> DiscoverNativePackages(string modulesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulesDirectory);

        SortedSet<string> packages = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in EnumerateNativeFiles(modulesDirectory))
        {
            string relative = Path.GetRelativePath(modulesDirectory, file);
            if (TryGetOwningPackage(relative, out string? package))
            {
                packages.Add(package);
            }
        }

        return [.. packages];
    }

    /// <summary>
    /// Takes the package name from the front of a path relative to
    /// <c>node_modules</c>, keeping both segments of a scoped name.
    /// </summary>
    internal static bool TryGetOwningPackage(
        string relativePath,
        out string package)
    {
        package = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        string[] segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        if (segments[0].StartsWith('@'))
        {
            if (segments.Length < 3)
            {
                return false;
            }

            package = Path.Combine(segments[0], segments[1]);
            return true;
        }

        package = segments[0];
        return true;
    }

    private static IEnumerable<string> EnumerateNativeFiles(string directory)
    {
        foreach (string file in Directory.EnumerateFiles(
            directory, "*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            foreach (string candidate in NativeExtensions)
            {
                if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Identifies the staged content so an unchanged package is not recopied
    /// and a changed one cannot be mistaken for it.
    /// </summary>
    /// <remarks>
    /// Package content is immutable once installed, so the name, size, and
    /// write time of every staged file identify it without reading 50 MB of
    /// content on a path that runs during setup.
    /// </remarks>
    private static string ComputeContentId(
        string modulesDirectory,
        IReadOnlyList<string> packages)
    {
        var builder = new StringBuilder();
        foreach (string package in packages)
        {
            string packageDirectory = Path.Combine(modulesDirectory, package);
            builder.Append(package).Append('\n');
            foreach (string file in Directory
                .EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                builder
                    .Append(Path.GetRelativePath(modulesDirectory, file))
                    .Append('|')
                    .Append(info.Length.ToString(CultureInfo.InvariantCulture))
                    .Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..32];
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(
                directory,
                Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>
    /// Drops roots left by earlier package versions, so upgrades do not
    /// accumulate a mirrored copy each.
    /// </summary>
    private static void RemoveSupersededRoots(string parent, string contentId)
    {
        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            if (string.Equals(
                Path.GetFileName(directory),
                contentId,
                StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A superseded copy still in use by a running agent process is
                // reclaimed by the next setup rather than failing this one.
            }
        }
    }
}
