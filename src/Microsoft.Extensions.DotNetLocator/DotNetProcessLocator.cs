using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Microsoft.Extensions;

/// <summary>
/// Implementation of .NET locator that wraps calls to 'dotnet --info' and parses the output.
/// </summary>
internal sealed class DotNetProcessLocator : IDotNetLocator
{
    /// <inheritdoc />
    public async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
        string probingDirectory,
        string? dotnetRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var dotnetExecutable = await FindDotNetExecutableAsync(dotnetRoot);
            if (dotnetExecutable == null)
            {
                return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                    "Could not locate dotnet executable.");
            }

            var infoOutput = await RunDotNetInfoAsync(dotnetExecutable, probingDirectory, cancellationToken);
            if (string.IsNullOrEmpty(infoOutput))
            {
                return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                    "dotnet --info returned empty output.");
            }

            var globalJsonInfo = await FindGlobalJsonInfoAsync(probingDirectory);
            var installationInfo = ParseDotNetInfoOutput(infoOutput, dotnetRoot ?? Path.GetDirectoryName(dotnetExecutable)!, globalJsonInfo);

            return DotNetLocationResult<DotNetInstallationInfo>.Success(installationInfo);
        }
        catch (Exception ex)
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                $"Failed to get .NET installation info: {ex.Message}", ex);
        }
    }

    private static Task<string?> FindDotNetExecutableAsync(string? dotnetRoot)
    {
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var explicitPath = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(explicitPath))
                return Task.FromResult<string?>(explicitPath);
        }

        // Try to find dotnet in PATH
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
            return Task.FromResult<string?>(null);

        var executableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var paths = pathVariable.Split(Path.PathSeparator);

        foreach (var path in paths)
        {
            try
            {
                var fullPath = Path.Combine(path, executableName);
                if (File.Exists(fullPath))
                    return Task.FromResult<string?>(fullPath);
            }
            catch
            {
                // Ignore invalid paths
            }
        }

        return Task.FromResult<string?>(null);
    }

    private static async Task<string> RunDotNetInfoAsync(
        string dotnetExecutable,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnetExecutable,
            Arguments = "--info",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet --info failed with exit code {process.ExitCode}. Error: {error}");
        }

        return output;
    }

    private static async Task<(string? Path, string? Version)> FindGlobalJsonInfoAsync(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory != null)
        {
            var globalJsonPath = Path.Combine(directory.FullName, "global.json");
            if (File.Exists(globalJsonPath))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(globalJsonPath);
                    var doc = JsonDocument.Parse(content);
                    
                    if (doc.RootElement.TryGetProperty("sdk", out var sdkElement) &&
                        sdkElement.TryGetProperty("version", out var versionElement))
                    {
                        return (globalJsonPath, versionElement.GetString());
                    }

                    return (globalJsonPath, null);
                }
                catch
                {
                    // Ignore JSON parsing errors and continue searching
                }
            }

            directory = directory.Parent;
        }

        return (null, null);
    }

    private static DotNetInstallationInfo ParseDotNetInfoOutput(
        string output,
        string dotnetRoot,
        (string? Path, string? Version) globalJsonInfo)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(line => line.Trim())
                          .ToArray();

        var host = ParseHostInfo(lines, dotnetRoot);
        var runtimeEnvironment = ParseRuntimeEnvironmentInfo(lines, dotnetRoot);
        var sdks = ParseSdkInfo(lines);
        var frameworks = ParseFrameworkInfo(lines);

        return new DotNetInstallationInfo
        {
            Host = host,
            RuntimeEnvironment = runtimeEnvironment,
            Sdks = sdks,
            Frameworks = frameworks,
            GlobalJsonPath = globalJsonInfo.Path,
            GlobalJsonSdkVersion = globalJsonInfo.Version,
            DotNetRoot = dotnetRoot
        };
    }

    private static DotNetHostInfo ParseHostInfo(string[] lines, string dotnetRoot)
    {
        var versionPattern = new Regex(@"Version:\s*(.+)");
        var archPattern = new Regex(@"Architecture:\s*(.+)");
        var commitPattern = new Regex(@"Commit:\s*(.+)");

        string version = "Unknown";
        string architecture = "Unknown";
        string? commitHash = null;

        bool inHostSection = false;
        foreach (var line in lines)
        {
            if (line.StartsWith(".NET Host", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Host", StringComparison.OrdinalIgnoreCase))
            {
                inHostSection = true;
                continue;
            }

            if (inHostSection)
            {
                if (string.IsNullOrWhiteSpace(line) || 
                    line.StartsWith(".NET SDK", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Runtime Environment", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var versionMatch = versionPattern.Match(line);
                if (versionMatch.Success)
                {
                    version = versionMatch.Groups[1].Value.Trim();
                    continue;
                }

                var archMatch = archPattern.Match(line);
                if (archMatch.Success)
                {
                    architecture = archMatch.Groups[1].Value.Trim();
                    continue;
                }

                var commitMatch = commitPattern.Match(line);
                if (commitMatch.Success)
                {
                    commitHash = commitMatch.Groups[1].Value.Trim();
                    continue;
                }
            }
        }

        var hostPath = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        return new DotNetHostInfo
        {
            Version = version,
            Architecture = architecture,
            CommitHash = commitHash,
            Path = hostPath
        };
    }

    private static DotNetRuntimeEnvironmentInfo ParseRuntimeEnvironmentInfo(string[] lines, string dotnetRoot)
    {
        var osPattern = new Regex(@"OS Name:\s*(.+)");
        var osVersionPattern = new Regex(@"OS Version:\s*(.+)");
        var ridPattern = new Regex(@"RID:\s*(.+)");
        var basePathPattern = new Regex(@"Base Path:\s*(.+)");

        string osDescription = "Unknown";
        string rid = "Unknown";
        string basePath = dotnetRoot;
        var properties = new Dictionary<string, string>();

        bool inRuntimeSection = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("Runtime Environment", StringComparison.OrdinalIgnoreCase))
            {
                inRuntimeSection = true;
                continue;
            }

            if (inRuntimeSection)
            {
                if (string.IsNullOrWhiteSpace(line) || 
                    line.StartsWith(".NET", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var osMatch = osPattern.Match(line);
                if (osMatch.Success)
                {
                    osDescription = osMatch.Groups[1].Value.Trim();
                    continue;
                }

                var osVersionMatch = osVersionPattern.Match(line);
                if (osVersionMatch.Success)
                {
                    osDescription += " " + osVersionMatch.Groups[1].Value.Trim();
                    continue;
                }

                var ridMatch = ridPattern.Match(line);
                if (ridMatch.Success)
                {
                    rid = ridMatch.Groups[1].Value.Trim();
                    continue;
                }

                var basePathMatch = basePathPattern.Match(line);
                if (basePathMatch.Success)
                {
                    basePath = basePathMatch.Groups[1].Value.Trim();
                    continue;
                }

                // Parse other properties
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    var key = line.Substring(0, colonIndex).Trim();
                    var value = line.Substring(colonIndex + 1).Trim();
                    properties[key] = value;
                }
            }
        }

        return new DotNetRuntimeEnvironmentInfo
        {
            OSDescription = osDescription,
            RID = rid,
            BasePath = basePath,
            Properties = properties
        };
    }

    private static IReadOnlyList<DotNetSdkInfo> ParseSdkInfo(string[] lines)
    {
        var sdks = new List<DotNetSdkInfo>();
        var versionPathPattern = new Regex(@"(.+?)\s+\[(.+?)\]");

        bool inSdkSection = false;
        foreach (var line in lines)
        {
            if (line.StartsWith(".NET SDK", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("SDK installed", StringComparison.OrdinalIgnoreCase))
            {
                inSdkSection = true;
                continue;
            }

            if (inSdkSection)
            {
                if (string.IsNullOrWhiteSpace(line) || 
                    line.StartsWith("Runtime Environment", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith(".NET Runtime", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var match = versionPathPattern.Match(line);
                if (match.Success)
                {
                    var version = match.Groups[1].Value.Trim();
                    var path = match.Groups[2].Value.Trim();

                    sdks.Add(new DotNetSdkInfo
                    {
                        Version = version,
                        Path = path
                    });
                }
            }
        }

        return sdks;
    }

    private static IReadOnlyList<DotNetFrameworkInfo> ParseFrameworkInfo(string[] lines)
    {
        var frameworks = new List<DotNetFrameworkInfo>();
        var versionPathPattern = new Regex(@"(.+?)\s+(.+?)\s+\[(.+?)\]");

        bool inRuntimeSection = false;
        foreach (var line in lines)
        {
            if (line.StartsWith(".NET Runtime", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Runtime installed", StringComparison.OrdinalIgnoreCase))
            {
                inRuntimeSection = true;
                continue;
            }

            if (inRuntimeSection)
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Download", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                var match = versionPathPattern.Match(line);
                if (match.Success)
                {
                    var name = match.Groups[1].Value.Trim();
                    var version = match.Groups[2].Value.Trim();
                    var path = match.Groups[3].Value.Trim();

                    frameworks.Add(new DotNetFrameworkInfo
                    {
                        Name = name,
                        Version = version,
                        Path = path
                    });
                }
            }
        }

        return frameworks;
    }
}