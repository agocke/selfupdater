using System.Reflection;
using System.Security.Cryptography;
using Serde;

namespace SelfUpdater;

internal static class Utilities
{
    /// <summary>Full path of the running executable.</summary>
    public static string? ProcessPath => Environment.ProcessPath;

    /// <summary>
    /// True when running as a self-contained single-file app (the case the swap
    /// supports). Single-file/AOT apps report an empty assembly location, which is
    /// exactly the signal we want here.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000",
        Justification = "An empty Location is precisely the single-file signal we test for."
    )]
    public static bool IsSingleFile() =>
        string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);

    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        var mode = File.GetUnixFileMode(path);
        mode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, mode);
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
