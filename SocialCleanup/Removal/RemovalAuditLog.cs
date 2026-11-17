using System;
using System.IO;
using System.Text;
using Dalamud.Plugin.Services;

namespace SocialCleanup.Removal;

// AuditPhase lives in IRemovalAudit.cs; StartupLatchCheck, the CSV format and the startup latch rule live in
// AuditLatchParser.cs (all Dalamud-free, linked into the tests).

/// <summary>
/// Append-only CSV audit trail of every removal attempt, in the plugin config directory.
/// Each write opens the file in append mode, writes one row, and flushes through to disk before returning.
/// Never throws; <see cref="TryWrite"/> returns false when the row could not be persisted.
/// </summary>
public sealed class RemovalAuditLog : IRemovalAudit
{
    public const string FileName = AuditLatchParser.FileName;

    private readonly IPluginLog log;
    private readonly object gate = new();

    public RemovalAuditLog(string directory, IPluginLog log)
    {
        this.log = log;
        FilePath = Path.Combine(directory, FileName);
    }

    public string FilePath { get; }

    /// <summary>Last write error, or null.</summary>
    public string? LastError { get; private set; }

    public bool TryWrite(
        ulong localContentId,
        RemovalTarget target,
        bool dryRun,
        string phase,
        string result,
        string detail)
    {
        var row = AuditLatchParser.FormatRow(DateTime.UtcNow, localContentId, target, dryRun, phase, result, detail);

        lock (gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
                var sb = new StringBuilder();
                if (fs.Length == 0)
                    sb.Append(AuditLatchParser.Header).Append("\r\n");
                sb.Append(row).Append("\r\n");

                var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log.Error(ex, $"[Audit] Could not write {phase} row to {FilePath}.");
                return false;
            }
        }
    }

    /// <summary>
    /// Startup check (persisted in-flight latch); see <see cref="AuditLatchParser.Evaluate"/> for the rule.
    /// A missing file → not blocked. Unreadable / locked / corrupt → blocked with the reason. Never throws.
    /// </summary>
    public StartupLatchCheck CheckStartupLatch(TimeSpan maxAge, DateTime nowUtc)
    {
        try
        {
            string text;
            lock (gate)
            {
                if (!File.Exists(FilePath))
                    return StartupLatchCheck.Clear("No audit file yet.");
                try
                {
                    using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                    text = sr.ReadToEnd();
                }
                catch (FileNotFoundException)
                {
                    return StartupLatchCheck.Clear("No audit file yet.");
                }
                catch (DirectoryNotFoundException)
                {
                    return StartupLatchCheck.Clear("No audit file yet.");
                }
                catch (Exception ex)
                {
                    return new StartupLatchCheck(true, $"Audit file {FileName} could not be read ({ex.Message}); cannot tell whether a removal is still in flight.", null, 0, null);
                }
            }

            return AuditLatchParser.Evaluate(text, maxAge, nowUtc);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[Audit] Startup latch check of {FilePath} failed.");
            return new StartupLatchCheck(true, $"Audit file check failed: {ex.Message}", null, 0, null);
        }
    }
}
