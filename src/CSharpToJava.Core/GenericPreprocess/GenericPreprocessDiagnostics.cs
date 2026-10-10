namespace CSharpToJava.Core.GenericPreprocess;

public enum GenericPreprocessSeverity { Info, Warning, Error }

public enum GenericPattern
{
    DefaultExpression,   // default(T)
    NewTypeParam,        // new T()
    NewTypeParamArray,   // new T[n]
    TypeOfExpression,   // typeof(T)
    IsTypeParam,         // is T
    AsTypeParam,         // as T
}

public sealed record GenericPreprocessDiagnostic(
    GenericPreprocessSeverity Severity,
    GenericPattern Pattern,
    string Message,
    string? FilePath = null,
    int? Line = null);

public sealed class GenericPreprocessStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int DefaultExpressionsRewritten;
    public int NewTypeParamRewritten;
    public int NewTypeParamArrayRewritten;
    public int TypeOfExpressionsSeen;
    public int IsTypeParamSeen;
    public int AsTypeParamSeen;
}

public sealed record GenericPreprocessResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<GenericPreprocessDiagnostic> Diagnostics,
    GenericPreprocessStatistics Statistics);
