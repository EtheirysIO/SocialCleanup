using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace SocialCleanup.Safety;

/// <summary>
/// Reads the running game's version from two independent sources and hands them to <see cref="VersionGate"/>:
/// <list type="number">
/// <item><b>In-process memory</b>: FFXIVClientStructs <c>Framework.Instance()->GameVersionString</c>, the version the
/// running ffxiv_dx11.exe loaded at startup. This is the only native read the gate performs.</item>
/// <item><b>On disk next to the running executable</b>: <c>ffxivgame.ver</c> in the directory of this process's main
/// module (the running ffxiv_dx11.exe), not a configured / launcher path.</item>
/// </list>
/// Both must be readable, well formed and identical. (Dalamud's own <c>DalamudStartInfo.GameVersion</c> is not exposed to
/// plugins; the exe's version resource is a constant 1.0.0.0, so neither is usable.)
/// </summary>
public static unsafe class GameVersionProbe
{
    public const string MemorySourceName = "Framework.GameVersion (in-process memory)";
    public const string FileSourceName = "ffxivgame.ver next to the running executable";

    private const int MaxVerFileBytes = 256;

    /// <summary>Never throws; any failure yields a blocked gate.</summary>
    public static VersionGate Check()
    {
        try
        {
            return VersionGate.Evaluate(SupportedGameVersion.Value, new[]
            {
                new GameVersionSource(MemorySourceName, ReadFromMemory),
                new GameVersionSource(FileSourceName, ReadFromVerFile),
            });
        }
        catch (Exception ex)
        {
            return VersionGate.Blocked(SupportedGameVersion.Value, $"The version check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? ReadFromMemory()
    {
        var fw = GameFramework.Instance();
        if (fw == null)
            throw new InvalidOperationException("Framework.Instance() is null");
        return fw->GameVersionString;
    }

    private static string? ReadFromVerFile()
    {
        using var process = Process.GetCurrentProcess();
        var exe = process.MainModule?.FileName;
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("the main module path of this process is unknown");
        var dir = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(dir))
            throw new InvalidOperationException($"no directory for '{exe}'");

        var path = Path.Combine(dir, "ffxivgame.ver");
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException($"'{path}' does not exist");
        if (info.Length > MaxVerFileBytes)
            throw new InvalidDataException($"'{path}' is {info.Length} bytes (expected a short version string)");

        // Strict decoding: invalid UTF-8 throws (blocked) instead of being replaced.
        return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
    }
}
