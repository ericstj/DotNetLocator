using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Microsoft.Extensions;

/// <summary>
/// Implementation of .NET locator that uses P/Invoke to call hostfxr APIs directly.
/// </summary>
internal sealed class DotNetPInvokeLocator : IDotNetLocator
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
            var resolvedDotNetRoot = dotnetRoot ?? await DiscoverDotNetRootAsync();
            if (string.IsNullOrEmpty(resolvedDotNetRoot))
            {
                return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                    "Could not locate .NET installation root.");
            }

            var hostfxrPath = FindHostfxrLibrary(resolvedDotNetRoot);
            if (string.IsNullOrEmpty(hostfxrPath))
            {
                return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                    "Could not locate hostfxr library.");
            }

            var environmentInfo = await GetDotNetEnvironmentInfoAsync(hostfxrPath, resolvedDotNetRoot);
            if (environmentInfo == null)
            {
                return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                    "Failed to retrieve .NET environment information from hostfxr.");
            }

            var globalJsonInfo = await FindGlobalJsonInfoAsync(probingDirectory);
            var installationInfo = ConvertToInstallationInfo(environmentInfo, resolvedDotNetRoot, globalJsonInfo);

            return DotNetLocationResult<DotNetInstallationInfo>.Success(installationInfo);
        }
        catch (Exception ex)
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                $"Failed to get .NET installation info via P/Invoke: {ex.Message}", ex);
        }
    }

    private static async Task<string?> DiscoverDotNetRootAsync()
    {
        // Try environment variable first
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot))
        {
            return dotnetRoot;
        }

        // Try to find dotnet executable and derive root from it
        var dotnetExecutable = await FindDotNetExecutableInPathAsync();
        if (!string.IsNullOrEmpty(dotnetExecutable))
        {
            return Path.GetDirectoryName(dotnetExecutable);
        }

        // Try default installation paths
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var defaultPath = Path.Combine(programFiles, "dotnet");
            if (Directory.Exists(defaultPath))
                return defaultPath;
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var defaultPaths = new[] { "/usr/share/dotnet", "/usr/local/share/dotnet" };
            foreach (var path in defaultPaths)
            {
                if (Directory.Exists(path))
                    return path;
            }
        }

        return null;
    }

    private static Task<string?> FindDotNetExecutableInPathAsync()
    {
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

    private static string? FindHostfxrLibrary(string dotnetRoot)
    {
        var hostDirectory = Path.Combine(dotnetRoot, "host", "fxr");
        if (!Directory.Exists(hostDirectory))
            return null;

        var libraryName = OperatingSystem.IsWindows() ? "hostfxr.dll" :
                         OperatingSystem.IsMacOS() ? "libhostfxr.dylib" : "libhostfxr.so";

        // Find the latest version directory using proper semantic version sorting
        var versionDirectories = Directory.GetDirectories(hostDirectory)
                                         .Select(d => new DirectoryInfo(d).Name)
                                         .Where(name => VersionRegex.IsMatch(name))
                                         .OrderByDescending(v => ParseVersion(v))
                                         .Select(name => Path.Combine(hostDirectory, name))
                                         .ToArray();

        foreach (var versionDir in versionDirectories)
        {
            var libraryPath = Path.Combine(versionDir, libraryName);
            if (File.Exists(libraryPath))
                return libraryPath;
        }

        return null;
    }

    private static Task<DotNetEnvironmentInfo?> GetDotNetEnvironmentInfoAsync(string hostfxrPath, string dotnetRoot)
    {
        var handle = NativeLibrary.Load(hostfxrPath);
        if (handle == IntPtr.Zero)
            return Task.FromResult<DotNetEnvironmentInfo?>(null);

        try
        {
            var getFunctionPtr = NativeLibrary.GetExport(handle, "hostfxr_get_dotnet_environment_info");
            if (getFunctionPtr == IntPtr.Zero)
                return Task.FromResult<DotNetEnvironmentInfo?>(null);

            var getFunction = Marshal.GetDelegateForFunctionPointer<hostfxr_get_dotnet_environment_info_fn>(getFunctionPtr);

            DotNetEnvironmentInfo? result = null;
            unsafe
            {
                var resultCallback = new hostfxr_get_dotnet_environment_info_result_fn((info, context) =>
                {
                    result = MarshalEnvironmentInfo(info);
                });

                var rootPtr = Marshal.StringToHGlobalUni(dotnetRoot);
                try
                {
                    var returnCode = getFunction(rootPtr, IntPtr.Zero, resultCallback, IntPtr.Zero);
                    if (returnCode == 0)
                        return Task.FromResult<DotNetEnvironmentInfo?>(result);
                }
                finally
                {
                    Marshal.FreeHGlobal(rootPtr);
                }
            }
        }
        finally
        {
            NativeLibrary.Free(handle);
        }

        return Task.FromResult<DotNetEnvironmentInfo?>(null);
    }

    private static unsafe DotNetEnvironmentInfo MarshalEnvironmentInfo(hostfxr_dotnet_environment_info* info)
    {
        var result = new DotNetEnvironmentInfo
        {
            HostfxrVersion = Marshal.PtrToStringUni((IntPtr)info->hostfxr_version) ?? string.Empty,
            HostfxrCommitHash = Marshal.PtrToStringUni((IntPtr)info->hostfxr_commit_hash) ?? string.Empty,
            Sdks = new List<DotNetEnvironmentSdkInfo>(),
            Frameworks = new List<DotNetEnvironmentFrameworkInfo>()
        };

        // Marshal SDKs
        for (int i = 0; i < (int)info->sdk_count; i++)
        {
            var sdkPtr = info->sdks + i;
            var sdk = new DotNetEnvironmentSdkInfo
            {
                Version = Marshal.PtrToStringUni((IntPtr)sdkPtr->version) ?? string.Empty,
                Path = Marshal.PtrToStringUni((IntPtr)sdkPtr->path) ?? string.Empty
            };
            ((List<DotNetEnvironmentSdkInfo>)result.Sdks).Add(sdk);
        }

        // Marshal Frameworks
        for (int i = 0; i < (int)info->framework_count; i++)
        {
            var frameworkPtr = info->frameworks + i;
            var framework = new DotNetEnvironmentFrameworkInfo
            {
                Name = Marshal.PtrToStringUni((IntPtr)frameworkPtr->name) ?? string.Empty,
                Version = Marshal.PtrToStringUni((IntPtr)frameworkPtr->version) ?? string.Empty,
                Path = Marshal.PtrToStringUni((IntPtr)frameworkPtr->path) ?? string.Empty
            };
            ((List<DotNetEnvironmentFrameworkInfo>)result.Frameworks).Add(framework);
        }

        return result;
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

    private static DotNetInstallationInfo ConvertToInstallationInfo(
        DotNetEnvironmentInfo environmentInfo,
        string dotnetRoot,
        (string? Path, string? Version) globalJsonInfo)
    {
        var hostExecutablePath = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        var host = new DotNetHostInfo
        {
            Version = environmentInfo.HostfxrVersion,
            Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            CommitHash = environmentInfo.HostfxrCommitHash,
            Path = hostExecutablePath
        };

        var runtimeEnvironment = new DotNetRuntimeEnvironmentInfo
        {
            OSDescription = RuntimeInformation.OSDescription,
            RID = RuntimeInformation.RuntimeIdentifier,
            BasePath = dotnetRoot,
            Properties = new Dictionary<string, string>()
        };

        var sdks = environmentInfo.Sdks.Select(s => new DotNetSdkInfo
        {
            Version = s.Version,
            Path = s.Path
        }).OrderByDescending(s => ParseVersion(s.Version)).ToArray();

        var frameworks = environmentInfo.Frameworks.Select(f => new DotNetFrameworkInfo
        {
            Name = f.Name,
            Version = f.Version,
            Path = f.Path
        }).ToArray();

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

    #region P/Invoke Declarations

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void hostfxr_get_dotnet_environment_info_result_fn(
        hostfxr_dotnet_environment_info* info,
        IntPtr result_context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int hostfxr_get_dotnet_environment_info_fn(
        IntPtr dotnet_root,
        IntPtr reserved,
        hostfxr_get_dotnet_environment_info_result_fn result,
        IntPtr result_context);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct hostfxr_dotnet_environment_sdk_info
    {
        public UIntPtr size;
        public char* version;
        public char* path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct hostfxr_dotnet_environment_framework_info
    {
        public UIntPtr size;
        public char* name;
        public char* version;
        public char* path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct hostfxr_dotnet_environment_info
    {
        public UIntPtr size;
        public char* hostfxr_version;
        public char* hostfxr_commit_hash;
        public UIntPtr sdk_count;
        public hostfxr_dotnet_environment_sdk_info* sdks;
        public UIntPtr framework_count;
        public hostfxr_dotnet_environment_framework_info* frameworks;
    }

    #endregion

    #region Helper Classes

    private class DotNetEnvironmentInfo
    {
        public string HostfxrVersion { get; set; } = string.Empty;
        public string HostfxrCommitHash { get; set; } = string.Empty;
        public IList<DotNetEnvironmentSdkInfo> Sdks { get; set; } = new List<DotNetEnvironmentSdkInfo>();
        public IList<DotNetEnvironmentFrameworkInfo> Frameworks { get; set; } = new List<DotNetEnvironmentFrameworkInfo>();
    }

    private class DotNetEnvironmentSdkInfo
    {
        public string Version { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    private class DotNetEnvironmentFrameworkInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    #endregion
}