namespace CSharpToJava.Core.LocalFunctionLift;

public enum LocalFunctionLiftSeverity { Info, Warning, Error }

public enum LocalFunctionSkipReason
{
    None, Iterator, NoBody, NoHostType, InterfaceHost, MethodGroupConversion,
    CheckedContext, UnsupportedCaptureType, RefCaptureUnavailable,
    EnclosingTypeParameterConflict, ParamsOrOptionalConflict,
    NamedArgumentCallSite, ReferencesSkippedLocalFunction, InternalError, AmbiguousName,
    /// <summary>
    /// 写入型捕获在函数体内的嵌套 lambda / 匿名方法中被引用（ref 形参非法，CS1628），
    /// 且该捕获无法改写为 StrongBox holder（ref struct/指针/参数等）。普通局部变量
    /// 现在会改走 holder，不再触发该原因。
    /// </summary>
    RefCaptureInNestedLambda,
    /// <summary>写入型捕获是未初始化且只能靠函数体赋值的局部：调用点未确定赋值，ref 实参非法（CS0165）。</summary>
    RefCaptureNotDefinitelyAssigned,
    /// <summary>局部函数名在本次分析编译排除的条件编译区里被引用：无法重命名，真实编译该区域时会 CS0103。</summary>
    NameReferencedInDisabledText,
}

public sealed record LocalFunctionLiftDiagnostic(
    LocalFunctionLiftSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0,
    LocalFunctionSkipReason Reason = LocalFunctionSkipReason.None,
    /// <summary>
    /// 有意保留（不是改写未完成）：该局部函数按设计不提升，residual 闸门不应据此判失败。
    /// </summary>
    bool IntentionallyRetained = false);
