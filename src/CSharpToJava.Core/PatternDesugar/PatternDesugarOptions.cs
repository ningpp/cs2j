namespace CSharpToJava.Core.PatternDesugar;

/// <summary>
/// Options for the pattern statement desugar pass. Strict=true treats
/// unreducible targets (PD010/PD011) and residuals (PD020) as errors;
/// Strict=false downgrades them to warnings (never silently off).
/// </summary>
public sealed record PatternDesugarOptions(
    bool Strict = true,
    bool Verbose = false,
    IReadOnlyList<string>? ResidualWhitelist = null);
