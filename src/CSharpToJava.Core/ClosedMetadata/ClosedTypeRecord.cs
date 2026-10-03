using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// One deduplicated closed type discovered by the collector (design §5.3 phase 1):
/// its normalized key, the symbol itself, the fully-qualified <c>typeof</c> emission
/// text, the declaring-site file (project-ownership input, D6) and the enumeration
/// rule that found it first (CM-ENUM-1/CM-ENUM-2).
/// </summary>
public sealed record ClosedTypeRecord(
    ClosedTypeKey Key,
    ITypeSymbol Type,
    string TypeText,
    string DeclaringFilePath,
    string SourceRule);
