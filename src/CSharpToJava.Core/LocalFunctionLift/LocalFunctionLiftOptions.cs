namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Options for the local function lift preprocessor.
/// </summary>
/// <param name="CapturesAtEnd">If true, capture parameters go after original parameters (default false = front).</param>
/// <param name="AllowInterfaceHost">If true, relax gate #4 for interface host types.</param>
public sealed record LocalFunctionLiftOptions(
    bool CapturesAtEnd = false,
    bool AllowInterfaceHost = false);
