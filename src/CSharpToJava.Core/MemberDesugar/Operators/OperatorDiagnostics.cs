namespace CSharpToJava.Core.MemberDesugar.Operators;

/// <summary>CS2J5170-5183 消息（设计 §6）。格式统一 `[符号] 消息`，符号名由调用方拼接。</summary>
internal static class OperatorDiagnostics
{
    public static string PlanCreated(string name, int siteCount)
        => $"operator plan created ({name}, {siteCount} site(s))";
    public static string ConversionPlanCreated(string name, string targetType, string kind)
        => $"conversion operator plan created ({name}, {kind} -> {targetType})";
    public static string NameDisambiguated(string baseName, string actualName)
        => $"generated name '{baseName}' collides with an existing member; using '{actualName}'";
    public static string ExpressionTree(string name)
        => $"operator/conversion '{name}' is referenced inside an expression-tree lambda; " +
           "the plan (declaration and all sites) is skipped atomically (overrides.force can release this)";
    public static string Dynamic(string name)
        => $"operator '{name}' may be dynamically dispatched; the plan is skipped atomically " +
           "(force does not release this)";
    public static string StaticAbstractInterface(string name)
        => $"interface static abstract/virtual operator '{name}' (C# 11 generic math) is not supported; " +
           "plan skipped";
    public static string UnbuildableLift(string name)
        => $"lifted operator/conversion '{name}' site cannot be rebuilt from the operation " +
           "(non-simple operand or unsupported lifted shape); plan skipped";
    public static string MirrorMiss(string name, string targetType)
        => $"conversion target type '{targetType}' is outside the naming mirror table; " +
           "plan skipped (sdk triage)";
    public static string NameExhausted(string baseName)
        => $"name reservation for '{baseName}' exhausted disambiguation candidates; plan skipped";
    public static string CompoundUnsupported(string name, string reason)
        => $"compound-assignment target is unsupported ({reason}); the operator plan " +
           $"'{name}' is skipped atomically";
    public static string GateRetained(string typeName, string opName)
        => $"gate type '{typeName}' declares op_True/op_False; '{opName}' is retained " +
           "for the java-keywords channel";
    public static string IncrementDeferred(string typeName)
        => $"op_Increment/op_Decrement on '{typeName}' is deferred to C17; " +
           "declaration and sites are untouched";
    public static string MetadataSite(string opName)
        => $"site binds metadata-only operator '{opName}' (BCL/compat); left untouched";
    public static string Residual(string name)
        => $"operator/conversion declaration '{name}' has neither a plan nor a skip diagnostic " +
           "(residual gate)";
}
