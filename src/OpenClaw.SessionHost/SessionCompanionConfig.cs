using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

internal sealed record CompanionConfigPatch(CompanionGatewayPatch Gateway);
internal sealed record CompanionGatewayPatch(string Mode, int Port, string Bind, CompanionAuthPatch Auth);
internal sealed record CompanionAuthPatch(string Mode, string Token);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CompanionConfigPatch))]
internal sealed partial class CompanionConfigPatchContext : JsonSerializerContext;

internal static class SessionCompanionConfig
{
    private const string EffectiveGatewayReader = """
        import { resolve } from "node:path";
        import { pathToFileURL } from "node:url";

        const applicationDirectory = process.argv[1];
        if (!applicationDirectory) {
          process.exit(1);
        }

        const configRuntime = pathToFileURL(
          resolve(applicationDirectory, "dist", "plugin-sdk", "config-runtime.js"),
        ).href;
        const { loadConfig } = await import(configRuntime);
        const config = loadConfig({
          pin: false,
          skipPluginValidation: true,
          skipShellEnvFallback: true,
        });
        process.stdout.write(JSON.stringify(config.gateway ?? {}));
        """;

    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile,
        string? profileRoot = null,
        Func<string, bool>? fileExists = null,
        Func<SessionCompanionConfigRequest, string, int>? applyPatch = null,
        Func<SessionCompanionConfigRequest, (int ExitCode, string Output)>? readEffectiveConfiguration = null,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;
        try
        {
            SessionCompanionConfigRequest request =
                SessionCompanionConfigProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId;
            Func<string, string?> environment = readEnvironmentVariable ?? Environment.GetEnvironmentVariable;
            foreach (string name in new[]
            {
                "OPENCLAW_CONFIG_PATH",
                "OPENCLAW_STATE_DIR",
                "OPENCLAW_GATEWAY_URL",
                "OPENCLAW_GATEWAY_PORT",
                "OPENCLAW_GATEWAY_TOKEN"
            })
            {
                if (!string.IsNullOrWhiteSpace(environment(name)))
                {
                    throw new SessionLaunchException(
                        $"The agent account has a {name} override. Remove it before configuring " +
                        "Companion so the package and Companion use the same Gateway.");
                }
            }
            string configPath = Path.Combine(profileRoot ?? AgentProfile.GetPath(), ".openclaw", "openclaw.json");
            SessionCompanionConfigResult result = Configure(
                request, configPath, readFile, writeFile, fileExists ?? File.Exists,
                applyPatch ?? ApplyPatch, readEffectiveConfiguration ?? ReadEffectiveConfiguration);
            writeFile(resultPath, SessionCompanionConfigProtocol.SerializeResult(result));
            return 0;
        }
        catch (Exception exception) when (exception is SessionLaunchException or IOException or
            UnauthorizedAccessException or ArgumentException or JsonException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            writeFile(resultPath, SessionCompanionConfigProtocol.SerializeResult(
                new SessionCompanionConfigResult { RequestId = requestId, Error = exception.Message }));
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }

    internal static SessionCompanionConfigResult Configure(
        SessionCompanionConfigRequest request,
        string configPath,
        Func<string, string> readFile,
        Action<string, string> writeFile,
        Func<string, bool> fileExists,
        Func<SessionCompanionConfigRequest, string, int> applyPatch,
        Func<SessionCompanionConfigRequest, (int ExitCode, string Output)> readEffectiveConfiguration)
    {
        if (request.CheckOnly)
        {
            if (!fileExists(configPath))
            {
                throw new SessionLaunchException(
                    "The agent's Gateway configuration is missing. Run `clawctl companion prepare` first.");
            }

            (int? effectivePort, string? effectiveToken) =
                ReadEffectiveConfiguration(request, readEffectiveConfiguration);
            if (effectivePort is null || string.IsNullOrWhiteSpace(effectiveToken))
            {
                throw new SessionLaunchException(
                    "The agent's Gateway configuration has no usable port or token. Run `clawctl companion prepare` first.");
            }

            return new SessionCompanionConfigResult
            {
                RequestId = request.RequestId,
                Port = effectivePort.Value,
                Token = effectiveToken
            };
        }

        (int? existingPort, string? existingToken) = fileExists(configPath)
            ? ReadEffectiveConfiguration(request, readEffectiveConfiguration)
            : (null, null);
        if (existingPort is not null && !string.IsNullOrWhiteSpace(existingToken))
        {
            return new SessionCompanionConfigResult
            {
                RequestId = request.RequestId,
                Port = existingPort.Value,
                Token = existingToken
            };
        }

        int port = existingPort ?? request.Port;
        string token = existingToken ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string patchPath = Path.Combine(
            Path.GetDirectoryName(configPath)!,
            ".companion-config-" + Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        try
        {
            writeFile(patchPath, JsonSerializer.Serialize(
                new CompanionConfigPatch(new CompanionGatewayPatch(
                    "local", port, "loopback", new CompanionAuthPatch("token", token))),
                CompanionConfigPatchContext.Default.CompanionConfigPatch));
            int exitCode = applyPatch(request, patchPath);
            if (exitCode != 0)
            {
                throw new SessionLaunchException(
                    $"OpenClaw rejected the Companion Gateway configuration (exit code {exitCode}). " +
                    "Inspect it with `clawctl pwsh` and `openclaw config validate`.");
            }
        }
        finally
        {
            File.Delete(patchPath);
        }

        if (!fileExists(configPath))
        {
            throw new SessionLaunchException("OpenClaw did not create the agent's Gateway configuration.");
        }
        (int? configuredPort, string? configuredToken) =
            ReadEffectiveConfiguration(request, readEffectiveConfiguration);
        if (configuredPort != port || configuredToken != token)
        {
            throw new SessionLaunchException(
                "The agent's Gateway configuration changed while Companion prepared it. Retry setup.");
        }
        return new SessionCompanionConfigResult { RequestId = request.RequestId, Port = port, Token = token };
    }

    private static (int? Port, string? Token) ReadEffectiveConfiguration(
        SessionCompanionConfigRequest request,
        Func<SessionCompanionConfigRequest, (int ExitCode, string Output)> readEffectiveConfiguration)
    {
        (int exitCode, string output) = readEffectiveConfiguration(request);
        if (exitCode != 0)
        {
            if (IsGatewayUnset(output))
            {
                return (null, null);
            }

            throw new SessionLaunchException(
                $"OpenClaw could not read the effective Gateway configuration (exit code {exitCode}). " +
                "Inspect it with `clawctl pwsh` and `openclaw config validate`.");
        }

        return ReadGatewayConfiguration(output);
    }

    private static bool IsGatewayUnset(string output)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            return document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("message", out JsonElement message) &&
                message.ValueKind == JsonValueKind.String &&
                message.GetString()!.StartsWith("Config path is valid but unset: gateway.", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (int? Port, string? Token) ReadGatewayConfiguration(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SessionLaunchException("The agent's effective Gateway configuration is not an object.");
        }
        JsonElement gateway = root;
        foreach ((string property, string expected) in new[] { ("mode", "local"), ("bind", "loopback") })
        {
            if (gateway.TryGetProperty(property, out JsonElement value) &&
                (value.ValueKind != JsonValueKind.String || value.GetString() != expected))
            {
                throw new SessionLaunchException(
                    $"The agent's existing Gateway {property} is not {expected}. Reconfigure it explicitly.");
            }
        }
        int? port = null;
        if (gateway.TryGetProperty("port", out JsonElement configuredPort))
        {
            if (!configuredPort.TryGetInt32(out int value) || value is < 1 or > 65535)
            {
                throw new SessionLaunchException("The agent's existing Gateway port is invalid.");
            }
            port = value;
        }
        if (!gateway.TryGetProperty("auth", out JsonElement auth))
        {
            return (port, null);
        }
        if (auth.ValueKind != JsonValueKind.Object)
        {
            throw new SessionLaunchException("The agent's Gateway authentication is not an object.");
        }
        if (auth.TryGetProperty("mode", out JsonElement authMode) &&
            (authMode.ValueKind != JsonValueKind.String || authMode.GetString() != "token"))
        {
            throw new SessionLaunchException("The agent's Gateway uses a different authentication mode.");
        }
        if (!auth.TryGetProperty("token", out JsonElement authToken))
        {
            return (port, null);
        }
        if (authToken.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(authToken.GetString()) ||
            authToken.GetString()!.Contains("${", StringComparison.Ordinal) ||
            authToken.GetString() == "__OPENCLAW_REDACTED__")
        {
            throw new SessionLaunchException(
                "OpenClaw did not expose the existing Gateway token to Companion. " +
                "The existing configuration was not changed.");
        }
        return (port, authToken.GetString());
    }

    private static int ApplyPatch(SessionCompanionConfigRequest request, string patchPath)
        => RunOpenClaw(request, Path.GetDirectoryName(patchPath)!,
            ["config", "patch", "--file", patchPath], null).ExitCode;

    private static (int ExitCode, string Output) ReadEffectiveConfiguration(
        SessionCompanionConfigRequest request)
        => RunNode(
            request,
            AppContext.BaseDirectory,
            null,
            ["--input-type=module", "--eval", EffectiveGatewayReader, request.ApplicationDirectory!],
            null);

    private static (int ExitCode, string Output) RunOpenClaw(
        SessionCompanionConfigRequest request,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? overrides)
        => RunNode(
            request,
            workingDirectory,
            Path.Combine(request.ApplicationDirectory!, "openclaw.mjs"),
            arguments,
            overrides);

    private static (int ExitCode, string Output) RunNode(
        SessionCompanionConfigRequest request,
        string workingDirectory,
        string? scriptPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? overrides)
    {
        using FileStream? lease = SessionNativeStager.OpenConsumerLease(request.NativeRootPath);
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(request.NodePath!)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        ProcessStartInfo start = process.StartInfo;
        if (request.NativeRootPath is not null)
        {
            start.ArgumentList.Add("--import");
            start.ArgumentList.Add(new Uri(request.PreloadPath!).AbsoluteUri);
        }
        if (scriptPath is not null)
        {
            start.ArgumentList.Add(scriptPath);
        }
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach ((string name, string value) in request.Environment!)
        {
            start.Environment[name] = value;
        }
        if (overrides is not null)
        {
            start.Environment.Remove("OPENCLAW_CONFIG_PATH");
            start.Environment.Remove("OPENCLAW_STATE_DIR");
            start.Environment.Remove("OPENCLAW_GATEWAY_URL");
            foreach ((string name, string value) in overrides)
            {
                start.Environment[name] = value;
            }
        }
        start.Environment["PATH"] = Path.GetDirectoryName(request.NodePath) +
            Path.PathSeparator + start.Environment["PATH"];
        if (!process.Start())
        {
            throw new SessionLaunchException("The packaged OpenClaw command could not be started.");
        }
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new SessionLaunchException(
                "The packaged OpenClaw command did not finish within two minutes. Retry Companion setup.");
        }
        string stdout = output.GetAwaiter().GetResult();
        _ = error.GetAwaiter().GetResult();
        return (process.ExitCode, stdout);
    }
}
