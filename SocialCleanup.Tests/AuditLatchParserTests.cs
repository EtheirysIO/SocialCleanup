using System;
using System.Linq;
using System.Text;
using SocialCleanup.Removal;
using Xunit;

namespace SocialCleanup.Tests;

public sealed class AuditLatchParserTests
{
    private static readonly DateTime T0 = new(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);
    private static readonly RemovalTarget Target = new(0x1001, "Friend, \"One\"", 73, "Adamantoise");
    private static readonly RemovalTarget Other = new(0x1002, "Friend 2", 73, "Adamantoise");

    private static string Csv(params (int Sec, RemovalTarget T, bool Dry, string Phase, string Result)[] rows)
    {
        var sb = new StringBuilder(AuditLatchParser.Header).Append("\r\n");
        foreach (var r in rows)
            sb.Append(AuditLatchParser.FormatRow(T0.AddSeconds(r.Sec), Rig.Me, r.T, r.Dry, r.Phase, r.Result, "detail, with comma")).Append("\r\n");
        return sb.ToString();
    }

    private static StartupLatchCheck Check(string text) => AuditLatchParser.Evaluate(text, TimeSpan.FromMinutes(10), T0.AddMinutes(1));

    [Fact]
    public void RefusedRightAfterIntent_Resolves()
    {
        var c = Check(Csv((0, Target, false, AuditPhase.Intent, "PENDING"), (1, Target, false, AuditPhase.Refused, "Failed")));
        Assert.False(c.Blocked, c.Reason);
    }

    [Fact]
    public void RefusedAfterAnOutcome_DoesNotResolve()
    {
        // Fix A3: once the native call happened (OUTCOME), a later REFUSED row proves nothing about that send.
        var c = Check(Csv(
            (0, Target, false, AuditPhase.Intent, "PENDING"),
            (1, Target, false, AuditPhase.Outcome, "Sent"),
            (2, Target, false, AuditPhase.Refused, "Unavailable")));
        Assert.True(c.Blocked, c.Reason);
        Assert.Equal(Target.ContentId, c.Target!.ContentId);
        Assert.Equal(Target.Name, c.Target.Name);
        Assert.Equal((ushort)73, c.Target.HomeWorldId);
    }

    [Theory]
    [InlineData(AuditPhase.Verdict, "Removed", false)]
    [InlineData(AuditPhase.Verdict, "StillPresent", false)]
    [InlineData(AuditPhase.Verdict, "ServerRejected", false)]
    [InlineData(AuditPhase.Verdict, "Indeterminate", true)]
    [InlineData(AuditPhase.Released, "TargetAbsent", false)]
    [InlineData(AuditPhase.Released, "ServerRejected", false)]
    [InlineData(AuditPhase.UserCleared, "UserCleared", false)]
    public void AfterOutcome(string phase, string result, bool blocked)
    {
        var c = Check(Csv(
            (0, Target, false, AuditPhase.Intent, "PENDING"),
            (1, Target, false, AuditPhase.Outcome, "Sent"),
            (2, Target, false, phase, result)));
        Assert.Equal(blocked, c.Blocked);
    }

    [Fact]
    public void IntentOnly_Blocks_RowsForOtherTargetsOrBatchRowsDoNotResolve()
    {
        var c = Check(Csv(
            (0, Target, false, AuditPhase.Intent, "PENDING"),
            (1, Other, false, AuditPhase.Refused, "Failed"),
            (2, RemovalAuditTargets.Batch, false, AuditPhase.BatchEnd, "Completed"),
            (3, RemovalAuditTargets.Batch, false, AuditPhase.Verify, "Passed"),
            (4, Target, true, AuditPhase.Refused, "Failed")));
        Assert.True(c.Blocked);
        Assert.Contains("no outcome", c.Reason);
    }

    [Fact]
    public void OldIntent_DoesNotBlock_EmptyOrNoIntent_DoesNotBlock()
    {
        Assert.False(AuditLatchParser.Evaluate(Csv((0, Target, false, AuditPhase.Intent, "PENDING")), TimeSpan.FromMinutes(10), T0.AddMinutes(11)).Blocked);
        Assert.False(Check(string.Empty).Blocked);
        Assert.False(Check(Csv((0, Target, true, AuditPhase.DryRun, "DryRunResolved"))).Blocked);
    }

    [Fact]
    public void CorruptFile_Blocks()
    {
        Assert.True(Check("not,the,header\r\n").Blocked);
        var truncated = Csv((0, Target, false, AuditPhase.Intent, "PENDING"));
        Assert.True(Check(truncated + "2026-11-16T20:00:01.0000000Z,0011223344556677,\"broken").Blocked);
        Assert.True(Check(truncated + "yesterday,a,b,c,d,false,INTENT,x,y\r\n").Blocked);
    }

    [Fact]
    public void FormatRow_RoundTripsThroughTheParser()
    {
        var row = AuditLatchParser.FormatRow(T0, Rig.Me, Target, false, AuditPhase.Verdict, "=cmd", "a \"quoted\", detail");
        Assert.True(AuditLatchParser.TryParseCsvLine(row, out var f));
        Assert.Equal(9, f.Length);
        Assert.Equal(Target.Name, f[3]);
        Assert.Equal("Adamantoise (73)", f[4]);
        Assert.Equal("'=cmd", f[7]);
        Assert.Equal("a \"quoted\", detail", f[8]);
        Assert.Equal(Target.ContentId, AuditLatchParser.ParseTarget(f).ContentId);
        Assert.DoesNotContain('\n', row);
    }
}
