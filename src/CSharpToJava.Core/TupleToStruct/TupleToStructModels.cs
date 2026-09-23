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
