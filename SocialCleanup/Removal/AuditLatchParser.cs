using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SocialCleanup.Friends;

namespace SocialCleanup.Removal;

// Dalamud-free on purpose: linked into the test project. File I/O stays in RemovalAuditLog.cs.

/// <summary>Result of scanning the audit CSV at startup for a real send that never resolved.</summary>
/// <param name="Blocked">Sends must stay blocked until the user clears it.</param>
/// <param name="Reason">Human-readable explanation (also when not blocked).</param>
/// <param name="Target">Target of the unresolved INTENT row, when one was found.</param>
/// <param name="LocalContentId">Sender of that row.</param>
/// <param name="IntentUtc">When that INTENT row was written.</param>
public sealed record StartupLatchCheck(bool Blocked, string Reason, RemovalTarget? Target, ulong LocalContentId, DateTime? IntentUtc)
{
    public static StartupLatchCheck Clear(string reason) => new(false, reason, null, 0, null);
}

/// <summary>CSV format of the audit file and the startup latch rule over its text.</summary>
public static class AuditLatchParser
{
    public const string FileName = "removal-audit.csv";

    public const string Header = "utc,local_content_id,target_content_id,name,home_world,dry_run,phase,result,detail";

    /// <summary>
    /// Startup check (persisted in-flight latch) over the full file text. Finds the most recent real (non-dry-run)
    /// INTENT row. Sends stay blocked when it is younger than <paramref name="maxAge"/> and no later row for the same
    /// sender + target shows it resolved. Resolved means: a REFUSED row with no OUTCOME row between the INTENT and it
    /// (nothing was sent), a RELEASED row (latch released by a qualifying snapshot or a definitive server rejection),
    /// a USERCLEARED row, or an OUTCOME row followed by a VERDICT of Removed / StillPresent / ServerRejected.
    /// BATCH_* / VERIFY rows carry target ContentId 0, never match an INTENT's target and are therefore ignored.
    /// Empty text → not blocked. Corrupt (bad header, wrong field count, bad timestamp, truncated row) → blocked.
    /// </summary>
    public static StartupLatchCheck Evaluate(string text, TimeSpan maxAge, DateTime nowUtc)
    {
        if (text.Length == 0)
            return StartupLatchCheck.Clear("Audit file is empty.");

        var lines = text.Split('\n');
        var rows = new List<string[]>(lines.Length);
        var headerSeen = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;
            if (!headerSeen)
            {
                // A UTF-8 BOM may survive when the text did not come through a BOM-detecting reader.
                if (line.TrimStart('﻿') != Header)
                    return Corrupt($"line {i + 1} is not the expected header");
                headerSeen = true;
                continue;
            }

            if (!TryParseCsvLine(line, out var fields) || fields.Length != 9)
                return Corrupt($"line {i + 1} is malformed (possibly truncated by a crash)");
            if (!DateTime.TryParse(fields[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                return Corrupt($"line {i + 1} has an unreadable timestamp");
            rows.Add(fields);
        }

        if (!headerSeen)
            return StartupLatchCheck.Clear("Audit file has no rows.");

        var last = -1;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i][6] == AuditPhase.Intent && rows[i][5] == "false")
            {
                last = i;
                break;
            }
        }

        if (last < 0)
            return StartupLatchCheck.Clear("No real send in the audit file.");

        var intent = rows[last];
        var intentUtc = DateTime.Parse(intent[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        var target = ParseTarget(intent);
        ulong.TryParse(intent[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var localId);
        var age = nowUtc - intentUtc;
        if (age > maxAge)
        {
            return new StartupLatchCheck(false,
                $"Last real send ({target}) was {age.TotalMinutes:0} min ago (older than {maxAge.TotalMinutes:0} min); not blocking.",
                target, localId, intentUtc);
        }

        var outcomeSeen = false;
        for (var i = last + 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r[5] != "false" || r[1] != intent[1] || r[2] != intent[2])
                continue;
            switch (r[6])
            {
                // A REFUSED row only proves "nothing sent" when the native call had not happened yet (no OUTCOME first).
                case AuditPhase.Refused when !outcomeSeen:
                    return new StartupLatchCheck(false, $"Last real send ({target}) was refused before the native call.", target, localId, intentUtc);
                case AuditPhase.Released:
                    return new StartupLatchCheck(false, $"Last real send ({target}) was resolved (latch released: {r[7]}).", target, localId, intentUtc);
                case AuditPhase.UserCleared:
                    return new StartupLatchCheck(false, $"Last real send ({target}) was cleared by the user.", target, localId, intentUtc);
                case AuditPhase.Outcome:
                    outcomeSeen = true;
                    break;
                case AuditPhase.Verdict when outcomeSeen && r[7] is "Removed" or "StillPresent" or "ServerRejected":
                    return new StartupLatchCheck(false, $"Last real send ({target}) has verdict {r[7]}.", target, localId, intentUtc);
            }
        }

        return new StartupLatchCheck(true,
            $"A real removal of {target} was started {intentUtc.ToLocalTime():HH:mm:ss} ({age.TotalSeconds:0}s ago) and the audit log shows no " +
            $"{(outcomeSeen ? "conclusive verdict" : "outcome")} for it (plugin reloaded or crashed mid-send?). Its result is unknown.",
            target, localId, intentUtc);

        static StartupLatchCheck Corrupt(string what) =>
            new(true, $"Audit file {FileName} {what}; cannot tell whether a removal is still in flight.", null, 0, null);
    }

    /// <summary>One CSV row, as the writer formats it.</summary>
    public static string FormatRow(DateTime utc, ulong localContentId, RemovalTarget target, bool dryRun, string phase, string result, string detail) =>
        string.Join(
            ',',
            Escape(utc.ToString("O", CultureInfo.InvariantCulture)),
            Escape(localContentId.ToString("X16", CultureInfo.InvariantCulture)),
            Escape(target.ContentId.ToString("X16", CultureInfo.InvariantCulture)),
            Escape(target.DisplayName),
            Escape($"{target.HomeWorldName} ({target.HomeWorldId})"),
            Escape(dryRun ? "true" : "false"),
            Escape(phase),
            Escape(result),
            Escape(detail));

    internal static RemovalTarget ParseTarget(string[] row)
    {
        ulong.TryParse(row[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cid);
        var name = row[3] == FriendEntry.UnableToRetrieveName ? string.Empty : row[3];

        // home_world is "Name (id)".
        var hw = row[4];
        ushort worldId = 0;
        var worldName = hw;
        var open = hw.LastIndexOf(" (", StringComparison.Ordinal);
        if (open >= 0 && hw.EndsWith(')')
            && ushort.TryParse(hw.AsSpan(open + 2, hw.Length - open - 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            worldId = id;
            worldName = hw[..open];
        }

        return new RemovalTarget(cid, name, worldId, worldName);
    }

    /// <summary>RFC 4180 single-line parse (the writer strips CR/LF from every field).</summary>
    public static bool TryParseCsvLine(string line, out string[] fields)
    {
        var result = new List<string>(9);
        var sb = new StringBuilder();
        var i = 0;
        while (true)
        {
            sb.Clear();
            if (i < line.Length && line[i] == '"')
            {
                i++;
                var closed = false;
                while (i < line.Length)
                {
                    var c = line[i++];
                    if (c == '"')
                    {
                        if (i < line.Length && line[i] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            closed = true;
                            break;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                if (!closed || (i < line.Length && line[i] != ','))
                {
                    fields = Array.Empty<string>();
                    return false;
                }
            }
            else
            {
                while (i < line.Length && line[i] != ',')
                {
                    if (line[i] == '"')
                    {
                        fields = Array.Empty<string>();
                        return false;
                    }

                    sb.Append(line[i++]);
                }
            }

            result.Add(sb.ToString());
            if (i >= line.Length)
                break;
            i++; // skip ','
        }

        fields = result.ToArray();
        return true;
    }

    /// <summary>RFC 4180 field escaping; also neutralises spreadsheet formula injection and strips CR/LF.</summary>
    public static string Escape(string? value)
    {
        value ??= string.Empty;
        value = value.Replace("\r", " ").Replace("\n", " ");
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        if (value.IndexOfAny([',', '"']) >= 0 || value.StartsWith(' ') || value.EndsWith(' '))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
