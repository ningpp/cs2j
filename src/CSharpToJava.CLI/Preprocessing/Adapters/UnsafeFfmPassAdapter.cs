using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the C06 unsafe-ffm pass. Reuses the standard
/// <see cref="ProjectPassAdapter"/> spine: the runner parses/compiles the whole
/// tree (compilation options keep allowUnsafe=true), this adapter rewrites each
/// file through <see cref="UnsafeFfmPreprocessor"/> (R1 unsafe-context stripping,
/// M4 base rules R7 ref struct declaration rejection / InlineArray detection /
/// R9 ref struct using normalization, M1 R2/R3 fixed lowering, M2 R4 stackalloc
/// heap-allocating, M3 R5 pointer signature whitelist and R6 pointer-expression
/// indexization, M4 R8 extern/interop site rejection UF060/UF070), and the
/// runner restores and verifies build/tests. There is no support library to
/// release (PassSupportLibrary.None) and no csproj injection; AfterFiles needs
/// no extra work. The FFM whitelist facade and Java FFM emission channel are a
/// later independent opt-in sub-segment; ExternWhitelistPath is not consumed.
/// </summary>
internal sealed class UnsafeFfmPassAdapter : ProjectPassAdapter
{
    private readonly UnsafeFfmOptions _options;
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly UnsafeFfmPreprocessor _preprocessor;
    private readonly List<UnsafeFfmDiagnostic> _diagnostics = new();
    private readonly UnsafeFfmStatistics _statistics = new();

    public UnsafeFfmPassAdapter(
        UnsafeFfmOptions options,
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _options = options;
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;

        _preprocessor = new UnsafeFfmPreprocessor(_options);
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassStubCatalog.Ids.UnsafeFfm);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "unsafe-ffm-preprocess";

    public UnsafeFfmStatistics Statistics => _statistics;
    public IReadOnlyList<UnsafeFfmDiagnostic> Diagnostics => _diagnostics;

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.RewriteFile(
            context.SourceCode, context.SemanticModel!);
        MergeStatistics(result.Statistics);

        foreach (var diagnostic in result.Diagnostics)
        {
            _diagnostics.Add(diagnostic with { FilePath = context.RelativePath });
        }

        if (_verbose)
        {
            Console.WriteLine(
                $"[unsafe-ffm] {(result.Changed ? "transformed" : "clean (copy)")}: {context.RelativePath}");
        }

        return Task.FromResult(result.Changed
            ? new PassFileRewrite { OutputCode = result.OutputCode, Changed = true, PreserveBom = true }
            : (PassFileRewrite?)null);
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
        // 无支撑库释放、无 csproj 注入（support=None）；标准 spine 已完成全部落盘。
        => Task.CompletedTask;

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Warning,
            UnsafeFfmDiagnosticCode.UF010,
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.",
            restoreTarget, 0, 0));

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error,
            UnsafeFfmDiagnosticCode.UF010,
            $"dotnet build failed with exit code {exitCode} after unsafe-ffm preprocessing. " +
            "The unsafe-context stripping produced code that does not compile."));

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error,
            UnsafeFfmDiagnosticCode.UF010,
            $"dotnet test failed with exit code {exitCode} after unsafe-ffm preprocessing."));

    public override bool HasErrors
        => _diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    public override string BuildChainSummaryLine()
        => $"unsafe-ffm: {_statistics.UnsafeBlocksStripped} unsafe block(s), " +
           $"{_statistics.UnsafeModifiersStripped} unsafe modifier(s) stripped " +
           $"({_statistics.UnsafeTypeModifiersStripped} type, " +
           $"{_statistics.UnsafeMethodModifiersStripped} method, " +
           $"{_statistics.UnsafeFieldModifiersStripped} field, " +
           $"{_statistics.UnsafeConstructorModifiersStripped} ctor, " +
           $"{_statistics.UnsafeLocalFunctionModifiersStripped} local function); " +
           $"{_statistics.FixedBufferFieldsRewritten} fixed buffer field(s), " +
           $"{_statistics.FixedStatementsCount} fixed statement(s) " +
           $"({_statistics.FixedVariablesTotal} pointer(s): " +
           $"{_statistics.FixedVariablesArray} array, " +
           $"{_statistics.FixedVariablesOffset} offset, " +
           $"{_statistics.FixedVariablesAlias} alias, " +
           $"{_statistics.FixedVariablesNull} null, " +
           $"{_statistics.FixedVariablesString} string, " +
           $"{_statistics.FixedVariablesScalar} scalar); " +
           $"{_statistics.ExplicitStackAllocCollected} explicit stackalloc(s), " +
           $"{_statistics.ImplicitStackAllocCollected} implicit stackalloc(s) heap-allocated; " +
           $"{_statistics.PointerSignaturesRewritten} pointer signature(s) " +
           $"({_statistics.PointerParametersRewritten} parameter(s), " +
           $"{_statistics.PointerReturnsRewritten} return(s); " +
           $"{_statistics.PointerLocalsRewritten} local(s), " +
           $"{_statistics.PointerLengthPatterns} pointer+length pattern(s)), " +
           $"{_statistics.PointerExprsIndexized} pointer expression(s) indexized; " +
           $"{_statistics.SpanUsingNoOps} Span using(s) no-op normalized; " +
           $"{_statistics.Rejections} rejection(s) (UF020={_statistics.Uf020Rejections}, " +
           $"UF021={_statistics.Uf021Rejections}, UF022={_statistics.Uf022Rejections}, " +
           $"UF030={_statistics.Uf030Rejections}, UF031={_statistics.Uf031Rejections}, " +
           $"UF040={_statistics.Uf040Rejections}, UF050={_statistics.Uf050Rejections}, " +
           $"UF051={_statistics.Uf051Rejections}, UF055={_statistics.Uf055Rejections}, " +
           $"UF060={_statistics.Uf060Rejections}, UF070={_statistics.Uf070Rejections}), " +
           $"{_statistics.FilesTransformed} file(s)";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics
            .Where(d => d.Severity != UnsafeFfmSeverity.Info || verbose)
            .Select(d => d.ToMsBuildString())
            .ToList();

    private void MergeStatistics(UnsafeFfmStatistics source)
    {
        _statistics.FilesScanned += source.FilesScanned;
        _statistics.FilesTransformed += source.FilesTransformed;
        _statistics.FilesSkippedClean += source.FilesSkippedClean;
        _statistics.UnsafeBlocksStripped += source.UnsafeBlocksStripped;
        _statistics.UnsafeTypeModifiersStripped += source.UnsafeTypeModifiersStripped;
        _statistics.UnsafeMethodModifiersStripped += source.UnsafeMethodModifiersStripped;
        _statistics.UnsafeFieldModifiersStripped += source.UnsafeFieldModifiersStripped;
        _statistics.UnsafeConstructorModifiersStripped += source.UnsafeConstructorModifiersStripped;
        _statistics.UnsafeLocalFunctionModifiersStripped += source.UnsafeLocalFunctionModifiersStripped;
        _statistics.UnsafeOtherModifiersStripped += source.UnsafeOtherModifiersStripped;
        _statistics.Rejections += source.Rejections;
        _statistics.ResidualCount += source.ResidualCount;
        _statistics.ExplicitStackAllocCollected += source.ExplicitStackAllocCollected;
        _statistics.ImplicitStackAllocCollected += source.ImplicitStackAllocCollected;
        _statistics.FixedBufferFieldsRewritten += source.FixedBufferFieldsRewritten;
        _statistics.FixedStatementsCount += source.FixedStatementsCount;
        _statistics.FixedVariablesArray += source.FixedVariablesArray;
        _statistics.FixedVariablesOffset += source.FixedVariablesOffset;
        _statistics.FixedVariablesAlias += source.FixedVariablesAlias;
        _statistics.FixedVariablesNull += source.FixedVariablesNull;
        _statistics.FixedVariablesString += source.FixedVariablesString;
        _statistics.FixedVariablesScalar += source.FixedVariablesScalar;
        _statistics.Uf020Rejections += source.Uf020Rejections;
        _statistics.Uf030Rejections += source.Uf030Rejections;
        _statistics.Uf031Rejections += source.Uf031Rejections;
        _statistics.Uf040Rejections += source.Uf040Rejections;
        _statistics.PointerParametersRewritten += source.PointerParametersRewritten;
        _statistics.PointerReturnsRewritten += source.PointerReturnsRewritten;
        _statistics.PointerLocalsRewritten += source.PointerLocalsRewritten;
        _statistics.PointerLengthPatterns += source.PointerLengthPatterns;
        _statistics.PointerExprsIndexized += source.PointerExprsIndexized;
        _statistics.Uf021Rejections += source.Uf021Rejections;
        _statistics.Uf022Rejections += source.Uf022Rejections;
        _statistics.Uf050Rejections += source.Uf050Rejections;
        _statistics.Uf051Rejections += source.Uf051Rejections;
        _statistics.Uf055Rejections += source.Uf055Rejections;
        _statistics.Uf060Rejections += source.Uf060Rejections;
        _statistics.Uf070Rejections += source.Uf070Rejections;
        _statistics.SpanUsingNoOps += source.SpanUsingNoOps;
    }
}
