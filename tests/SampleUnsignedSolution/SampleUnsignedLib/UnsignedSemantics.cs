using System.Text;

namespace SampleUnsignedLib;

public static class UnsignedSemantics
{
    public static string RunAll()
    {
        var sb = new StringBuilder();
        AppendSection(sb, "complex", UnsignedComplex.Run());
        AppendSection(sb, "overflow", UnsignedOverflow.Run());
        AppendSection(sb, "conversions", UnsignedConversions.Run());
        AppendSection(sb, "mixed", UnsignedMixedOps.Run());
        AppendSection(sb, "postfix", UnsignedPostfix.Run());
        AppendSection(sb, "patterns", UnsignedPatterns.Run());
        AppendSection(sb, "collections", UnsignedCollections.Run());
        AppendSection(sb, "nullables", UnsignedNullables.Run());
        AppendSection(sb, "checked", UnsignedChecked.Run());
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string name, string content)
    {
        sb.AppendLine("== " + name + " ==");
        sb.Append(content);
    }
}
