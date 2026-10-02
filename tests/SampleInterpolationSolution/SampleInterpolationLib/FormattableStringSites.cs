using System;

namespace SampleInterpolationLib;

/// <summary>
/// CS2J0511 preserve zone: an interpolation bound to FormattableString is an
/// explicit runtime shape (composite + boxed args) and is deliberately kept
/// verbatim. This is the ONLY file of the positive fixture whose
/// intermediate tree still contains an interpolation node; it pairs with the
/// CS2J0511 accounting in the whole-tree gate.
/// </summary>
public static class FormattableStringSites
{
    public static string TakeFormat(FormattableString fs)
        => fs.Format;

    public static string Describe()
        => TakeFormat($"y={2}");
}
