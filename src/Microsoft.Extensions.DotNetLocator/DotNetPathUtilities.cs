using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Extensions;

internal static class DotNetPathUtilities
{
    private static readonly string[] WindowsDefaultPathext = new[] { ".com", ".exe", ".bat", ".cmd" };

    public static async Task<string?> LocateDotNetExecutableAsync(CancellationToken cancellationToken = default)
    {
        var viaPathEnumeration = LocateByEnumeratingPath();
        if (!string.IsNullOrEmpty(viaPathEnumeration))
        {
            return viaPathEnumeration;
        }

        if (OperatingSystem.IsWindows())
        {
            var viaSearchPath = TryLocateWithSearchPath("dotnet");
            if (!string.IsNullOrEmpty(viaSearchPath))
            {
                return viaSearchPath;
            }

            var viaWhere = await TryLocateWithProcessAsync("where", new[] { "dotnet" }, cancellationToken);
            if (!string.IsNullOrEmpty(viaWhere))
            {
                return viaWhere;
            }
        }
        else
        {
            var viaWhich = await TryLocateWithProcessAsync("which", new[] { "dotnet" }, cancellationToken);
            if (!string.IsNullOrEmpty(viaWhich))
            {
                return viaWhich;
            }

            var viaCommand = await TryLocateWithProcessAsync("/bin/sh", new[] { "-c", "command -v dotnet" }, cancellationToken);
            if (!string.IsNullOrEmpty(viaCommand))
            {
                return viaCommand;
            }

            var viaType = await TryLocateWithProcessAsync("/bin/sh", new[] { "-c", "type -p dotnet" }, cancellationToken);
            if (!string.IsNullOrEmpty(viaType))
            {
                return viaType;
            }
        }

        return null;
    }

    private static string? LocateByEnumeratingPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        var commandName = OperatingSystem.IsWindows() ? "dotnet" : "dotnet";
        var candidateNames = BuildCandidateNames(commandName);

        foreach (var rawEntry in SplitPathEntries(pathValue))
        {
            var directory = NormalizePathEntry(rawEntry);
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            string resolvedDirectory;
            try
            {
                resolvedDirectory = Path.IsPathRooted(directory) ? directory : Path.GetFullPath(directory);
            }
            catch
            {
                continue;
            }

            if (!Directory.Exists(resolvedDirectory))
            {
                continue;
            }

            foreach (var candidate in candidateNames)
            {
                string candidatePath;
                try
                {
                    candidatePath = Path.Combine(resolvedDirectory, candidate);
                }
                catch
                {
                    continue;
                }

                if (IsExecutable(candidatePath))
                {
                    return candidatePath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildCandidateNames(string commandName)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return commandName;
            yield break;
        }

        var hasExtension = Path.HasExtension(commandName);
        if (hasExtension)
        {
            yield return commandName;
            yield break;
        }

        var pathext = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrEmpty(pathext)
            ? WindowsDefaultPathext
            : pathext.Split(';', StringSplitOptions.RemoveEmptyEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in extensions)
        {
            var normalized = ext.StartsWith('.') ? ext : "." + ext;
            if (seen.Add(normalized))
            {
                yield return commandName + normalized;
            }
        }

        if (seen.Add(string.Empty))
        {
            yield return commandName;
        }
    }

    private static IEnumerable<string> SplitPathEntries(string pathValue)
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var part in pathValue.Split(Path.PathSeparator, StringSplitOptions.None))
            {
                yield return part;
            }

            yield break;
        }

        var builder = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in pathValue)
        {
            if (ch == '\"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (ch == Path.PathSeparator && !inQuotes)
            {
                yield return builder.ToString();
                builder.Clear();
            }
            else
            {
                builder.Append(ch);
            }
        }

        yield return builder.ToString();
    }

    private static string? NormalizePathEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return null;
        }

        var trimmed = entry.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '\"' && trimmed[^1] == '\"')
        {
            trimmed = trimmed[1..^1];
        }

        trimmed = trimmed.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        try
        {
            trimmed = Environment.ExpandEnvironmentVariables(trimmed);
        }
        catch
        {
            // Ignore expansion errors
        }

        if (!OperatingSystem.IsWindows() && trimmed.StartsWith('~'))
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                if (trimmed.Length == 1)
                {
                    trimmed = home;
                }
                else if (trimmed[1] == Path.DirectorySeparatorChar || trimmed[1] == Path.AltDirectorySeparatorChar)
                {
                    trimmed = Path.Combine(home, trimmed[2..]);
                }
            }
        }

        return trimmed;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                return true;
            }

            try
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode executeMask = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                return (mode & executeMask) != 0;
            }
            catch (PlatformNotSupportedException)
            {
                return true;
            }
            catch (IOException)
            {
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string?> TryLocateWithProcessAsync(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);
            await errorTask;

            if (process.ExitCode != 0)
            {
                return null;
            }

            var output = await outputTask;
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                return null;
            }

            return NormalizeResolvedPath(lines[0]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeResolvedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '\"' && trimmed[^1] == '\"')
        {
            trimmed = trimmed[1..^1];
        }

        trimmed = trimmed.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        try
        {
            trimmed = Environment.ExpandEnvironmentVariables(trimmed);
        }
        catch
        {
            // Ignore expansion errors
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static string? TryLocateWithSearchPath(string executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var result = TryInvokeSearchPath(executable);
        if (!string.IsNullOrEmpty(result))
        {
            return NormalizeResolvedPath(result);
        }

        var withExtension = TryInvokeSearchPath(executable + ".exe");
        return NormalizeResolvedPath(withExtension);
    }

    private static string? TryInvokeSearchPath(string executable)
    {
        var bufferSize = 260;

        while (true)
        {
            var buffer = new StringBuilder(bufferSize);
            var length = SearchPath(null, executable, null, buffer.Capacity, buffer, out _);
            if (length == 0)
            {
                return null;
            }

            if (length >= buffer.Capacity)
            {
                bufferSize = length + 1;
                continue;
            }

            return buffer.ToString();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SearchPath(string? lpPath, string lpFileName, string? lpExtension, int nBufferLength, StringBuilder lpBuffer, out IntPtr lpFilePart);
}
