namespace Microsoft.Extensions;

/// <summary>
/// Defines the contract for .NET installation locator implementations.
/// </summary>
internal interface IDotNetLocator
{
    /// <summary>
    /// Gets .NET installation information based on the specified parameters.
    /// </summary>
    /// <param name="probingDirectory">The directory to start probing from for global.json resolution.</param>
    /// <param name="dotnetRoot">Optional specific .NET root directory. If null, will be discovered.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A result containing the .NET installation information.</returns>
    Task<DotNetLocationResult<DotNetInstallationInfo>> GetInstallationInfoAsync(
        string probingDirectory,
        string? dotnetRoot,
        CancellationToken cancellationToken);
}