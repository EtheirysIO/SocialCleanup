using Dalamud.Plugin.Services;

namespace SocialCleanup;

/// <summary>
/// Per-send / per-reply / per-snapshot telemetry goes to Debug, or to Information when the user turned on
/// <see cref="Configuration.VerboseLogging"/>. Version-gate results, batch start/end, halts, server rejections, final
/// verification and every Warning/Error keep their own level and do not go through here.
/// </summary>
internal static class TelemetryLog
{
    private static volatile bool verbose;

    /// <summary>Mirrors <see cref="Configuration.VerboseLogging"/> (set by the plugin at load and when the setting changes).</summary>
    public static bool Verbose
    {
        get => verbose;
        set => verbose = value;
    }

    public static void Telemetry(this IPluginLog log, string message)
    {
        if (verbose)
            log.Information(message);
        else
            log.Debug(message);
    }
}
