using System;
using System.Globalization;
using SampleInterpolationLib;
using Xunit;

namespace SampleInterpolationTests;

/// <summary>
/// Runtime equivalence gate for C23: these assertions pass both on the
/// native solution and on the preprocessed intermediate tree (the chain
/// runs 'dotnet test' against the copy). This file deliberately contains no
/// interpolated strings of its own so the library files are the sole C23
/// subjects of the project-level residual scan.
/// </summary>
public static class InterpolationRuntimeTests
{
    private sealed class PinnedCulture : IDisposable
    {
        private readonly CultureInfo _previous;

        public PinnedCulture()
        {
            _previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        }

        public void Dispose()
            => CultureInfo.CurrentCulture = _previous;
    }

    [Fact]
    public static void ConcatMatrix()
    {
        Assert.Equal("plain text", ConcatSites.PureText());
        Assert.Equal("hi bob #7", ConcatSites.Greeting("bob", 7));
        Assert.Equal("78", ConcatSites.ConsecutiveHoles(7, 8));
        Assert.Equal("[x]", ConcatSites.Brackets("x"));
        Assert.Equal("[]", ConcatSites.NullBrackets());
        Assert.Equal("b=True", ConcatSites.Flag(true));
        Assert.Equal("color=Green", ConcatSites.ColorName(SignalColor.Green));
        Assert.Equal("z/3/False", ConcatSites.Mixed("z", 3, false));

        using (new PinnedCulture())
        {
            Assert.Equal("d=1.5", ConcatSites.DoublePlain(1.5));
        }
    }

    [Fact]
    public static void UnsignedMatrix()
    {
        Assert.Equal("7:8", UnsignedSites.UIntPair(7u, 8));
        Assert.Equal("u=9", UnsignedSites.ULong(9UL));
        Assert.Equal("255/1", UnsignedSites.BytePair(255, 1));
    }

    [Fact]
    public static void FormatMatrix()
    {
        Assert.Equal("[    7|7    ]", FormatSites.Align(7));
        Assert.Equal("0xFF", FormatSites.Hex(255));

        using (new PinnedCulture())
        {
            Assert.Equal("1.50", FormatSites.Fixed2(1.5));
        }

        Assert.Equal(
            "2026-03-04",
            FormatSites.YearMonthDay(new DateTime(2026, 3, 4)));
    }

    [Fact]
    public static void FoldNestedVerbatimRaw()
    {
        Assert.Equal("ab-WW", FoldNestedRawSites.Fold());
        Assert.Equal("ab7", FoldNestedRawSites.Nested(7));
        Assert.Equal(@"C:\tmp\3.txt", FoldNestedRawSites.VerbatimPath(3));
        Assert.Equal("a\"1b", FoldNestedRawSites.RawQuote(1));
    }

    [Fact]
    public static void FormattableStringSitePreserved()
    {
        // The site is preserved verbatim (CS2J0511); its runtime semantics are
        // the FormattableString composite, not an expanded concatenation.
        Assert.Equal("y={0}", FormattableStringSites.Describe());
    }

    [Fact]
    public static void CleanFileUnaffected()
    {
        Assert.Equal(5, CleanFile.Add(2, 3));
    }
}
