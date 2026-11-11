namespace CSharpToJava.Core.LocalFunctionLift;

public enum LocalFunctionLiftSeverity { Info, Warning, Error }

public enum LocalFunctionSkipReason
{
    None, Iterator, NoBody, NoHostType, InterfaceHost, MethodGroupConversion,
    CheckedContext, UnsupportedCaptureType, RefCaptureUnavailable,
    EnclosingTypeParameterConflict, ParamsOrOptionalConflict,
    NamedArgumentCallSite, ReferencesSkippedLocalFunction, InternalError, AmbiguousName,
    /// <summary>写入型捕获在函数体内的嵌套 lambda / 匿名方法中被引用（ref 形参非法，CS1628）。</summary>
    RefCaptureInNestedLambda,
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
