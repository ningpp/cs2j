namespace CSharpToJava.Core.LexicalNormalize;

internal sealed class MutableLexicalStats
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int FilesDirtyNoRewrite;
    public int ResidualCount;
    public int LineDirectivesRemoved;
    public int ErrorDirectivesSeen;
    public int ResidualConditionalDirectives;
    public int ExternAliasDirectives;
    public int ExternAliasQualifiedSites;
    public int GlobalUsingsPlain;
    public int GlobalUsingsStatic;
    public int GlobalUsingsAlias;
    public int StaticUsings;
    public int DuplicateGlobalUsings;
    public int FileScopedNamespacesNormalized;

    private readonly Dictionary<string, int> _errorsByCode = new(StringComparer.Ordinal);

    public void AddError(string code)
        => _errorsByCode[code] = _errorsByCode.TryGetValue(code, out var count) ? count + 1 : 1;

    public LexicalNormalizeStatistics Build() => new()
    {
        FilesScanned = FilesScanned,
        FilesTransformed = FilesTransformed,
        FilesSkippedClean = FilesSkippedClean,
        FilesDirtyNoRewrite = FilesDirtyNoRewrite,
        ResidualCount = ResidualCount,
        LineDirectivesRemoved = LineDirectivesRemoved,
        ErrorDirectivesSeen = ErrorDirectivesSeen,
        ResidualConditionalDirectives = ResidualConditionalDirectives,
        ExternAliasDirectives = ExternAliasDirectives,
        ExternAliasQualifiedSites = ExternAliasQualifiedSites,
        GlobalUsingsPlain = GlobalUsingsPlain,
        GlobalUsingsStatic = GlobalUsingsStatic,
        GlobalUsingsAlias = GlobalUsingsAlias,
        StaticUsings = StaticUsings,
        DuplicateGlobalUsings = DuplicateGlobalUsings,
        FileScopedNamespacesNormalized = FileScopedNamespacesNormalized,
        ErrorsByCode = new Dictionary<string, int>(_errorsByCode, StringComparer.Ordinal),
    };
}
