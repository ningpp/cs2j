namespace AssignmentLib;

/// <summary>
/// C51 null-conditional assignment sites: every terminal here is lowered by
/// the assignment sub-rule (guarded if for statements, null ternary for
/// values; compound/coalesce/chain shapes). The plain coalesce site at the
/// end deliberately has no <c>?.</c>, covering the E03 interaction quadrant.
/// </summary>
public static class AssignmentSites
{
    // NA-1 member / NA-2 indexer.
    public static void Member(Node? n, int v) { n?.Field = v; }

    public static void Property(Node? n, int v) { n?.Prop = v; }

    public static void Index(Node? n, int i, int v) { n?[i] = v; }

    // NA-3 compound (field and custom event accessor).
    public static void Add(Node? n, int v) { n?.Field += v; }

    public static void EventOn(Node? n, Action h) { n?.Ping += h; }

    // NA-6 ??= inside a conditional access.
    public static void Coalesce(Node? n, string v) { n?.Label ??= v; }

    // NA-4 chains: M8 statement and value forms.
    public static void Chain(Node? n, int v) { n?.Next?.Field = v; }

    public static int? ValueForm(Node? n, int v) { return n?.Field = v; }

    public static string? CoalesceValue(Node? n, string v)
    {
        return n?.Label ??= v;
    }

    // E03 quadrant: plain ??= (no conditional access) keeps its semantics.
    public static void PlainCoalesce(Node n, string v) { n.Label ??= v; }
}
