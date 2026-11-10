using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.TypeShapeMarker;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Adapter for the <c>type-shape-marker</c> C#-to-C# pass. It appends empty
/// Cs2jPrelude marker interfaces to struct/record/readonly declarations so the
/// Java converter only sees ordinary <c>implements</c> clauses. No support
/// library is released here: Cs2jPrelude is already injected by the delegate
/// pass and referenced by every project.
/// </summary>
internal sealed class TypeShapeMarkerPassAdapter : ProjectPassAdapter
{
    private readonly bool _verbose;
    private readonly bool _filterUnsupportedProjects;
    private readonly IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private readonly bool _verifyBuild;
    private readonly bool _verifyTests;

    private readonly TypeShapeMarkerPreprocessor _preprocessor = new();
    private readonly List<string> _diagnostics = new();
    private int _filesTransformed;
    private int _typesMarked;
    private int _markersAdded;

    public TypeShapeMarkerPassAdapter(
        bool verbose,
        bool filterUnsupportedProjects,
        IReadOnlyCollection<string>? compatNamespacePrefixes,
        bool verifyBuild,
        bool verifyTests)
    {
        _verbose = verbose;
        _filterUnsupportedProjects = filterUnsupportedProjects;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _verifyBuild = verifyBuild;
        _verifyTests = verifyTests;
    }

    public override PassDescriptor Descriptor =>
        PassCatalog.ImplementedById(PassIds.TypeShapeMarker);
    public override bool Verbose => _verbose;
    public override bool FilterUnsupportedProjects => _filterUnsupportedProjects;
    public override IReadOnlyCollection<string>? CompatNamespacePrefixes =>
        _compatNamespacePrefixes;
    public override bool VerifyBuild => _verifyBuild;
    public override bool VerifyTests => _verifyTests;
    public override bool DeleteBuildOutputsAfterRestore => true;
    public override CSharpParseOptions CreateParseOptions(PassTreeContext tree)
        => new(LanguageVersion.Latest);
    public override string CompilationName => "type-shape-marker-preprocess";

    public override Task<PassFileRewrite?> RewriteFileAsync(PassFileContext context)
    {
        var result = _preprocessor.RewriteFile(
            context.SourceCode, context.SemanticModel!, context.RelativePath);

        if (!result.Changed)
        {
            return Task.FromResult<PassFileRewrite?>(null);
        }

        _filesTransformed++;
        _typesMarked += result.TypesMarked;
        _markersAdded += result.MarkersAdded;

        if (_verbose)
        {
            Console.WriteLine(
                $"[type-shape-marker] transformed: {context.RelativePath}");
        }

        return Task.FromResult<PassFileRewrite?>(new PassFileRewrite
        {
            OutputCode = result.OutputCode,
            Changed = true,
            PreserveBom = true,
        });
    }

    public override Task AfterFilesAsync(PassTreeContext tree)
        => Task.CompletedTask;

    public override void OnRestoreFailed(int exitCode, string restoreTarget)
        => _diagnostics.Add(
            $"dotnet restore failed with exit code {exitCode} for {restoreTarget}.");

    public override void OnBuildFailed(int exitCode)
        => _diagnostics.Add(
            $"dotnet build failed with exit code {exitCode} after type-shape-marker preprocessing.");

    public override void OnTestsFailed(int exitCode)
        => _diagnostics.Add(
            $"dotnet test failed with exit code {exitCode} after type-shape-marker preprocessing.");

    public override bool HasErrors => _diagnostics.Count > 0;

    public override string BuildChainSummaryLine()
        => $"type-shape-marker: {_filesTransformed} transformed, " +
           $"{_typesMarked} type(s) marked, {_markersAdded} marker(s) added";

    public override IReadOnlyList<string> BuildChainDiagnosticLines(bool verbose)
        => _diagnostics;
}
