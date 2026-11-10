namespace CSharpToJava.Core.BigLiteralResource;

/// <summary>
/// Element kinds the pass can externalize. Deliberately restricted to the
/// element types that have a 1:1 Java representation, so the emitted loader call
/// converts without any wrapper: <c>byte</c>/<c>short</c>/<c>int</c>/<c>long</c>/
/// <c>float</c>/<c>double</c>/<c>char</c>/<c>bool</c>/<c>string</c>.
/// <c>sbyte</c>/<c>ushort</c>/<c>uint</c>/<c>ulong</c> are intentionally absent —
/// they are rewritten later by the <c>unsigned</c> pass into support-library
/// shapes, so extracting them here would fight that pass instead of helping it.
/// </summary>
public enum BigLiteralElementKind
{
    Byte,
    Int16,
    Int32,
    Int64,
    Single,
    Double,
    Char,
    Boolean,
    String,
}

/// <summary>Element-kind metadata: payload width and the loader entry point name.</summary>
public static class BigLiteralElementKinds
{
    /// <summary>
    /// Resolve a C# element type name (simple or qualified, e.g. <c>byte</c>,
    /// <c>System.Byte</c>, <c>Int32</c>, <c>string</c>, <c>String</c>) to a
    /// supported kind. Unknown types simply mean "not a candidate".
    /// </summary>
    public static bool TryResolve(string? elementTypeName, out BigLiteralElementKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(elementTypeName))
        {
            return false;
        }

        var name = elementTypeName!;
        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
        {
            name = name[(lastDot + 1)..];
        }

        name = name.Trim();
        switch (name)
        {
            case "byte" or "Byte":
                kind = BigLiteralElementKind.Byte;
                return true;
            case "short" or "Int16":
                kind = BigLiteralElementKind.Int16;
                return true;
            case "int" or "Int32":
                kind = BigLiteralElementKind.Int32;
                return true;
            case "long" or "Int64":
                kind = BigLiteralElementKind.Int64;
                return true;
            case "float" or "Single":
                kind = BigLiteralElementKind.Single;
                return true;
            case "double" or "Double":
                kind = BigLiteralElementKind.Double;
                return true;
            case "char" or "Char":
                kind = BigLiteralElementKind.Char;
                return true;
            case "bool" or "Boolean":
                kind = BigLiteralElementKind.Boolean;
                return true;
            case "string" or "String":
                kind = BigLiteralElementKind.String;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Whether the kind is stored as a fixed-width little-endian slot table.</summary>
    public static bool IsFixedWidth(BigLiteralElementKind kind)
        => kind != BigLiteralElementKind.String;

    /// <summary>Payload width of one element in bytes; 0 for variable-length kinds.</summary>
    public static int ElementWidth(BigLiteralElementKind kind) => kind switch
    {
        BigLiteralElementKind.Byte or BigLiteralElementKind.Boolean => 1,
        BigLiteralElementKind.Int16 or BigLiteralElementKind.Char => 2,
        BigLiteralElementKind.Int32 or BigLiteralElementKind.Single => 4,
        BigLiteralElementKind.Int64 or BigLiteralElementKind.Double => 8,
        _ => 0,
    };

    /// <summary>Loader method for a <c>T[]</c> payload of this kind.</summary>
    public static string ArrayLoaderMethod(BigLiteralElementKind kind) => kind switch
    {
        BigLiteralElementKind.Byte => "LoadBytes",
        BigLiteralElementKind.Int16 => "LoadInt16",
        BigLiteralElementKind.Int32 => "LoadInt32",
        BigLiteralElementKind.Int64 => "LoadInt64",
        BigLiteralElementKind.Single => "LoadSingle",
        BigLiteralElementKind.Double => "LoadDouble",
        BigLiteralElementKind.Char => "LoadChars",
        BigLiteralElementKind.Boolean => "LoadBooleans",
        BigLiteralElementKind.String => "LoadStrings",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Loader method for a single <c>string</c> payload.</summary>
    public static string StringLoaderMethod => "LoadString";
}

/// <summary>One extracted payload: the resource name plus its bytes.</summary>
public sealed class BigLiteralResourcePayload
{
    public required string ResourceName { get; init; }

    /// <summary>File name inside the resource directory (no directory part).</summary>
    public required string FileName { get; init; }

    /// <summary>Raw file content written next to the declaring source file.</summary>
    public required byte[] Content { get; init; }

    /// <summary>Index of the extracted declaration, for deterministic reporting.</summary>
    public required string SourceSymbol { get; init; }

    /// <summary>Byte size of <see cref="Content"/>.</summary>
    public int Size => Content.Length;
}

/// <summary>Severity of a <see cref="BigLiteralResourceDiagnostic"/>.</summary>
public enum BigLiteralResourceSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Diagnostic raised while extracting big literals. A record so the adapter can
/// stamp the owning file path with a <c>with</c> expression, exactly like
/// <c>DelegatePreprocessorDiagnostic</c>.
/// </summary>
public sealed record BigLiteralResourceDiagnostic
{
    public BigLiteralResourceDiagnostic(
        BigLiteralResourceSeverity severity,
        string code,
        string message,
        int line = 0)
    {
        Severity = severity;
        Code = code;
        Message = message;
        Line = line;
    }

    public BigLiteralResourceSeverity Severity { get; init; }

    /// <summary>CS2J43xx code from <c>PreprocessDiagnosticCodes</c>.</summary>
    public string Code { get; init; }

    public string Message { get; init; }

    /// <summary>1-based line of the declaration, 0 when unknown.</summary>
    public int Line { get; init; }

    /// <summary>Set by the adapter to the file's relative path.</summary>
    public string? FilePath { get; init; }
}

/// <summary>Per-file counters.</summary>
public sealed class BigLiteralResourceStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int ArraysExtracted { get; set; }
    public int StringsExtracted { get; set; }
    public int PayloadBytes { get; set; }
    public int SkippedNonLiteral { get; set; }
    public int SkippedConst { get; set; }
    public int SkippedUnsupportedText { get; set; }
}

/// <summary>Result of one file rewrite.</summary>
public sealed class BigLiteralResourceResult
{
    public required string OutputCode { get; init; }
    public required bool Changed { get; init; }
    public required IReadOnlyList<BigLiteralResourcePayload> Payloads { get; init; }
    public required IReadOnlyList<BigLiteralResourceDiagnostic> Diagnostics { get; init; }
    public required BigLiteralResourceStatistics Statistics { get; init; }
}
