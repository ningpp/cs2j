using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.TypeShapeMarker;

/// <summary>
/// Single-file result of <see cref="TypeShapeMarkerPreprocessor"/>.
/// </summary>
public sealed record TypeShapeMarkerResult
{
    public required string OutputCode { get; init; }

    public required bool Changed { get; init; }

    /// <summary>Number of type declarations that received at least one marker.</summary>
    public required int TypesMarked { get; init; }

    /// <summary>Total number of marker interfaces appended.</summary>
    public required int MarkersAdded { get; init; }
}

/// <summary>
/// C#-to-C# pass that carries C# type modifiers Java cannot express as empty
/// marker interfaces. The converted Java class then only has to implement the
/// mapped compat interface; the converter needs no special case for records,
/// structs, readonly structs or lowered ref structs.
/// <para>
/// Markers are chosen from the semantic symbol (not from spellings in one
/// particular source file), so partial declarations, generated shapes and
/// record/struct combinations all resolve consistently. The pass is idempotent,
/// preserves every existing base type, and skips injected support libraries and
/// declarations that still carry the <c>ref</c> modifier (C# forbids those from
/// implementing interfaces before C# 13; ref-struct-lowering owns them).
/// </para>
/// </summary>
public sealed class TypeShapeMarkerPreprocessor
{
    private const string MarkerNamespace = "global::Cs2jPrelude.";

    private const string StructMarker = "Cs2jStructMarker";
    private const string RecordMarker = "Cs2jRecordMarker";
    private const string ReadOnlyMarker = "Cs2jReadOnlyMarker";

    /// <summary>
    /// Injected support libraries are symbol sources only: they never enter the
    /// Java conversion, and marking them would make the marker interfaces
    /// reference themselves. Kept in sync with normalization-audit's skip list.
    /// </summary>
    private static readonly string[] SupportLibraryRoots =
    [
        "Cs2jPrelude",
        "Cs2jArray",
        "Cs2jRefOutIn",
        "Cs2jUnsignedMath",
        "Cs2jTuples",
    ];

    public TypeShapeMarkerResult RewriteFile(
        string sourceCode, SemanticModel semanticModel, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);

        if (IsSupportLibraryPath(relativePath))
        {
            return Unchanged(sourceCode);
        }

        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var rewriter = new MarkerRewriter(semanticModel);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (rewriter.TypesMarked == 0)
        {
            return Unchanged(sourceCode);
        }

        var outputCode = newRoot.ToFullString();
        if (!ParsesWithoutErrors(outputCode))
        {
            // Keep the original tree byte-for-byte: a malformed rewrite must
            // never reach the C# verification build.
            return Unchanged(sourceCode);
        }

        return new TypeShapeMarkerResult
        {
            OutputCode = outputCode,
            Changed = true,
            TypesMarked = rewriter.TypesMarked,
            MarkersAdded = rewriter.MarkersAdded,
        };
    }

    private static bool IsSupportLibraryPath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var separator = normalized.IndexOf('/');
        var topLevel = separator >= 0 ? normalized[..separator] : normalized;
        return SupportLibraryRoots.Any(
            root => string.Equals(root, topLevel, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class MarkerRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel _model;

        public MarkerRewriter(SemanticModel model)
            : base(visitIntoStructuredTrivia: false)
            => _model = model;

        public int TypesMarked { get; private set; }

        public int MarkersAdded { get; private set; }

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        {
            var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
            return Mark(node, visited);
        }

        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
        {
            var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
            return Mark(node, visited);
        }

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
            // A plain C# class has a Java counterpart; only record classes
            // (which are RecordDeclarationSyntax) carry a shape marker, but
            // visiting children here is required for nested types.
            return visited;
        }

        private TypeDeclarationSyntax Mark(
            TypeDeclarationSyntax original, TypeDeclarationSyntax visited)
        {
            // A ref struct that lowering intentionally preserved still has the
            // `ref` modifier; C# may reject an interface on it. Skip those.
            if (original.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
            {
                return visited;
            }

            if (_model.GetDeclaredSymbol(original) is not INamedTypeSymbol symbol
                || symbol.IsStatic
                || symbol.TypeKind is TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate)
            {
                return visited;
            }

            var markers = new List<string>(3);
            if (symbol.IsValueType)
            {
                markers.Add(StructMarker);
            }

            if (symbol.IsRecord)
            {
                markers.Add(RecordMarker);
            }

            if (symbol.IsReadOnly)
            {
                markers.Add(ReadOnlyMarker);
            }

            if (markers.Count == 0)
            {
                return visited;
            }

            var existing = visited.BaseList?.Types
                .Select(t => t.Type.ToString())
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
            var toAdd = markers
                .Where(m => !existing.Any(e => e.EndsWith(m, StringComparison.Ordinal)))
                .ToList();

            if (toAdd.Count == 0)
            {
                return visited;
            }

            TypesMarked++;
            MarkersAdded += toAdd.Count;
            return AppendMarkers(visited, toAdd);
        }

        private static TypeDeclarationSyntax AppendMarkers(
            TypeDeclarationSyntax declaration, IReadOnlyList<string> markerNames)
        {
            var types = new List<BaseTypeSyntax>();
            foreach (var name in markerNames)
            {
                var type = SyntaxFactory.ParseTypeName(MarkerNamespace + name);
                types.Add(SyntaxFactory.SimpleBaseType(
                    type.WithLeadingTrivia(SyntaxFactory.Space)));
            }

            if (declaration.BaseList is { } existingBaseList)
            {
                return declaration.WithBaseList(existingBaseList.AddTypes([.. types]));
            }

            var colon = SyntaxFactory.Token(SyntaxKind.ColonToken)
                .WithTrailingTrivia(SyntaxFactory.Space);
            if (!declaration.Identifier.HasTrailingTrivia)
            {
                colon = colon.WithLeadingTrivia(SyntaxFactory.Space);
            }

            // The first marker must not inherit the leading space reserved for
            // the separator after a comma.
            types[0] = SyntaxFactory.SimpleBaseType(
                SyntaxFactory.ParseTypeName(MarkerNamespace + markerNames[0]));
            return declaration.WithBaseList(
                SyntaxFactory.BaseList(colon, SyntaxFactory.SeparatedList(types))
                    .WithTrailingTrivia(SyntaxFactory.Space));
        }
    }

    private static bool ParsesWithoutErrors(string code)
    {
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(
                code, new CSharpParseOptions(LanguageVersion.Latest));
            return !reparsed.GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }

    private static TypeShapeMarkerResult Unchanged(string sourceCode)
        => new()
        {
            OutputCode = sourceCode,
            Changed = false,
            TypesMarked = 0,
            MarkersAdded = 0,
        };
}
