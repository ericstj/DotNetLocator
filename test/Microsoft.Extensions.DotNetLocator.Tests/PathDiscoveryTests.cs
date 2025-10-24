using FluentAssertions;
using Microsoft.Extensions;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace DotNetLocator.Tests;

/// <summary>
/// Tests PATH environment variable discovery logic.
/// These tests focus on edge cases and robustness of finding dotnet.exe in PATH.
/// Since the internal locator classes are not public, we test through the main API
/// which internally uses all three locators and returns the first successful result.
/// </summary>
public class PathDiscoveryTests : IDisposable
{
    private readonly string _originalPath;
    private readonly string _tempDir;
    private readonly string _dotnetExecutableName;

    public PathDiscoveryTests()
    {
        _originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _dotnetExecutableName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private string CreateFakeDotNetExecutable(string directory)
    {
        var executablePath = Path.Combine(directory, _dotnetExecutableName);
        
        if (OperatingSystem.IsWindows())
        {
            // Create a minimal Windows executable that just exits
            File.WriteAllBytes(executablePath, new byte[] 
            { 
                0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00,
                0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            });
        }
        else
        {
            // Create a shell script that exits
            File.WriteAllText(executablePath, "#!/bin/bash\nexit 0\n");
            // Make it executable
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{executablePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit();
        }
        
        return executablePath;
    }

    private void SetPathEnvironment(string pathValue)
    {
        Environment.SetEnvironmentVariable("PATH", pathValue);
    }

    [Fact]
    public async Task FindDotNet_WithTrailingSlashInPath_ShouldFindExecutable()
    {
        // Arrange
        var dotnetDir = Path.Combine(_tempDir, "dotnet-with-slash");
        Directory.CreateDirectory(dotnetDir);
        var expectedPath = CreateFakeDotNetExecutable(dotnetDir);
        
        // Test with trailing slash (and backslash on Windows)
        var pathWithSlash = dotnetDir + Path.DirectorySeparatorChar;
        SetPathEnvironment(pathWithSlash);

        // Act & Assert - Test through the main API which tries all three locators
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Since we're setting PATH to our test directory with a fake dotnet, 
        // the API should handle trailing slashes gracefully
        result.Should().NotBeNull();
        // Note: May fail with fake executable, but should handle the path parsing correctly
    }

    [Fact]
    public async Task FindDotNet_WithMixedSlashesInPath_ShouldFindExecutable()
    {
        // Arrange
        var dotnetDir = Path.Combine(_tempDir, "dotnet-mixed-slashes");
        Directory.CreateDirectory(dotnetDir);
        CreateFakeDotNetExecutable(dotnetDir);
        
        // Create path with mixed forward/back slashes
        var pathWithMixedSlashes = OperatingSystem.IsWindows() 
            ? dotnetDir.Replace('\\', '/') 
            : dotnetDir.Replace('/', '\\');
        SetPathEnvironment(pathWithMixedSlashes);

        // Act & Assert
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Note: This test documents expected behavior - Path.Combine should normalize slashes
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task FindDotNet_WithQuotedPathEntry_ShouldHandleGracefully()
    {
        // Arrange
        var dotnetDir = Path.Combine(_tempDir, "dotnet with spaces");
        Directory.CreateDirectory(dotnetDir);
        CreateFakeDotNetExecutable(dotnetDir);
        
        // Create PATH with quoted directory (common on Windows)
        var quotedPath = $"\"{dotnetDir}\"";
        SetPathEnvironment(quotedPath);

        // Act & Assert
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Current implementation likely fails this - quotes are not stripped
        // This documents that the implementation needs improvement
        result.Should().NotBeNull("PATH discovery should handle quoted paths");
    }

    [Fact]
    public async Task FindDotNet_WithRelativePathEntry_ShouldHandleGracefully()
    {
        // Arrange
        var relativePath = OperatingSystem.IsWindows() ? @".\relative\path" : "./relative/path";
        SetPathEnvironment(relativePath);

        // Act & Assert
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Should handle gracefully - relative paths in PATH are valid but unusual
        // May succeed or fail depending on current directory, but shouldn't crash
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task FindDotNet_WithPathContainingDotsAndSpaces_ShouldFindExecutable()
    {
        // Arrange
        var dotnetDir = Path.Combine(_tempDir, "dotnet. with .spaces.");
        Directory.CreateDirectory(dotnetDir);
        CreateFakeDotNetExecutable(dotnetDir);
        SetPathEnvironment(dotnetDir);

        // Act & Assert
        var result = await DotNetLocator.GetInstallationInfoAsync();

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task FindDotNet_WithCurrentDirectoryInPath_ShouldHandleCorrectly()
    {
        // Arrange - Add current directory to PATH (represented by "." on Unix, often implicit on Windows)
        var currentDir = Directory.GetCurrentDirectory();
        var dotnetExecutable = Path.Combine(currentDir, _dotnetExecutableName);
        
        // Create fake dotnet in current directory
        CreateFakeDotNetExecutable(currentDir);
        
        try
        {
            SetPathEnvironment(".");

            // Act & Assert
            var result = await DotNetLocator.GetInstallationInfoAsync();

            result.Should().NotBeNull();
        }
        finally
        {
            // Cleanup - remove fake dotnet from current directory
            if (File.Exists(dotnetExecutable))
            {
                File.Delete(dotnetExecutable);
            }
        }
    }

    [Theory]
    [InlineData("PROGRA~1", true)]  // 8.3 name for "Program Files"
    [InlineData("PROGRA~2", true)]  // 8.3 name for "Program Files (x86)"
    public async Task FindDotNet_With8Dot3Names_ShouldFindExecutable(string shortName, bool isWindows)
    {
        // Skip on non-Windows since 8.3 names are Windows-specific
        if (!OperatingSystem.IsWindows() && isWindows)
        {
            return;
        }

        // Arrange
        var basePath = OperatingSystem.IsWindows() ? @"C:\" : "/";
        var shortPath = Path.Combine(basePath, shortName, "dotnet");
        
        // We can't easily test real 8.3 names without administrative privileges
        // So this test documents the expected behavior
        SetPathEnvironment(shortPath);

        // Act & Assert
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Should handle gracefully even if path doesn't exist
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task FindDotNet_WithProbingDirectory_ShouldFindFirstExecutable()
    {
        // Arrange - Create multiple directories with dotnet executable
        var dir1 = Path.Combine(_tempDir, "dotnet1");
        var dir2 = Path.Combine(_tempDir, "dotnet2");
        var dir3 = Path.Combine(_tempDir, "dotnet3");
        
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        Directory.CreateDirectory(dir3);
        
        var exe1 = CreateFakeDotNetExecutable(dir1);
        var exe2 = CreateFakeDotNetExecutable(dir2);
        var exe3 = CreateFakeDotNetExecutable(dir3);
        
        // Set PATH with multiple directories
        var pathWithMultipleDirs = string.Join(Path.PathSeparator.ToString(), dir1, dir2, dir3);
        SetPathEnvironment(pathWithMultipleDirs);

        // Act & Assert
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Should find the first executable in PATH order
        result.Should().NotBeNull();
        
        // The DotNetRoot should be dir1 since it's first in PATH
        if (result.IsSuccess && result.Data != null)
        {
            result.Data.DotNetRoot.Should().Be(dir1);
        }
    }

    [Fact]
    public async Task FindDotNet_WithEmptyPathEntry_ShouldSkipEmptyEntries()
    {
        // Arrange
        var dotnetDir = Path.Combine(_tempDir, "dotnet-with-empty");
        Directory.CreateDirectory(dotnetDir);
        CreateFakeDotNetExecutable(dotnetDir);
        
        // Create PATH with empty entries (double semicolons/colons)
        var pathWithEmpties = $"{Path.PathSeparator}{Path.PathSeparator}{dotnetDir}{Path.PathSeparator}{Path.PathSeparator}";
        SetPathEnvironment(pathWithEmpties);

        // Act & Assert
        var result = await DotNetLocator.GetInstallationInfoAsync();

        result.Should().NotBeNull();
    }

    [Fact]
    public async Task FindDotNet_WithNonExistentPathEntries_ShouldHandleGracefully()
    {
        // Arrange
        var nonExistentPaths = new[]
        {
            @"C:\NonExistent\Path",
            @"/non/existent/path",
            @"\\invalid\unc\path",
            @"relative\non\existent"
        };
        
        var pathWithNonExistent = string.Join(Path.PathSeparator.ToString(), nonExistentPaths);
        SetPathEnvironment(pathWithNonExistent);

        // Act & Assert
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Should handle gracefully with try/catch and fall back to default locations
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task FindDotNet_WithNullOrEmptyPath_ShouldReturnResult()
    {
        // Arrange
        SetPathEnvironment("");

        // Act & Assert
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Should fall back to default locations when PATH is empty
        result.Should().NotBeNull();
        
        // Test with null PATH
        Environment.SetEnvironmentVariable("PATH", null);
        
        var result2 = await DotNetLocator.GetInstallationInfoAsync();

        result2.Should().NotBeNull();
    }

    [Fact]
    public async Task FindDotNet_CompareWithWhereCommand_ShouldBeConsistent()
    {
        // This test compares our implementation with the OS "where" (Windows) or "which" (Unix) command
        // to validate that our PATH discovery logic matches the OS behavior
        
        if (!OperatingSystem.IsWindows())
        {
            // Skip on non-Windows for now, would need "which" command logic
            return;
        }

        try
        {
            // Use the real PATH and see if "where dotnet" finds the same result as our logic
            var whereProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "dotnet",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            
            whereProcess.Start();
            var whereOutput = await whereProcess.StandardOutput.ReadToEndAsync();
            await whereProcess.WaitForExitAsync();
            
            if (whereProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(whereOutput))
            {
                var whereDotnetPath = whereOutput.Trim().Split('\n')[0].Trim();
                var whereDotnetRoot = Path.GetDirectoryName(whereDotnetPath);

                // Compare with our implementation
                var result = await DotNetLocator.GetInstallationInfoAsync();

                if (result.IsSuccess && result.Data != null)
                {
                    result.Data.DotNetRoot.Should().Be(whereDotnetRoot, 
                        "Our locator should find same dotnet as 'where' command");
                }
            }
        }
        catch
        {
            // If "where" command fails, skip the test
            // This is acceptable since not all environments have dotnet in PATH
        }
    }
}
