using CSharpToJava.Core.Analysis;
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
    /// <summary>
    /// 有意保留（设计上不提升），residual 闸门不应把它算作改写残留。
    /// 因引用有意保留的函数而级联跳过的调用者同样继承该标记。
    /// </summary>
    public bool IntentionallyRetained;
    public List<string> EnclosingTypeParams = new();  // type params from enclosing method
    public bool NeedsExplicitTypeArguments;  // true if hoisted method is generic and call sites need <TArgs>
    /// <summary>
    /// R1b: unified resolver plan (own + enclosing closure, three-state
    /// expressibility, stable shadowing names). The rewriter emits declarations,
    /// constraints and call-site names from this plan when present.
    /// </summary>
    public SyntheticTypeParameterPlan? TypeParameterPlan;
}

/// <summary>
/// One captured variable: its symbol, original name, parameter name, type, and whether it is written.
/// </summary>
internal sealed record CapturePlan(
    ISymbol Symbol,
    string OriginalName,
    string ParameterName,     // _cs2j_cap_xxx / _cs2j_cap_xxx_2
    string TypeDisplay,       // FullyQualifiedFormat
    bool IsWritten,           // source body writes the variable
    bool ByRef,               // final form: ref parameter (= IsWritten && !UseHolder)
    /// <summary>
    /// 该捕获改用 <c>System.Runtime.CompilerServices.StrongBox&lt;T&gt;</c> 闭包传递：
    /// 声明处的变量本身被改写为同名 holder，提升方法的形参是同名 StrongBox。
    /// 用于写入型捕获会落进 lambda（ref 形参非法，CS1628）的场景。
    /// </summary>
    bool UseHolder = false,
    /// <summary>T 在该声明/成员位置可解析的类型显示（holder 声明与形参共用）。</summary>
    string? HolderElementTypeDisplay = null);
