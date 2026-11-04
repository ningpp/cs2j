namespace CSharpToJava.Core.TupleToStruct;

public enum TupleToStructSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record TupleToStructDiagnostic(TupleToStructSeverity Severity, string Message);

/// <summary>Per-project counters surfaced in the CLI summary line.</summary>
public sealed class TupleToStructStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int TupleTypesReplaced { get; set; }
    public int TupleLiteralsRewritten { get; set; }
    public int ValueTupleCreatesRewritten { get; set; }
    public int DeconstructionsExpanded { get; set; }
    public int MemberAccessesRewritten { get; set; }
    public int CastsRewritten { get; set; }
    public int ImplicitConversionsRewritten { get; set; }
    public int ComparisonsRewritten { get; set; }

    /// <summary>
    /// 局部声明由显式元组类型改写为 <c>var</c> 的次数。仅针对 tuple pass 最终仍保留
    /// 原生 <c>TupleTypeSyntax</c> 的局部声明（Java 门禁只卡局部声明的元组类型）；
    /// 已被降级为 Cs2jTuples struct 的声明不计入。
    /// </summary>
    public int TupleLocalsNormalizedToVar { get; set; }

    public int StructsGenerated { get; set; }
}

public sealed class TupleToStructOptions
{
    /// <summary>Namespace holding the generated tuple structs.</summary>
    public string NamespaceName { get; init; } = "Cs2jTuples";

    /// <summary>File name of the shared generated struct file written to the intermediate root.</summary>
    public string GeneratedFileName { get; init; } = "_Cs2jTupleTypes.cs";
}

public sealed record TupleToStructFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
