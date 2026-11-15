namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Options for the local function lift preprocessor.
/// </summary>
/// <param name="CapturesAtEnd">If true, capture parameters go after original parameters (default false = front).</param>
/// <param name="AllowInterfaceHost">
/// If true, relax gate #4 for interface host types: local functions inside default interface
/// method bodies are lifted to <c>private</c> interface methods (legal since C# 8; the analyzer
/// still skips them when the project's LanguageVersion is below C# 8).
/// Defaults to false for library callers (the hoisted method changes the host type's member
/// set), while both CLI entry points pass <c>true</c> explicitly.
/// </param>
public sealed record LocalFunctionLiftOptions(
    bool CapturesAtEnd = false,
    bool AllowInterfaceHost = false);
