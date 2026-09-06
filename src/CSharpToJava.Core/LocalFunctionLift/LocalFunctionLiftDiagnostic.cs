namespace CSharpToJava.Core.LocalFunctionLift;

public enum LocalFunctionLiftSeverity { Info, Warning, Error }

public enum LocalFunctionSkipReason
{
    None, Iterator, NoBody, NoHostType, InterfaceHost, MethodGroupConversion,
    CheckedContext, UnsupportedCaptureType, RefCaptureUnavailable,
    EnclosingTypeParameterConflict, ParamsOrOptionalConflict,
    NamedArgumentCallSite, ReferencesSkippedLocalFunction, InternalError,
}

public sealed record LocalFunctionLiftDiagnostic(
    LocalFunctionLiftSeverity Severity,
    string Message,
    string? FilePath = null,
    int Line = 0,
    LocalFunctionSkipReason Reason = LocalFunctionSkipReason.None);
