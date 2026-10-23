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

    /// <summary>有意保留（非跳过、非缺陷）：原声明与使用点不变，且为语义正确所必需。</summary>
    public const string RetainSuffix =
        "the property is intentionally retained — its declaration and all of its use sites " +
        "were left unchanged";

    public static string RetainedMetadataInterface(string name)
        => $"property '{name}' implements a metadata (BCL/external) interface property; " +
           $"expanding it into get_/set_ methods would break interface implementation " +
           $"(CS0535/CS0470/CS0683); {RetainSuffix}.";

    public static string RetainedMergedMirror(string name)
        => $"the type of property '{name}' is merged from same-named declarations " +
           $"(partial + non-partial); expanding it would collide with the mirror " +
           $"declaration's accessor slots (CS0082/CS0111); {RetainSuffix}.";

    public static string RetainedAttributeClass(string name)
        => $"'{name}' is a property of a System.Attribute subclass referenced by name from " +
           $"attribute named arguments, which cannot be rewritten into method calls " +
           $"(expansion would orphan every named argument with CS0246); {RetainSuffix}.";

    public static string RetainedOverrideChain(string name)
        => $"'{name}' participates in an override chain / interface implementation whose " +
           $"members do not all qualify for accessor expansion; keeping only part of the " +
           $"chain would break the virtual slot (CS0115) or silently drop the override; " +
           $"{RetainSuffix}.";
}
