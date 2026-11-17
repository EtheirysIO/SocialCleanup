using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SocialCleanup.Safety;

/// <summary>One independent way of reading the running game's version. <see cref="Read"/> may return null or throw.</summary>
public sealed record GameVersionSource(string Name, Func<string?> Read);

/// <summary>What one source produced: the normalised value, or why it is unusable (<see cref="Error"/> non-null).</summary>
public sealed record GameVersionReading(string Source, string? Value, string? Error)
{
    public bool IsUsable => Error == null && Value != null;

    public override string ToString() => IsUsable ? $"{Source}: {Value}" : $"{Source}: unreadable ({Error})";
}

/// <summary>
/// Hard game-version gate (Dalamud-free; linked into the test project). Immutable. Fail closed: the gate passes ONLY
/// when at least one source was given, every source returned a well-formed version, all of them agree, and that value
/// is exactly <see cref="SupportedVersion"/> (ordinal). A source that throws, returns null / empty / garbage, sources
/// that disagree, a malformed supported constant, or any exception inside the evaluation all block.
///
/// Normalisation is limited to stripping leading / trailing ASCII whitespace (space, tab, CR, LF) and trailing NULs
/// (fixed-size native buffers). Nothing looser: no prefix match, no "newer is fine", no numeric comparison.
///
/// There is deliberately no way to force a pass.
/// </summary>
public sealed class VersionGate
{
    private static readonly char[] TrailingJunk = { '\0', ' ', '\t', '\r', '\n' };
    private static readonly char[] LeadingJunk = { ' ', '\t', '\r', '\n' };

    private VersionGate(bool passed, string supportedVersion, string? detectedVersion, string reason, IReadOnlyList<GameVersionReading> readings)
    {
        Passed = passed;
        SupportedVersion = supportedVersion;
        DetectedVersion = detectedVersion;
        Reason = reason;
        Readings = readings;
    }

    /// <summary>True only when the running game is exactly the supported version.</summary>
    public bool Passed { get; }

    public string SupportedVersion { get; }

    /// <summary>The version every source agreed on (null when a source failed or they disagree).</summary>
    public string? DetectedVersion { get; }

    /// <summary>Why the gate passed / blocked, for the log and the UI.</summary>
    public string Reason { get; }

    public IReadOnlyList<GameVersionReading> Readings { get; }

    /// <summary>The detected version, or what each source returned when they did not produce one agreed value.</summary>
    public string DetectedDescription =>
        DetectedVersion ?? (Readings.Count == 0 ? "not read" : string.Join("; ", Readings));

    /// <summary>One line for the plugin log.</summary>
    public string Summary =>
        $"{(Passed ? "PASS" : "BLOCKED")}: supported game version {SupportedVersion}, detected {DetectedDescription}. {Reason}";

    /// <summary>Reads every source and decides. Never throws.</summary>
    public static VersionGate Evaluate(string supportedVersion, IReadOnlyList<GameVersionSource>? sources)
    {
        var supported = supportedVersion ?? string.Empty;
        try
        {
            if (!IsWellFormed(supported))
                return Block(supported, $"The supported-version constant '{Printable(supported)}' is malformed.");
            if (sources == null || sources.Count == 0)
                return Block(supported, "No game-version source was available.");

            var readings = new List<GameVersionReading>(sources.Count);
            foreach (var s in sources)
                readings.Add(ReadOne(s));

            var bad = readings.FindAll(r => !r.IsUsable);
            if (bad.Count > 0)
                return Block(supported, $"The game version could not be read ({string.Join("; ", bad)}).", readings);

            var first = readings[0].Value!;
            foreach (var r in readings)
            {
                if (!string.Equals(r.Value, first, StringComparison.Ordinal))
                    return Block(supported, $"Game-version sources disagree ({string.Join("; ", readings)}).", readings);
            }

            if (!string.Equals(first, supported, StringComparison.Ordinal))
                return new VersionGate(false, supported, first, $"Game version {first} is not the supported version {supported}.", readings.AsReadOnly());

            return new VersionGate(true, supported, first, $"All {readings.Count} source(s) agree ({string.Join("; ", readings)}).", readings.AsReadOnly());
        }
        catch (Exception ex)
        {
            return Block(supported, $"The version check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>A blocked gate for a failure outside <see cref="Evaluate"/> (e.g. building the sources threw).</summary>
    public static VersionGate Blocked(string supportedVersion, string reason) => Block(supportedVersion ?? string.Empty, reason);

    /// <summary>
    /// Defence in depth for every class that scans signatures, hooks or reads native game memory: call FIRST in its
    /// constructor, before any field is touched. Throws unless the gate passed.
    /// </summary>
    public static void EnsurePassed(VersionGate? gate, string component)
    {
        if (gate is not { Passed: true })
        {
            throw new InvalidOperationException(
                $"{component} refused: game-version gate {(gate == null ? "missing" : $"BLOCKED ({gate.Reason})")}. " +
                $"Supported game version is {SupportedGameVersion.Value}.");
        }
    }

    /// <summary>Strips leading / trailing ASCII whitespace and trailing NULs. Null stays null.</summary>
    public static string? Normalize(string? raw)
    {
        if (raw == null)
            return null;
        return raw.TrimEnd(TrailingJunk).TrimStart(LeadingJunk);
    }

    /// <summary>Exactly <c>YYYY.MM.DD.NNNN.NNNN</c> with ASCII digits.</summary>
    public static bool IsWellFormed(string? v)
    {
        if (v is not { Length: 20 })
            return false;
        for (var i = 0; i < v.Length; i++)
        {
            var dot = i is 4 or 7 or 10 or 15;
            var c = v[i];
            if (dot ? c != '.' : c is < '0' or > '9')
                return false;
        }

        return true;
    }

    private static GameVersionReading ReadOne(GameVersionSource? source)
    {
        var name = source?.Name ?? "(unnamed source)";
        if (source?.Read == null)
            return new GameVersionReading(name, null, "no reader");

        string? raw;
        try
        {
            raw = source.Read();
        }
        catch (Exception ex)
        {
            return new GameVersionReading(name, null, $"{ex.GetType().Name}: {ex.Message}");
        }

        if (raw == null)
            return new GameVersionReading(name, null, "no value");
        var v = Normalize(raw)!;
        if (v.Length == 0)
            return new GameVersionReading(name, null, "empty");
        if (!IsWellFormed(v))
            return new GameVersionReading(name, null, $"not a game version: '{Printable(v)}'");
        return new GameVersionReading(name, v, null);
    }

    /// <summary>Blocked without one agreed detected value (unreadable, disagreeing, or failed check).</summary>
    private static VersionGate Block(string supported, string reason, List<GameVersionReading>? readings = null) =>
        new(false, supported, null, reason, (readings ?? new List<GameVersionReading>()).AsReadOnly());

    /// <summary>For messages: control / non-ASCII characters escaped, at most 64 characters.</summary>
    private static string Printable(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (sb.Length >= 64)
            {
                sb.Append('…');
                break;
            }

            if (c is >= ' ' and <= '~')
                sb.Append(c);
            else
                sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
