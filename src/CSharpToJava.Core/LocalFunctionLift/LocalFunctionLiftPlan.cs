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
    public List<string> EnclosingTypeParams = new();  // type params from enclosing method
    public bool NeedsExplicitTypeArguments;  // true if hoisted method is generic and call sites need <TArgs>
    /// <summary>
    /// 局部函数名在条件编译区（本次分析编译未启用对应符号，即
    /// <c>DisabledTextTrivia</c>）里被引用：禁用区是不透明文本，重写器只能改活动代码。
    /// 标记后命名优先保留原名，使两种符号配置下所有引用都解析到同一个提升方法。
    /// </summary>
    public bool PreserveOriginalName;
    /// <summary>
    /// 原名与宿主类型成员冲突、必须改名时，对条件编译区里已确认指向本局部函数的
    /// 标识符做的精确替换（trivia 起始位置 + 区内偏移）。
    /// </summary>
    public List<DisabledTextIdentifierRename> DisabledTextRenames = new();
    /// <summary>
    /// R1b: unified resolver plan (own + enclosing closure, three-state
    /// expressibility, stable shadowing names). The rewriter emits declarations,
    /// constraints and call-site names from this plan when present.
    /// </summary>
    public SyntheticTypeParameterPlan? TypeParameterPlan;
}

/// <summary>
/// 条件编译区（<c>DisabledTextTrivia</c>）里的一个标识符替换：
/// <see cref="TriviaPosition"/> 是该 trivia 在原树中的起始位置，
/// <see cref="Offset"/>/<see cref="Length"/> 是标识符在 trivia 文本中的位置。
/// </summary>
internal sealed record DisabledTextIdentifierRename(
    int TriviaPosition,
    int Offset,
    int Length,
    string NewName);

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
