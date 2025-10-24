using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Microsoft.Extensions;

/// <summary>
/// Implementation of .NET locator that ports the hostfxr logic to managed code.
/// This implementation replicates the SDK and framework discovery logic without P/Invoke.
/// </summary>
internal sealed class DotNetManagedLocator : IDotNetLocator
{
    private static readonly Regex VersionRegex = new(@"^(\d+)\.(\d+)\.(\d+)(?:-([a-zA-Z0-9\-\.]+))?$", RegexOptions.Compiled);

    /// <inheritdoc />
    public async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
        string probingDirectory,
        string? dotnetRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolvedDotNetRoot = dotnetRoot ?? await DiscoverDotNetRootAsync(cancellationToken);
            if (string.IsNullOrEmpty(resolvedDotNetRoot))
            {
                return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                    "Could not locate .NET installation root.");
            }

            var globalJsonInfo = await FindGlobalJsonInfoAsync(probingDirectory);
            var hostInfo = await GetHostInfoAsync(resolvedDotNetRoot);
            var runtimeEnvironment = GetRuntimeEnvironmentInfo(resolvedDotNetRoot);
            var sdks = await GetInstalledSdksAsync(resolvedDotNetRoot);
            var frameworks = await GetInstalledFrameworksAsync(resolvedDotNetRoot);

            var installationInfo = new DotNetInstallationInfo
            {
                Host = hostInfo,
                RuntimeEnvironment = runtimeEnvironment,
                Sdks = sdks,
                Frameworks = frameworks,
                GlobalJsonPath = globalJsonInfo.Path,
                GlobalJsonSdkVersion = globalJsonInfo.Version,
                DotNetRoot = resolvedDotNetRoot
            };

            return DotNetLocationResult<DotNetInstallationInfo>.Success(installationInfo);
        }
        catch (Exception ex)
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                $"Failed to get .NET installation info via managed implementation: {ex.Message}", ex);
        }
    }

    private static async Task<string?> DiscoverDotNetRootAsync(CancellationToken cancellationToken)
    {
        // 1. Try DOTNET_ROOT environment variable
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot))
        {
            return dotnetRoot;
        }

        // 2. Try to find dotnet executable in PATH and derive root
        var dotnetExecutable = await DotNetPathUtilities.LocateDotNetExecutableAsync(cancellationToken);
        if (!string.IsNullOrEmpty(dotnetExecutable))
        {
            return Path.GetDirectoryName(dotnetExecutable);
        }

        // 3. Try default installation locations
        var defaultPaths = GetDefaultInstallationPaths();
        foreach (var path in defaultPaths)
        {
            if (Directory.Exists(path))
            {
                var dotnetExe = Path.Combine(path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (File.Exists(dotnetExe))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetDefaultInstallationPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(programFiles, "dotnet");

            // Try Program Files (x86) as well
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "dotnet");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/share/dotnet";
            yield return "/usr/local/share/dotnet";
            yield return "/opt/dotnet";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/local/share/dotnet";
            yield return "/usr/local/dotnet";
        }
    }
    private static Task<DotNetHostInfo> GetHostInfoAsync(string dotnetRoot)
    {
        var hostExecutablePath = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        
        // Try to get version from the host directory structure
        var hostDirectory = Path.Combine(dotnetRoot, "host", "fxr");
        var version = "Unknown";
        var commitHash = (string?)null;

        if (Directory.Exists(hostDirectory))
        {
            // Find the latest hostfxr version
            var versionDirectories = Directory.GetDirectories(hostDirectory)
                                             .Select(d => new DirectoryInfo(d).Name)
                                             .Where(name => VersionRegex.IsMatch(name))
                                             .OrderByDescending(v => ParseVersion(v))
                                             .ToArray();

            if (versionDirectories.Any())
            {
                version = versionDirectories.First();
            }
        }

        var result = new DotNetHostInfo
        {
            Version = version,
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            CommitHash = commitHash,
            Path = hostExecutablePath
        };

        return Task.FromResult(result);
    }

    private static DotNetRuntimeEnvironmentInfo GetRuntimeEnvironmentInfo(string dotnetRoot)
    {
        return new DotNetRuntimeEnvironmentInfo
        {
            OSDescription = RuntimeInformation.OSDescription,
            RID = RuntimeInformation.RuntimeIdentifier,
            BasePath = dotnetRoot,
            Properties = new Dictionary<string, string>
            {
                ["DOTNET_ROOT"] = dotnetRoot,
                ["Architecture"] = RuntimeInformation.OSArchitecture.ToString(),
                ["ProcessArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString()
            }
        };
    }

    private static Task<IReadOnlyList<DotNetSdkInfo>> GetInstalledSdksAsync(string dotnetRoot)
    {
        var sdkDirectory = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkDirectory))
            return Task.FromResult<IReadOnlyList<DotNetSdkInfo>>(Array.Empty<DotNetSdkInfo>());

        var sdks = new List<DotNetSdkInfo>();

        foreach (var versionDir in Directory.GetDirectories(sdkDirectory))
        {
            var versionName = new DirectoryInfo(versionDir).Name;

            if (!VersionRegex.IsMatch(versionName))
                continue;

            if (IsValidSdkDirectory(versionDir))
            {
                sdks.Add(new DotNetSdkInfo
                {
                    Version = versionName,
                    Path = versionDir
                });
            }
        }

        var result = sdks.OrderByDescending(s => ParseVersion(s.Version)).ToArray();
        return Task.FromResult<IReadOnlyList<DotNetSdkInfo>>(result);
    }

    private static bool IsValidSdkDirectory(string sdkPath)
    {
        var requiredFiles = new[]
        {
            "Microsoft.Common.CurrentVersion.targets",
            "Microsoft.NET.Build.Extensions.targets"
        };

        return requiredFiles.Any(file => File.Exists(Path.Combine(sdkPath, file))) ||
               Directory.Exists(Path.Combine(sdkPath, "Sdks"));
    }

    private static Task<IReadOnlyList<DotNetFrameworkInfo>> GetInstalledFrameworksAsync(string dotnetRoot)
    {
        var sharedDirectory = Path.Combine(dotnetRoot, "shared");
        if (!Directory.Exists(sharedDirectory))
            return Task.FromResult<IReadOnlyList<DotNetFrameworkInfo>>(Array.Empty<DotNetFrameworkInfo>());

        var frameworks = new List<DotNetFrameworkInfo>();

        foreach (var frameworkDir in Directory.GetDirectories(sharedDirectory))
        {
            var frameworkName = new DirectoryInfo(frameworkDir).Name;

            foreach (var versionDir in Directory.GetDirectories(frameworkDir))
            {
                var versionName = new DirectoryInfo(versionDir).Name;

                // Skip non-version directories
                if (!VersionRegex.IsMatch(versionName))
                    continue;

                // Verify this is a valid framework directory
                if (IsValidFrameworkDirectory(versionDir))
                {
                    frameworks.Add(new DotNetFrameworkInfo
                    {
                        Name = frameworkName,
                        Version = versionName,
                        Path = versionDir
                    });
                }
            }
        }

        // Sort by name, then by version in descending order
        var result = frameworks.OrderBy(f => f.Name)
                              .ThenByDescending(f => ParseVersion(f.Version))
                              .ToArray();
        return Task.FromResult<IReadOnlyList<DotNetFrameworkInfo>>(result);
    }

    private static bool IsValidFrameworkDirectory(string frameworkPath)
    {
        // Check for essential framework files
        var requiredFiles = new[]
        {
            $"{Path.GetFileName(Path.GetDirectoryName(frameworkPath))}.dll",
            $"{Path.GetFileName(Path.GetDirectoryName(frameworkPath))}.deps.json"
        };

        return requiredFiles.Any(file => File.Exists(Path.Combine(frameworkPath, file))) ||
               Directory.GetFiles(frameworkPath, "*.dll").Any();
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

    private static Version ParseVersion(string versionString)
    {
        var match = VersionRegex.Match(versionString);
        if (!match.Success)
            return new Version(0, 0, 0);

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = int.Parse(match.Groups[3].Value);
        
        // For prerelease versions, we use the 4th component to differentiate
        // This is a simple approach - we could make it more sophisticated
        var prerelease = match.Groups[4].Value;
        var revision = 0;
        
        if (!string.IsNullOrEmpty(prerelease))
        {
            // Simple heuristic: common prerelease order is alpha < beta < rc < (release)
            // We assign higher numbers to later stages, so rc > beta > alpha
            if (prerelease.StartsWith("alpha", StringComparison.OrdinalIgnoreCase))
                revision = 1000;
            else if (prerelease.StartsWith("beta", StringComparison.OrdinalIgnoreCase))
                revision = 2000;
            else if (prerelease.StartsWith("rc", StringComparison.OrdinalIgnoreCase))
                revision = 3000;
            else if (prerelease.StartsWith("preview", StringComparison.OrdinalIgnoreCase))
                revision = 1500; // Between alpha and beta
            else
                revision = 500; // Other prerelease types get lowest priority
        }
        else
        {
            // Release version gets highest priority
            revision = 9999;
        }

        return new Version(major, minor, patch, revision);
    }
}