using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata;

/// <summary>
/// One deduplicated closed type discovered by the collector (design §5.3 phase 1):
/// its normalized key, the symbol itself, the fully-qualified <c>typeof</c> emission
/// text, the ownership file (D6: the closed type's own source declaration file when it
/// is user-declared, otherwise the discovery-site file) and the enumeration rule that
/// found it first (CM-ENUM-1..4).
/// </summary>
public sealed record ClosedTypeRecord(
    ClosedTypeKey Key,
    ITypeSymbol Type,
    string TypeText,
    string DeclaringFilePath,
    string SourceRule);
