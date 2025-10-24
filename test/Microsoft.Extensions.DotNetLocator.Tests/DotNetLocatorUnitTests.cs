using FluentAssertions;
using Microsoft.Extensions;
using Xunit;

namespace Microsoft.Extensions.Tests;

public class DotNetLocatorApiTests
{
    [Fact]
    public async Task GetInstallationInfoAsync_DefaultCall_ShouldReturnValidInfo()
    {
        // Act
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Host.Should().NotBeNull();
        result.Data.RuntimeEnvironment.Should().NotBeNull();
        result.Data.Sdks.Should().NotBeNull();
        result.Data.Frameworks.Should().NotBeNull();
    }

    [Fact]
    public async Task GetInstallationInfoAsync_WithCustomDir_ShouldReturnValidInfo()
    {
        // Arrange
        var tempDir = Path.GetTempPath();

        // Act
        var result = await DotNetLocator.GetInstallationInfoAsync(tempDir, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessLocator_GetInstallationInfoAsync_ShouldReturnValidInfo()
    {
        // Act
        var result = await DotNetLocator.ProcessLocator.GetInstallationInfoAsync();

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Host.Should().NotBeNull();
        result.Data.RuntimeEnvironment.Should().NotBeNull();
        result.Data.Sdks.Should().NotBeNull();
        result.Data.Frameworks.Should().NotBeNull();
    }

    [Fact]
    public async Task ManagedLocator_GetInstallationInfoAsync_ShouldReturnValidInfo()
    {
        // Act
        var result = await DotNetLocator.ManagedLocator.GetInstallationInfoAsync();

        // Assert
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data!.Host.Should().NotBeNull();
        result.Data.RuntimeEnvironment.Should().NotBeNull();
        result.Data.Sdks.Should().NotBeNull();
        result.Data.Frameworks.Should().NotBeNull();
    }

    [Fact]
    public async Task PInvokeLocator_GetInstallationInfoAsync_ShouldReturnValidInfo()
    {
        // Act
        var result = await DotNetLocator.PInvokeLocator.GetInstallationInfoAsync();

        // Assert
        result.Should().NotBeNull();
        if (result.IsSuccess)
        {
            result.Data.Should().NotBeNull();
            result.Data!.Host.Should().NotBeNull();
            result.Data.RuntimeEnvironment.Should().NotBeNull();
            result.Data.Sdks.Should().NotBeNull();
            result.Data.Frameworks.Should().NotBeNull();
        }
        else
        {
            // P/Invoke might fail on some systems due to library loading issues
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }
}

public class VersionSortingTests
{
    [Fact]
    public async Task ManagedLocator_VersionSorting_ShouldUseSemVer()
    {
        // This test verifies that version sorting uses semantic versioning
        // Act
        var result = await DotNetLocator.ManagedLocator.GetInstallationInfoAsync();

        // Assert
        result.Should().NotBeNull();
        if (result.IsSuccess && result.Data?.Sdks.Count > 1)
        {
            var versions = result.Data.Sdks.Select(s => s.Version).ToList();
            
            // Verify that versions are sorted correctly - newer versions should come first
            for (int i = 0; i < versions.Count - 1; i++)
            {
                var current = ParseVersion(versions[i]);
                var next = ParseVersion(versions[i + 1]);

                // Current version should be >= next version (descending order)
                current.Should().BeGreaterThanOrEqualTo(next,
                    $"Version {versions[i]} should be >= {versions[i + 1]} in descending sort order");
            }
        }
    }

    [Fact]
    public async Task PInvokeLocator_VersionSorting_ShouldUseSemVer()
    {
        // This test verifies that version sorting uses semantic versioning in P/Invoke locator
        // Act
        var result = await DotNetLocator.PInvokeLocator.GetInstallationInfoAsync();

        // Assert
        result.Should().NotBeNull();
        if (result.IsSuccess && result.Data?.Sdks.Count > 1)
        {
            var versions = result.Data.Sdks.Select(s => s.Version).ToList();
            
            // Verify that versions are sorted correctly - newer versions should come first
            for (int i = 0; i < versions.Count - 1; i++)
            {
                var current = ParseVersion(versions[i]);
                var next = ParseVersion(versions[i + 1]);
                
                // Current version should be >= next version (descending order)
                current.Should().BeGreaterThanOrEqualTo(next, 
                    $"Version {versions[i]} should be >= {versions[i + 1]} in descending sort order");
            }
        }
    }

    [Theory]
    [InlineData("8.0.100", "7.0.400", true)]
    [InlineData("8.0.200", "8.0.100", true)]
    [InlineData("8.1.0", "8.0.999", true)]
    [InlineData("9.0.0-preview.1", "8.9.999", true)]
    [InlineData("8.0.100-rc.1", "8.0.100-alpha.1", true)]
    public void VersionParsing_ShouldCompareCorrectly(string version1, string version2, bool version1ShouldBeGreater)
    {
        // Act
        var v1 = ParseVersion(version1);
        var v2 = ParseVersion(version2);

        // Assert
        if (version1ShouldBeGreater)
        {
            v1.Should().BeGreaterThan(v2, $"{version1} should be greater than {version2}");
        }
        else
        {
            v1.Should().BeLessThan(v2, $"{version1} should be less than {version2}");
        }
    }

    private static Version ParseVersion(string version)
    {
        // Replicate the version parsing logic from the locators
        var match = System.Text.RegularExpressions.Regex.Match(version, @"^(\d+)\.(\d+)\.(\d+)(?:-([a-zA-Z0-9\-\.]+))?$");
        if (!match.Success)
        {
            throw new ArgumentException($"Invalid version format: {version}");
        }
        
        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = int.Parse(match.Groups[3].Value);
        
        // For prerelease versions, we use the 4th component to differentiate
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

public class DataModelTests
{
    [Fact]
    public void DotNetSdkInfo_ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var sdk = new DotNetSdkInfo
        {
            Version = "8.0.100",
            Path = "/usr/share/dotnet/sdk/8.0.100"
        };

        // Act
        var result = sdk.ToString();

        // Assert
        result.Should().Be("8.0.100 [/usr/share/dotnet/sdk/8.0.100]");
    }

    [Fact]
    public void DotNetFrameworkInfo_ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var framework = new DotNetFrameworkInfo
        {
            Name = "Microsoft.NETCore.App",
            Version = "8.0.0",
            Path = "/usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.0"
        };

        // Act
        var result = framework.ToString();

        // Assert
        result.Should().Be("Microsoft.NETCore.App 8.0.0 [/usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.0]");
    }

    [Fact]
    public async Task LocationResult_Success_ShouldHaveCorrectProperties()
    {
        // Act
        var result = await DotNetLocator.GetInstallationInfoAsync();

        // Assert
        if (result.IsSuccess)
        {
            result.Data.Should().NotBeNull();
            result.ErrorMessage.Should().BeNull();
            result.Exception.Should().BeNull();
        }
        else
        {
            result.Data.Should().BeNull();
            result.ErrorMessage.Should().NotBeNullOrEmpty();
        }
    }
}