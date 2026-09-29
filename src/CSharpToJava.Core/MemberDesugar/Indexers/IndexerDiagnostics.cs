namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>CS2J5130-5141 indexer 面消息文案（码位常量在
/// <see cref="MemberDesugarDiagnosticCode"/>；只放消息模板，对齐 PropertyDiagnostics）。</summary>
internal static class IndexerDiagnostics
{
    public const string SkipSuffix =
        "the indexer declaration and all of its use sites were left unchanged";

    public static string Partial(string name)
        => $"partial indexer '{name}' is not supported before partial-merge (C20); {SkipSuffix}.";

    public static string Unsupported(string name, string why)
        => $"{why} indexer '{name}' is conservatively skipped; {SkipSuffix}.";

    public static string ExpressionTree(string name)
        => $"indexer '{name}' is accessed inside an expression-tree lambda; rewriting would " +
           $"change the tree shape; {SkipSuffix} (overrides.force lifts this).";

    public static string Dynamic(string name)
        => $"indexer '{name}' may be accessed through a dynamic receiver; binding is " +
           $"undecidable; {SkipSuffix}.";

    public static string NonLiftable(string name)
        => $"indexer '{name}' is written in a compound/increment/initializer/value-consumed " +
           $"context that cannot be lifted to statements; {SkipSuffix}.";

    public static string NameCollision(string name)
        => $"generated accessor methods for indexer '{name}' collide with an existing " +
           $"same-signature member; {SkipSuffix}.";

    public static string Residual(string name)
        => $"indexer '{name}' has neither a plan nor a skip diagnostic (residual gate).";
}
