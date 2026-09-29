namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>CS2J5110-5129 property 面消息文案（码位常量在
/// <see cref="MemberDesugarDiagnosticCode"/>；本文件只放消息模板，便于统一措辞）。</summary>
internal static class PropertyDiagnostics
{
    public const string SkipSuffix =
        "the property declaration and all of its use sites were left unchanged";

    public static string PartialProperty(string name)
        => $"partial property '{name}' is not supported before partial-merge (C20); {SkipSuffix}.";

    public static string RefReturning(string name)
        => $"ref/ref-readonly returning property '{name}' couples with C01 ref lowering; {SkipSuffix}.";

    public static string StaticAbstractInterface(string name)
        => $"static abstract/virtual interface property '{name}' is gated behind C36; {SkipSuffix}.";

    public static string ExpressionTree(string name)
        => $"property '{name}' is accessed inside an expression-tree lambda; rewriting would " +
           $"change the tree shape; {SkipSuffix} (overrides.force lifts this).";

    public static string Dynamic(string name)
        => $"property name '{name}' is accessed through a dynamic receiver; binding is " +
           $"undecidable; {SkipSuffix}.";

    public static string NonLiftable(string name)
        => $"property '{name}' is assigned in an object initializer / compound assignment in a " +
           $"context that cannot be lifted to statements; {SkipSuffix}.";

    public static string FieldKeyword(string name)
        => $"an accessor of property '{name}' uses the C# 'field' contextual keyword; {SkipSuffix}.";

    public static string PositionalRecord(string name)
        => $"property '{name}' is a positional record parameter property (left to " +
           $"RecordTransformer; C27); {SkipSuffix}.";

    public static string WithSite(string name)
        => $"property '{name}' is referenced by a with-expression initializer " +
           $"(C27 prerequisite missing); {SkipSuffix}.";

    public static string AttributeUnroutable(string name, string attributeName)
        => $"attribute '{attributeName}' on property '{name}' is only applicable to properties " +
           $"and cannot be routed to the backing field or accessor methods; {SkipSuffix}.";
}
