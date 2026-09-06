namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Options for the local function lift preprocessor.
/// </summary>
/// <param name="CapturesAtEnd">If true, capture parameters go after original parameters (default false = front).</param>
/// <param name="AllowRefInAsyncCallers">If true, relax gate #8 for async/iterator call sites.</param>
/// <param name="AllowInterfaceHost">If true, relax gate #4 for interface host types.</param>
/// <param name="Verbose">If true, emit Info-level diagnostics.</param>
public sealed record LocalFunctionLiftOptions(
    bool CapturesAtEnd = false,
    bool AllowRefInAsyncCallers = false,
    bool AllowInterfaceHost = false,
    bool Verbose = false);
