namespace SocialCleanup.Removal;

// Dalamud-free on purpose: linked into the test project.

/// <summary>Row phases written to the audit CSV.</summary>
public static class AuditPhase
{
    /// <summary>Written (and flushed) immediately BEFORE the native call. No row → no call.</summary>
    public const string Intent = "INTENT";

    /// <summary>Written right after the native call returned (or threw).</summary>
    public const string Outcome = "OUTCOME";

    /// <summary>A dry run that passed every check; nothing was sent.</summary>
    public const string DryRun = "DRYRUN";

    /// <summary>A send (real or dry) refused before the native call; nothing was sent.</summary>
    public const string Refused = "REFUSED";

    /// <summary>Final verdict of one removal (single-removal test or one queue item).</summary>
    public const string Verdict = "VERDICT";

    /// <summary>The remover's in-flight latch was released by a qualifying snapshot.</summary>
    public const string Released = "RELEASED";

    /// <summary>The user cleared a startup send-block ("I have checked the friend list").</summary>
    public const string UserCleared = "USERCLEARED";

    /// <summary>
    /// A removal batch started. Written with <see cref="RemovalAuditTargets.Batch"/> (target ContentId 0), so the
    /// startup latch check, which only looks at rows for the INTENT's own target, never treats it as a resolution.
    /// </summary>
    public const string BatchStart = "BATCH_START";

    /// <summary>A removal batch reached a terminal state (completed / cancelled / aborted / disposed), with counts. Target ContentId 0.</summary>
    public const string BatchEnd = "BATCH_END";

    /// <summary>End-of-batch verification (fast: after the one real reload; standard: against the last full snapshot). Target ContentId 0.</summary>
    public const string Verify = "VERIFY";

    /// <summary>Friends that were NOT in the batch went missing since batch start; the batch halted and may not be resumed. Target ContentId 0.</summary>
    public const string UnexpectedMissing = "UNEXPECTED_MISSING";
}

public static class RemovalAuditTargets
{
    /// <summary>Pseudo-target for batch-level rows. ContentId 0 is never a real send target (the remover refuses it before INTENT).</summary>
    public static readonly RemovalTarget Batch = new(0, "(batch)", 0, "n/a");
}

/// <summary>Append-only audit sink. Implementations never throw; false means the row is NOT on disk.</summary>
public interface IRemovalAudit
{
    /// <summary>Last write error, or null.</summary>
    string? LastError { get; }

    bool TryWrite(ulong localContentId, RemovalTarget target, bool dryRun, string phase, string result, string detail);
}
