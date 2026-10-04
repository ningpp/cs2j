namespace CSharpToJava.Core.AnonymousTypeLift;

public enum AnonymousTypeLiftSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 诊断条目。码位全集（AL001/AL002/AL010/AL020/AL030/AL040/AL050/AL060/AL070）
/// 见 C21 设计 §6；本阶段实际只产生 AL002/AL010/AL060。
/// </summary>
public sealed record AnonymousTypeLiftDiagnostic(
    string Code,
    AnonymousTypeLiftSeverity Severity,
    string Message);

/// <summary>Per-project counters surfaced in the CLI summary line.</summary>
public sealed class AnonymousTypeLiftStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int ShapesRegistered { get; set; }
    public int GenericShapes { get; set; }
    public int DynamicShapes { get; set; }
    public int NestedShapes { get; set; }
    public int CreationsRewritten { get; set; }
    public int TypesGenerated { get; set; }
    public int CrossCompilationCopies { get; set; }
    public int ResidualCreations { get; set; }
}

public sealed class AnonymousTypeLiftOptions
{
    /// <summary>Namespace holding the generated anonymous-shape classes.</summary>
    public string NamespaceName { get; init; } = "Cs2jAnonymous";

    /// <summary>File name of the shared generated class file written to the intermediate root.</summary>
    public string GeneratedFileName { get; init; } = "_Cs2jAnonymousTypes.cs";
}

public sealed record AnonymousTypeLiftFileResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
}
