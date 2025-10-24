namespace Microsoft.Extensions;

/// <summary>
/// Provides methods to locate and retrieve information about .NET SDK installations.
/// This is the primary entry point for the Microsoft.Extensions.DotNetLocator library.
/// </summary>
public static class DotNetLocator
{
    /// <summary>
    /// Gets .NET installation information using the current environment and working directory,
    /// locating the SDK via global.json if present.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A result containing the .NET installation information.</returns>
    public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
        CancellationToken cancellationToken = default)
    {
        return await GetInstallationInfoAsync(Environment.CurrentDirectory, cancellationToken);
    }

    /// <summary>
    /// Gets .NET installation information based on a user-specified probing directory,
    /// honoring global.json if present in the directory hierarchy.
    /// </summary>
    /// <param name="probingDirectory">The directory to start probing from for global.json and SDK resolution.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A result containing the .NET installation information.</returns>
    public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
        string probingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(probingDirectory))
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure("Probing directory cannot be null or empty.");
        }

        if (!Directory.Exists(probingDirectory))
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure($"Probing directory does not exist: {probingDirectory}");
        }

        try
        {
            var implementations = new IDotNetLocator[]
            {
                new DotNetPInvokeLocator(),
                new DotNetManagedLocator(),
                new DotNetProcessLocator()
            };

            DotNetLocationResult<DotNetInstallationInfo>? lastResult = null;
            Exception? lastException = null;

            foreach (var implementation in implementations)
            {
                try
                {
                    var result = await implementation.GetInstallationInfoAsync(probingDirectory, null, cancellationToken);
                    if (result.IsSuccess)
                    {
                        return result;
                    }

                    lastResult = result;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (lastResult != null)
            {
                return lastResult;
            }

            return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                "All .NET locator implementations failed.", lastException);
        }
        catch (Exception ex)
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                $"Failed to get .NET installation info from probing directory '{probingDirectory}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets .NET installation information based on a user-specified .NET root directory
    /// and optional probing directory. The .NET root must contain dotnet.exe and all expected subdirectories.
    /// </summary>
    /// <param name="dotnetRoot">The root directory of the .NET installation (must contain dotnet.exe).</param>
    /// <param name="probingDirectory">Optional directory to start probing from for global.json resolution.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A result containing the .NET installation information.</returns>
    public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
        string dotnetRoot,
        string? probingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dotnetRoot))
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure(".NET root directory cannot be null or empty.");
        }

        if (!Directory.Exists(dotnetRoot))
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure($".NET root directory does not exist: {dotnetRoot}");
        }

        var dotnetExePath = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnetExePath))
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                $"dotnet executable not found in specified root: {dotnetExePath}");
        }

        try
        {
            var implementations = new IDotNetLocator[]
            {
                new DotNetPInvokeLocator(),
                new DotNetManagedLocator(),
                new DotNetProcessLocator()
            };

            DotNetLocationResult<DotNetInstallationInfo>? lastResult = null;
            Exception? lastException = null;

            foreach (var implementation in implementations)
            {
                try
                {
                    var result = await implementation.GetInstallationInfoAsync(
                        probingDirectory ?? Environment.CurrentDirectory,
                        dotnetRoot,
                        cancellationToken);

                    if (result.IsSuccess)
                    {
                        return result;
                    }

                    lastResult = result;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (lastResult != null)
            {
                return lastResult;
            }

            return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                "All .NET locator implementations failed.", lastException);
        }
        catch (Exception ex)
        {
            return DotNetLocationResult<DotNetInstallationInfo>.Failure(
                $"Failed to get .NET installation info from root '{dotnetRoot}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets .NET installation information using the Process wrapper implementation.
    /// This implementation calls 'dotnet --info' and parses its output.
    /// </summary>
    public static class ProcessLocator
    {
        /// <summary>
        /// Gets .NET installation information using the current environment and working directory.
        /// </summary>
        public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
            CancellationToken cancellationToken = default)
        {
            var locator = new DotNetProcessLocator();
            return await locator.GetInstallationInfoAsync(Environment.CurrentDirectory, null, cancellationToken);
        }

        /// <summary>
        /// Gets .NET installation information based on a probing directory.
        /// </summary>
        public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
            string probingDirectory, CancellationToken cancellationToken = default)
        {
            var locator = new DotNetProcessLocator();
            return await locator.GetInstallationInfoAsync(probingDirectory, null, cancellationToken);
        }

        /// <summary>
        /// Gets .NET installation information based on a specific .NET root.
        /// </summary>
        public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
            string dotnetRoot, string? probingDirectory = null, CancellationToken cancellationToken = default)
        {
            var locator = new DotNetProcessLocator();
            return await locator.GetInstallationInfoAsync(probingDirectory ?? Environment.CurrentDirectory, dotnetRoot, cancellationToken);
        }
    }

    /// <summary>
    /// Gets .NET installation information using the managed implementation.
    /// This implementation ports the hostfxr logic to managed code.
    /// </summary>
    public static class ManagedLocator
    {
        /// <summary>
        /// Gets .NET installation information using the current environment and working directory.
        /// </summary>
        public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
            CancellationToken cancellationToken = default)
        {
            var locator = new DotNetManagedLocator();
            return await locator.GetInstallationInfoAsync(Environment.CurrentDirectory, null, cancellationToken);
        }

        /// <summary>
        /// Gets .NET installation information based on a probing directory.
        /// </summary>
        public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
            string probingDirectory, CancellationToken cancellationToken = default)
        {
            var locator = new DotNetManagedLocator();
            return await locator.GetInstallationInfoAsync(probingDirectory, null, cancellationToken);
        }

        /// <summary>
        /// Gets .NET installation information based on a specific .NET root.
        /// </summary>
        public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
            string dotnetRoot, string? probingDirectory = null, CancellationToken cancellationToken = default)
        {
            var locator = new DotNetManagedLocator();
            return await locator.GetInstallationInfoAsync(probingDirectory ?? Environment.CurrentDirectory, dotnetRoot, cancellationToken);
        }
    }

    /// <summary>
    /// Gets .NET installation information using the P/Invoke implementation.
    /// This implementation uses hostfxr_get_dotnet_environment_info and related APIs.
    /// </summary>
    public static class PInvokeLocator
    {
        /// <summary>
        /// Gets .NET installation information using the current environment and working directory.
        /// </summary>
        public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
            CancellationToken cancellationToken = default)
        {
            var locator = new DotNetPInvokeLocator();
            return await locator.GetInstallationInfoAsync(Environment.CurrentDirectory, null, cancellationToken);
        }

        /// <summary>
        /// Gets .NET installation information based on a probing directory.
        /// </summary>
        public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
            string probingDirectory, CancellationToken cancellationToken = default)
        {
            var locator = new DotNetPInvokeLocator();
            return await locator.GetInstallationInfoAsync(probingDirectory, null, cancellationToken);
        }

        /// <summary>
        /// Gets .NET installation information based on a specific .NET root.
        /// </summary>
        public static async Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
            string dotnetRoot, string? probingDirectory = null, CancellationToken cancellationToken = default)
        {
            var locator = new DotNetPInvokeLocator();
            return await locator.GetInstallationInfoAsync(probingDirectory ?? Environment.CurrentDirectory, dotnetRoot, cancellationToken);
        }
    }
}