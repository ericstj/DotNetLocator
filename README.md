# Microsoft.Extensions.DotNetLocator

A comprehensive .NET library for locating and retrieving detailed information about .NET SDK installations, similar to `dotnet --info` output.

## Features

- **Multiple Discovery Methods**: Supports environment-based discovery, probing directory-based discovery, and explicit .NET root specification
- **Three Implementation Strategies**:
  1. **Process Wrapper**: Calls `dotnet --info` and parses output
  2. **Managed Implementation**: Pure managed code that replicates hostfxr logic
  3. **P/Invoke Implementation**: Direct calls to hostfxr APIs
- **Global.json Support**: Automatically discovers and honors global.json files in the directory hierarchy
- **Comprehensive Information**: Returns detailed SDK, framework, host, and runtime environment information
- **Cross-platform**: Works on Windows, Linux, and macOS

## Quick Start

### Basic Usage

```csharp
using Microsoft.Extensions;

// Get .NET installation info using current environment
var result = await DotNetLocator.GetInstallationInfoAsync();

if (result.IsSuccess)
{
    var info = result.Data!;
    Console.WriteLine($"Host: {info.Host}");
    Console.WriteLine($"Runtime Environment: {info.RuntimeEnvironment}");
    Console.WriteLine($"SDKs: {info.Sdks.Count}");
    Console.WriteLine($"Frameworks: {info.Frameworks.Count}");
    
    foreach (var sdk in info.Sdks)
    {
        Console.WriteLine($"  SDK: {sdk}");
    }
    
    foreach (var framework in info.Frameworks) 
    {
        Console.WriteLine($"  Framework: {framework}");
    }
}
else
{
    Console.WriteLine($"Error: {result.ErrorMessage}");
}
```

### Probing Directory Usage

```csharp
// Get .NET installation info starting from a specific directory
// This will search for global.json in the directory hierarchy
var result = await DotNetLocator.GetInstallationInfoAsync("/path/to/project");
```

### Explicit .NET Root Usage

```csharp
// Get .NET installation info from a specific .NET installation
var result = await DotNetLocator.GetInstallationInfoAsync(
    dotnetRoot: "/usr/share/dotnet",
    probingDirectory: "/path/to/project"  // Optional, for global.json discovery
);
```

## Implementation-Specific Usage

### Process Locator (dotnet --info wrapper)

```csharp
var result = await DotNetLocator.ProcessLocator.GetInstallationInfoAsync();
```

### Managed Locator (Pure managed code)

```csharp
var result = await DotNetLocator.ManagedLocator.GetInstallationInfoAsync();
```

### P/Invoke Locator (Direct hostfxr calls)

```csharp
var result = await DotNetLocator.PInvokeLocator.GetInstallationInfoAsync();
```

## Data Model

### DotNetInstallationInfo

The main result type containing comprehensive .NET installation information:

```csharp
public sealed class DotNetInstallationInfo
{
    public DotNetHostInfo Host { get; init; }
    public DotNetRuntimeEnvironmentInfo RuntimeEnvironment { get; init; }
    public IReadOnlyList<DotNetSdkInfo> Sdks { get; init; }
    public IReadOnlyList<DotNetFrameworkInfo> Frameworks { get; init; }
    public string? GlobalJsonPath { get; init; }
    public string? GlobalJsonSdkVersion { get; init; }
    public string DotNetRoot { get; init; }
}
```

### SDK Information

```csharp
public sealed class DotNetSdkInfo
{
    public string Version { get; init; }
    public string Path { get; init; }
    public string? CommitHash { get; init; }
}
```

### Framework Information

```csharp
public sealed class DotNetFrameworkInfo
{
    public string Name { get; init; }        // e.g., "Microsoft.NETCore.App"
    public string Version { get; init; }
    public string Path { get; init; }
    public string? CommitHash { get; init; }
}
```

### Host Information

```csharp
public sealed class DotNetHostInfo
{
    public string Version { get; init; }
    public string Architecture { get; init; }  // e.g., "x64", "arm64"
    public string? CommitHash { get; init; }
    public string Path { get; init; }
}
```

### Runtime Environment Information

```csharp
public sealed class DotNetRuntimeEnvironmentInfo
{
    public string OSDescription { get; init; }
    public string RID { get; init; }  // Runtime Identifier
    public string BasePath { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; }
}
```

## Global.json Support

The library automatically searches for `global.json` files in the directory hierarchy starting from the probing directory. When found, it extracts the SDK version information:

```json
{
  "sdk": {
    "version": "8.0.100",
    "rollForward": "latestMinor"
  }
}
```

The `GlobalJsonPath` and `GlobalJsonSdkVersion` properties in `DotNetInstallationInfo` will be populated accordingly.

## Implementation Details

### 1. Process Locator
- Executes `dotnet --info` as a child process
- Parses the text output using regular expressions
- Most reliable but has process overhead
- Works on all platforms where `dotnet` is available

### 2. Managed Locator
- Pure managed C# implementation
- Replicates the SDK/framework discovery logic from hostfxr
- Scans filesystem directories for SDK and framework installations
- No external dependencies, fastest execution
- Platform-specific logic for default installation paths

### 3. P/Invoke Locator
- Uses P/Invoke to call `hostfxr_get_dotnet_environment_info` directly
- Requires loading and calling the hostfxr native library
- Most accurate as it uses the same APIs as the .NET host
- May fail on systems with non-standard installations

## Error Handling

All methods return `DotNetLocationResult<T>` which includes:

```csharp
public sealed class DotNetLocationResult<T>
{
    public bool IsSuccess { get; init; }
    public T? Data { get; init; }
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }
}
```

## Requirements

- .NET 8.0 or later
- Works on Windows, Linux, and macOS
- Requires a .NET installation to be present on the system

## Installation

```xml
<PackageReference Include="Microsoft.Extensions.DotNetLocator" Version="1.0.0" />
```

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.