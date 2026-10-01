namespace CSharpToJava.Core.EqualityConcat;

/// <summary>Severity of an equality/concat diagnostic.</summary>
public enum EqualityConcatSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>CS2J047x diagnostic codes (design §6). Code positions are append-only.</summary>
public enum EqualityConcatDiagnosticCode
{
    /// <summary>IOperation and symbols cannot determine the site (error).</summary>
    CS2J0470,

    /// <summary>Dynamically bound equality/concatenation site (error).</summary>
    CS2J0471,

    /// <summary>Site inside an expression tree was skipped (info).</summary>
    CS2J0472,

    /// <summary>Delegate equality materialization disabled or support source missing.</summary>
    CS2J0473,

    /// <summary>Lifted ==/!= site pending the C22 pass.</summary>
    CS2J0474,

    /// <summary>+= target cannot guarantee single evaluation.</summary>
    CS2J0475,

    /// <summary>Tuple/null comparison residual (E06 edge).</summary>
    CS2J0476,
}

/// <summary>Diagnostic emitted by the equality/concat preprocessor.</summary>
public sealed record EqualityConcatDiagnostic(
    EqualityConcatSeverity Severity,
    EqualityConcatDiagnosticCode Code,
    string Message,
    string? FilePath = null,
    int Line = 0,
    int Column = 0)
{
    /// <summary>MSBuild format: <c>file(line,col): warning CS2J047x: message</c>.</summary>
    public string ToMsBuildString()
    {
        var level = Severity switch
        {
            EqualityConcatSeverity.Error => "error",
            EqualityConcatSeverity.Warning => "warning",
            _ => "info",
        };
        var loc = FilePath != null ? $"{FilePath}({Line},{Column}): " : string.Empty;
        return $"{loc}{level} {Code}: {Message}";
    }
}
