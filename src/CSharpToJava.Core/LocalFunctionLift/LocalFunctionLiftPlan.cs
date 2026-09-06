using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Data contract between the analyzer and rewriter.
/// One instance per local function in the file.
/// </summary>
internal sealed class LiftPlan
{
    public required LocalFunctionStatementSyntax Node;
    public required IMethodSymbol Symbol;
    public required string NewName;
    public TypeDeclarationSyntax? HostType;      // insertion target (the declaration in this file)
    public bool IsStatic;
    public List<CapturePlan> Captures = new();    // ordered
    public bool Skipped;
    public LocalFunctionSkipReason SkipReason = LocalFunctionSkipReason.None;
}

/// <summary>
/// One captured variable: its symbol, original name, parameter name, type, and whether it is written.
/// </summary>
internal sealed record CapturePlan(
    ISymbol Symbol,
    string OriginalName,
    string ParameterName,     // _cs2j_cap_xxx / _cs2j_cap_xxx_2
    string TypeDisplay,       // FullyQualifiedFormat
    bool IsWritten,           // -> ref parameter
    bool ByRef);              // final form (= IsWritten)
