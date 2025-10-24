using System.Text.Json.Serialization;

namespace Microsoft.Extensions;

/// <summary>
/// Represents information about a .NET SDK installation.
/// </summary>
public sealed class DotNetSdkInfo
{
    /// <summary>
    /// Gets the version of the SDK.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the full path to the SDK installation directory.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the commit hash of the SDK build, if available.
    /// </summary>
    public string? CommitHash { get; init; }

    /// <summary>
    /// Returns a string representation of the SDK information.
    /// </summary>
    public override string ToString() => $"{Version} [{Path}]";
}

/// <summary>
/// Represents information about a .NET runtime framework installation.
/// </summary>
public sealed class DotNetFrameworkInfo
{
    /// <summary>
    /// Gets the name of the framework (e.g., "Microsoft.NETCore.App", "Microsoft.AspNetCore.App").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the version of the framework.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the full path to the framework installation directory.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the commit hash of the framework build, if available.
    /// </summary>
    public string? CommitHash { get; init; }

    /// <summary>
    /// Returns a string representation of the framework information.
    /// </summary>
    public override string ToString() => $"{Name} {Version} [{Path}]";
}

/// <summary>
/// Represents information about the .NET host installation.
/// </summary>
public sealed class DotNetHostInfo
{
    /// <summary>
    /// Gets the version of the host.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the architecture of the host (e.g., "x64", "x86", "arm64").
    /// </summary>
    public required string Architecture { get; init; }

    /// <summary>
    /// Gets the commit hash of the host build, if available.
    /// </summary>
    public string? CommitHash { get; init; }

    /// <summary>
    /// Gets the path to the .NET host executable.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Returns a string representation of the host information.
    /// </summary>
    public override string ToString() => $"{Version} ({Architecture}) [{Path}]";
}

/// <summary>
/// Represents information about the runtime environment.
/// </summary>
public sealed class DotNetRuntimeEnvironmentInfo
{
    /// <summary>
    /// Gets the operating system description.
    /// </summary>
    public required string OSDescription { get; init; }

    /// <summary>
    /// Gets the RID (Runtime Identifier) for the current platform.
    /// </summary>
    public required string RID { get; init; }

    /// <summary>
    /// Gets the base path where .NET is installed.
    /// </summary>
    public required string BasePath { get; init; }

    /// <summary>
    /// Gets additional environment information as key-value pairs.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Returns a string representation of the runtime environment information.
    /// </summary>
    public override string ToString() => $"{OSDescription} (RID: {RID}) [Base: {BasePath}]";
}

/// <summary>
/// Represents comprehensive information about a .NET installation, similar to 'dotnet --info' output.
/// </summary>
public sealed class DotNetInstallationInfo
{
    /// <summary>
    /// Gets information about the .NET host.
    /// </summary>
    public required DotNetHostInfo Host { get; init; }

    /// <summary>
    /// Gets information about the runtime environment.
    /// </summary>
    public required DotNetRuntimeEnvironmentInfo RuntimeEnvironment { get; init; }

    /// <summary>
    /// Gets the collection of installed SDKs.
    /// </summary>
    public IReadOnlyList<DotNetSdkInfo> Sdks { get; init; } = Array.Empty<DotNetSdkInfo>();

    /// <summary>
    /// Gets the collection of installed runtime frameworks.
    /// </summary>
    public IReadOnlyList<DotNetFrameworkInfo> Frameworks { get; init; } = Array.Empty<DotNetFrameworkInfo>();

    /// <summary>
    /// Gets the path to the global.json file used for SDK resolution, if any.
    /// </summary>
    public string? GlobalJsonPath { get; init; }

    /// <summary>
    /// Gets the SDK version resolved from global.json, if any.
    /// </summary>
    public string? GlobalJsonSdkVersion { get; init; }

    /// <summary>
    /// Gets the root directory of the .NET installation.
    /// </summary>
    public required string DotNetRoot { get; init; }

    /// <summary>
    /// Returns a string representation of the .NET installation information.
    /// </summary>
    public override string ToString() => $".NET {Host.Version} installation at {DotNetRoot} with {Sdks.Count} SDKs and {Frameworks.Count} frameworks";
}

/// <summary>
/// Represents the result of a .NET location operation.
/// </summary>
/// <typeparam name="T">The type of the result data.</typeparam>
public sealed class DotNetLocationResult<T>
{
    /// <summary>
    /// Gets a value indicating whether the operation was successful.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Gets the result data if the operation was successful.
    /// </summary>
    public T? Data { get; init; }

    /// <summary>
    /// Gets the error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the exception that caused the operation to fail, if any.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="data">The result data.</param>
    /// <returns>A successful result.</returns>
    public static DotNetLocationResult<T> Success(T data) => new()
    {
        IsSuccess = true,
        Data = data
    };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="exception">The exception that caused the failure.</param>
    /// <returns>A failed result.</returns>
    public static DotNetLocationResult<T> Failure(string errorMessage, Exception? exception = null) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage,
        Exception = exception
    };
}