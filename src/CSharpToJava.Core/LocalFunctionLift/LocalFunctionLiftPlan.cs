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
    /// <summary>
    /// 局部函数声明在顶层语句里（<see cref="Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax"/>），
    /// 没有语法宿主类型。<see cref="HostType"/> 为 <see langword="null"/>；提升方法
    /// 由重写器合成一个文件内的私有静态类承载（见 <see cref="TopLevelHostClassName"/>）。
    /// </summary>
    public bool IsTopLevelHost;
    /// <summary>顶层宿主合成类的类名（同文件内所有顶层局部函数共用）。</summary>
    public string? TopLevelHostClassName;
    public bool IsStatic;
    public List<CapturePlan> Captures = new();    // ordered
    /// <summary>
    /// 被局部函数引用的外层 <c>const</c> 局部：常量不是捕获，提升后按值内联为
    /// 字面量（不生成形参、调用点不加实参），使常量语境保持可编译。
    /// </summary>
    public List<InlinedConstantPlan> InlinedConstants = new();
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
    /// <summary>
    /// 局部函数<em>声明点</em>所处的 checked/unchecked 上下文。提升方法体整体包一层
    /// 同名块即可精确复现该区域的算术溢出检查语义（体内显式的 checked/unchecked
    /// 表达式仍然局部覆盖，不受影响）。
    /// </summary>
    public CheckedContextKind CheckedContext = CheckedContextKind.None;
}

/// <summary>局部函数声明点所处的溢出检查上下文。</summary>
internal enum CheckedContextKind
{
    None,
    Checked,
    Unchecked,
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
    string? HolderElementTypeDisplay = null,
    /// <summary>
    /// 捕获的来源声明带 <c>scoped</c>（C# 11）：提升后的捕获形参必须保留该修饰符，
    /// 否则把 scoped 变量传给非 scoped 形参会被判定为可能逃逸（CS8352）。
    /// </summary>
    bool IsScoped = false,
    /// <summary>
    /// 只读 ref 局部（<c>ref readonly T x = ref ...</c>）的捕获形态：提升为
    /// <c>in T</c> 形参、调用点传 <c>in x</c>。只读语义与源完全等价；源的
    /// ref readonly 局部本身也不能被写。可写 ref 局部用 <see cref="ByRef"/>。
    /// </summary>
    bool ByIn = false);

/// <summary>
/// 外层 <c>const</c> 局部在提升方法里的内联形态：按名字/符号匹配后替换为
/// <see cref="LiteralText"/>（<c>v is Limit</c> → <c>v is 42</c>）。
/// </summary>
internal sealed record InlinedConstantPlan(
    ISymbol Symbol,
    string OriginalName,
    string LiteralText);
