using System;
using System.Collections.Generic;
using SocialCleanup.Safety;
using Xunit;

namespace SocialCleanup.Tests;

public sealed class VersionGateTests
{
    private const string Supported = "2026.11.03.0000.0000";

    private static VersionGate Two(string? memory, string? file) =>
        VersionGate.Evaluate(Supported, new[]
        {
            new GameVersionSource("memory", () => memory),
            new GameVersionSource("file", () => file),
        });

    private static VersionGate Both(string? value) => Two(value, value);

    // ------------------------------------------------------------------ the constant

    [Fact]
    public void SupportedConstant_IsTheVerifiedVersion_AndWellFormed()
    {
        Assert.Equal("2026.11.03.0000.0000", SupportedGameVersion.Value);
        Assert.True(VersionGate.IsWellFormed(SupportedGameVersion.Value));
    }

    // ------------------------------------------------------------------ pass

    [Fact]
    public void ExactMatch_BothSources_Passes()
    {
        var g = Both(Supported);
        Assert.True(g.Passed);
        Assert.Equal(Supported, g.DetectedVersion);
        Assert.Equal(Supported, g.SupportedVersion);
        Assert.Equal(2, g.Readings.Count);
        Assert.All(g.Readings, r => Assert.True(r.IsUsable));
        Assert.StartsWith("PASS", g.Summary);
    }

    [Fact]
    public void ExactMatch_AgainstTheRealConstant_Passes()
    {
        var g = VersionGate.Evaluate(SupportedGameVersion.Value, new[] { new GameVersionSource("a", () => "2026.11.03.0000.0000") });
        Assert.True(g.Passed);
    }

    // ------------------------------------------------------------------ any differing component blocks

    [Theory]
    [InlineData("2027.11.03.0000.0000")] // year
    [InlineData("2025.11.03.0000.0000")]
    [InlineData("2026.12.03.0000.0000")] // month
    [InlineData("2026.10.03.0000.0000")]
    [InlineData("2026.11.04.0000.0000")] // day (newer is NOT fine)
    [InlineData("2026.11.02.0000.0000")] // older
    [InlineData("2026.11.03.0001.0000")] // build
    [InlineData("2026.11.03.0000.0001")] // revision
    [InlineData("2026.11.03.1000.0000")]
    [InlineData("2026.11.03.0000.1000")]
    public void AnyDifferingComponent_Blocks(string detected)
    {
        var g = Both(detected);
        Assert.False(g.Passed);
        Assert.Equal(detected, g.DetectedVersion); // read fine, agreed, just not supported
        Assert.Contains("not the supported version", g.Reason);
        Assert.StartsWith("BLOCKED", g.Summary);
    }

    [Theory]
    [InlineData("2026.11.03")] // prefix
    [InlineData("2026.11.03.0000")]
    [InlineData("2026.11.03.0000.000")]
    [InlineData("2026.11.03.0000.00000")] // supported as a prefix
    [InlineData("2026.11.03.0000.0000.0")]
    [InlineData("2026.11.03.0000.0000x")]
    [InlineData("x2026.11.03.0000.0000")]
    [InlineData("2026-11-03-0000-0000")]
    [InlineData("2026.11.3.0000.0000")]
    [InlineData("2026.11.03.0000.0000 2026.11.03.0000.0000")]
    public void NoPrefixOrLooseMatching(string detected)
    {
        var g = Both(detected);
        Assert.False(g.Passed);
        Assert.Null(g.DetectedVersion);
    }

    // ------------------------------------------------------------------ normalisation (whitespace / newline / trailing NUL only)

    [Theory]
    [InlineData("2026.11.03.0000.0000\n")]
    [InlineData("2026.11.03.0000.0000\r\n")]
    [InlineData("2026.11.03.0000.0000 ")]
    [InlineData("2026.11.03.0000.0000\t")]
    [InlineData("  2026.11.03.0000.0000")]
    [InlineData("\r\n2026.11.03.0000.0000\r\n")]
    [InlineData("2026.11.03.0000.0000\0")]
    [InlineData("2026.11.03.0000.0000\0\0\0\0\0\0\0\0\0\0\0\0")]
    [InlineData("2026.11.03.0000.0000\r\n\0\0")]
    [InlineData("2026.11.03.0000.0000\0 \n")]
    public void WhitespaceNewlineTrailingNul_AreNormalised_ThenExactMatch(string raw)
    {
        Assert.Equal(Supported, VersionGate.Normalize(raw));
        var g = Two(raw, Supported);
        Assert.True(g.Passed, g.Summary);
        Assert.Equal(Supported, g.DetectedVersion);
    }

    [Theory]
    [InlineData("\02026.11.03.0000.0000")] // leading NUL
    [InlineData("2026.11.03\0.0000.0000")] // embedded NUL
    [InlineData("2026.11.03.0000.0000\0garbage")] // NUL-terminated with junk after: not a trailing NUL
    [InlineData("﻿2026.11.03.0000.0000")] // BOM
    [InlineData("2026.11.03.0000.0000 ")] // non-breaking space
    [InlineData("2026.11.03.0000.0000　")] // ideographic space
    [InlineData("٢٠٢٦.11.03.0000.0000")] // non-ASCII digits
    [InlineData("２０２６.11.03.0000.0000")] // full-width digits
    [InlineData("2026.11.03.0000.0000\v")] // vertical tab is not in the allowed set
    public void NothingLooserThanTheAllowedNormalisation(string raw)
    {
        var g = Two(raw, Supported);
        Assert.False(g.Passed);
    }

    // ------------------------------------------------------------------ null / empty / garbage

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    [InlineData("\0\0\0\0")]
    [InlineData("garbage")]
    [InlineData("????????????????????")]
    [InlineData("\u0001\u0002\u0003")]
    public void NullEmptyGarbage_InEitherSource_Blocks(string? bad)
    {
        foreach (var g in new[] { Both(bad), Two(bad, Supported), Two(Supported, bad) })
        {
            Assert.False(g.Passed);
            Assert.Null(g.DetectedVersion);
            Assert.Contains("could not be read", g.Reason);
        }
    }

    [Fact]
    public void Unreadable_ReasonNamesTheSource()
    {
        var g = Two(Supported, null);
        Assert.False(g.Passed);
        Assert.Contains("file", g.Reason);
        Assert.True(g.Readings[0].IsUsable);
        Assert.False(g.Readings[1].IsUsable);
        Assert.Contains("unreadable", g.DetectedDescription);
    }

    // ------------------------------------------------------------------ sources disagree

    [Theory]
    [InlineData("2026.11.03.0000.0000", "2026.11.04.0000.0000")]
    [InlineData("2026.11.04.0000.0000", "2026.11.03.0000.0000")]
    [InlineData("2026.11.04.0000.0000", "2026.11.05.0000.0000")]
    public void SourcesDisagree_Blocks_EvenIfOneMatches(string memory, string file)
    {
        var g = Two(memory, file);
        Assert.False(g.Passed);
        Assert.Null(g.DetectedVersion);
        Assert.Contains("disagree", g.Reason);
        Assert.Contains(memory, g.DetectedDescription);
        Assert.Contains(file, g.DetectedDescription);
    }

    [Fact]
    public void ThreeSources_OneDisagrees_Blocks()
    {
        var g = VersionGate.Evaluate(Supported, new[]
        {
            new GameVersionSource("a", () => Supported),
            new GameVersionSource("b", () => Supported),
            new GameVersionSource("c", () => "2026.11.03.0000.0001"),
        });
        Assert.False(g.Passed);
        Assert.Contains("disagree", g.Reason);
    }

    // ------------------------------------------------------------------ exceptions

    [Fact]
    public void ExceptionInEitherSource_Blocks()
    {
        foreach (var throwFirst in new[] { true, false })
        {
            var g = VersionGate.Evaluate(Supported, new[]
            {
                new GameVersionSource("memory", () => throwFirst ? throw new InvalidOperationException("Framework.Instance() is null") : Supported),
                new GameVersionSource("file", () => throwFirst ? Supported : throw new System.IO.FileNotFoundException("no ffxivgame.ver")),
            });
            Assert.False(g.Passed);
            Assert.Null(g.DetectedVersion);
            Assert.Contains(throwFirst ? "Framework.Instance() is null" : "no ffxivgame.ver", g.Reason);
        }
    }

    [Fact]
    public void ExceptionInBothSources_Blocks() =>
        Assert.False(VersionGate.Evaluate(Supported, new[]
        {
            new GameVersionSource("a", () => throw new AccessViolationException()),
            new GameVersionSource("b", () => throw new Exception("boom")),
        }).Passed);

    [Fact]
    public void NullReadDelegate_OrNullSource_Blocks()
    {
        Assert.False(VersionGate.Evaluate(Supported, new[] { new GameVersionSource("a", null!) }).Passed);
        Assert.False(VersionGate.Evaluate(Supported, new GameVersionSource[] { new("a", () => Supported), null! }).Passed);
    }

    [Fact]
    public void ThrowingSourceList_Blocks_WithoutThrowing()
    {
        var g = VersionGate.Evaluate(Supported, new ThrowingList());
        Assert.False(g.Passed);
        Assert.Contains("version check failed", g.Reason);
    }

    // ------------------------------------------------------------------ no sources / bad constant / factories

    [Fact]
    public void NoSources_Blocks()
    {
        Assert.False(VersionGate.Evaluate(Supported, Array.Empty<GameVersionSource>()).Passed);
        Assert.False(VersionGate.Evaluate(Supported, null).Passed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026.11.03")]
    [InlineData("garbage")]
    [InlineData(" 2026.11.03.0000.0000")] // the constant itself is never normalised
    public void MalformedSupportedConstant_Blocks_EvenWhenSourcesEqualIt(string supported)
    {
        var g = VersionGate.Evaluate(supported, new[] { new GameVersionSource("a", () => supported) });
        Assert.False(g.Passed);
        Assert.Contains("malformed", g.Reason);
    }

    [Fact]
    public void BlockedFactory_IsBlocked()
    {
        var g = VersionGate.Blocked(Supported, "probe threw");
        Assert.False(g.Passed);
        Assert.Null(g.DetectedVersion);
        Assert.Equal("probe threw", g.Reason);
        Assert.Contains("not read", g.DetectedDescription);
    }

    // ------------------------------------------------------------------ defence in depth

    [Fact]
    public void EnsurePassed_ThrowsUnlessPassed()
    {
        VersionGate.EnsurePassed(Both(Supported), "X"); // no throw

        var ex = Assert.Throws<InvalidOperationException>(() => VersionGate.EnsurePassed(Both("2026.11.04.0000.0000"), "DirectFriendRemover"));
        Assert.Contains("DirectFriendRemover", ex.Message);
        Assert.Contains("BLOCKED", ex.Message);
        Assert.Throws<InvalidOperationException>(() => VersionGate.EnsurePassed(null, "FriendListReader"));
        Assert.Throws<InvalidOperationException>(() => VersionGate.EnsurePassed(VersionGate.Blocked(Supported, "x"), "ReloadSuppressor"));
        Assert.Throws<InvalidOperationException>(() => VersionGate.EnsurePassed(Two(Supported, null), "ReloadSuppressor"));
    }

    [Fact]
    public void Gate_HasNoPublicConstructor_OrSettablePassed()
    {
        Assert.Empty(typeof(VersionGate).GetConstructors());
        Assert.Null(typeof(VersionGate).GetProperty(nameof(VersionGate.Passed))!.SetMethod);
    }

    private sealed class ThrowingList : IReadOnlyList<GameVersionSource>
    {
        public int Count => 2;

        public GameVersionSource this[int index] => throw new InvalidOperationException("indexer");

        public IEnumerator<GameVersionSource> GetEnumerator() => throw new InvalidOperationException("enumerate");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
