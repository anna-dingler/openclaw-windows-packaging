using System.Diagnostics;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionToolInstallerTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GuestInstallerCreatesTheCommandShimInItsWorkspace()
    {
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        string requestPath = Path.Combine(workspace, "tools.json");
        File.WriteAllText(
            requestPath,
            SessionRuntimeProtocol.SerializeToolInstallRequest(new SessionToolInstallRequest
            {
                RequestId = "tools1",
                WorkspacePath = workspace
            }));

        int exitCode = SessionToolInstaller.Run(
            requestPath,
            File.ReadAllText,
            File.WriteAllText);

        Assert.Equal(0, exitCode);
        SessionToolInstallResult result = SessionRuntimeProtocol.ReadToolInstallResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(requestPath)));
        Assert.Equal("tools1", result.RequestId);
        Assert.True(File.Exists(result.ShimPath));
        Assert.Equal(
            Path.Combine(workspace, ".openclaw-tools", "openclaw.cmd"),
            result.ShimPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("--max-old-space-size=4096")]
    [InlineData("--require \"C:\\agent data\\a&b\\preload.cjs\"")]
    public async Task CommandShimRestoresNativeRedirectForAgentInvocations(
        string? existingNodeOptions)
    {
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        string requestPath = Path.Combine(workspace, "tools.json");
        File.WriteAllText(
            requestPath,
            SessionRuntimeProtocol.SerializeToolInstallRequest(new SessionToolInstallRequest
            {
                RequestId = "tools1",
                WorkspacePath = workspace
            }));
        Assert.Equal(
            0,
            SessionToolInstaller.Run(
                requestPath,
                File.ReadAllText,
                File.WriteAllText));
        SessionToolInstallResult result = SessionRuntimeProtocol.ReadToolInstallResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(requestPath)));

        string outputPath = Path.Combine(workspace, "environment.txt");
        string fakeNodePath = Path.Combine(workspace, "node.cmd");
        File.WriteAllText(
            fakeNodePath,
            "@echo off\r\n" +
            "setlocal EnableDelayedExpansion\r\n" +
            "> \"%OPENCLAW_TEST_OUTPUT%\" echo %OPENCLAW_NATIVE_APP_ROOT%\r\n" +
            ">> \"%OPENCLAW_TEST_OUTPUT%\" echo %OPENCLAW_NATIVE_STAGED_ROOT%\r\n" +
            ">> \"%OPENCLAW_TEST_OUTPUT%\" echo(!NODE_OPTIONS!\r\n" +
            ">> \"%OPENCLAW_TEST_OUTPUT%\" echo %~1\r\n" +
            ">> \"%OPENCLAW_TEST_OUTPUT%\" echo %~2\r\n" +
            ">> \"%OPENCLAW_TEST_OUTPUT%\" echo %~3\r\n" +
            ">> \"%OPENCLAW_TEST_OUTPUT%\" echo %~4\r\n" +
            "exit /b 0\r\n");

        string applicationDirectory = @"C:\Program Files\WindowsApps\OpenClaw\app";
        string nativeRoot = @"C:\Users\agent\AppData\Local\openclaw\native\abc";
        string nativePreloadUrl =
            "file:///C:/Program%20Files/WindowsApps/OpenClaw/node/native-redirect.mjs";
        IReadOnlyDictionary<string, string> shimEnvironment =
            AgentToolShim.BuildEnvironment(
                fakeNodePath,
                applicationDirectory,
                nativeRoot,
                nativePreloadUrl);
        string commandInterpreter = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = commandInterpreter,
            Arguments = $"/d /c call \"{result.ShimPath}\" doctor",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspace
        };
        startInfo.Environment.Remove(OpenClawRuntimeEnvironment.NativeApplicationRootVariable);
        startInfo.Environment.Remove(OpenClawRuntimeEnvironment.NativeStagedRootVariable);
        if (existingNodeOptions is null)
        {
            startInfo.Environment.Remove(OpenClawRuntimeEnvironment.NodeOptionsVariable);
        }
        else
        {
            startInfo.Environment[OpenClawRuntimeEnvironment.NodeOptionsVariable] =
                existingNodeOptions;
        }
        startInfo.Environment["OPENCLAW_TEST_OUTPUT"] = outputPath;
        foreach ((string name, string value) in shimEnvironment)
        {
            startInfo.Environment[name] = value;
        }

        using Process process = new() { StartInfo = startInfo };
        Assert.True(process.Start());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(
            [
                applicationDirectory,
                nativeRoot,
                existingNodeOptions ?? string.Empty,
                "--import",
                nativePreloadUrl,
                Path.Combine(applicationDirectory, "openclaw.mjs"),
                "doctor"
            ],
            File.ReadAllLines(outputPath));
    }
}
