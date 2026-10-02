using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Whole-tree analysis → PartialMergePlan (design §4.1). M1: collect groups,
/// build merged declarations depth-first (so nested merged nodes exist when the
/// containing type is built), emit one Replace edit for the primary and one
/// Remove edit per other part. Baseline snapshot/skip, CS2J2016 shape gates and
/// recompile-diff attribution arrive in M2.
/// </summary>
internal sealed class PartialMergeAnalyzer
{
    public PartialMergePlan Analyze(
        CSharpCompilation compilation,
        PartialMergeOptions options,
        string sourceRoot,
        IReadOnlyList<Action<TypeGroupPlan>>? groupMutators = null)
    {
        var diagnostics = new List<PartialMergeDiagnostic>();
        var (groups, topLevelSkipped) = PartialTypePartCollector.Collect(compilation, sourceRoot);

        // Baseline-first (design §4.1): groups already covered by a compiler error
        // are marked skip — the pass does not compete with compiler diagnostics.
        var baselineErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Location.SourceTree != null)
            .ToList();

        foreach (var group in groups)
        {
            if (baselineErrors.Any(error =>
                    group.Parts.Any(part =>
                        string.Equals(part.Declaration.SyntaxTree.FilePath,
                            error.Location.SourceTree!.FilePath, StringComparison.OrdinalIgnoreCase)
                        && part.Declaration.FullSpan.Contains(error.Location.SourceSpan.Start))))
            {
                group.SkipBaseline = true;
            }
        }

        // Shape gate (CS2J2016): a record primary-constructor parameter list may
        // appear on at most one part; multiple parts with a parameter list is a
        // baseline CS error and an explicitly unsupported merge shape (§4.5).
        foreach (var group in groups)
        {
            var parameterLists = group.Parts
                .Select(part => part.Declaration)
                .OfType<RecordDeclarationSyntax>()
                .Count(record => record.ParameterList != null);
            if (parameterLists > 1)
            {
                group.SkipUnsupportedShape = true;
                diagnostics.Add(new PartialMergeDiagnostic(
                    PartialMergeSeverity.Error,
                    PartialMergeCodes.UnsupportedShape,
                    "record primary-constructor parameter list appears on " + parameterLists +
                    " parts; group left untouched",
                    TypeFullName: group.TypeFullName));
            }
        }

        // Static lookup is per-analysis; reset so successive runs in one process
        // never see a previous tree's aliases.
        PartialMemberMerge.NestedOverrides.Clear();

        // Build deepest-first; after a group is built, register each part's text
        // as an alias for its merged node so containing types (built afterwards)
        // replace the nested part members. Switch to annotation keys if real
        // fixtures produce text-key collisions.
        foreach (var group in groups
                     .Where(g => !g.SkipBaseline && !g.SkipUnsupportedShape)
                     .OrderByDescending(g => g.TypeFullName.Count(c => c == '.')))
        {
            var merged = PartialTypeMergeBuilder.Build(compilation, group);
            group.MergedDeclaration = merged;

            // Test-only injection hook (InternalsVisibleTo); production callers
            // always pass null. Runs before edit serialization on purpose.
            if (groupMutators != null)
            {
                foreach (var mutator in groupMutators)
                {
                    mutator(group);
                }
            }

            foreach (var part in group.Parts)
            {
                // Primary slot carries the merged node inside the containing type;
                // non-primary nested parts are removed from it.
                PartialMemberMerge.NestedOverrides.TryAdd(
                    part.Declaration.ToFullString().Trim(),
                    new NestedOverride(merged, part.Ordinal != 0));
            }
        }

        // Nested groups emit no file-level edits: their merged nodes live inside
        // the containing merged declaration (member merge), and whole containing
        // declarations in other part files are removed anyway. Emitting edits for
        // them too would overlap the containing group's spans.
        var allPartNodes = groups
            .SelectMany(group => group.Parts.Select(part => part.Declaration))
            .ToHashSet();

        var edits = new List<FileEdit>();
        foreach (var group in groups)
        {
            if (group.MergedDeclaration is null)
            {
                continue;
            }

            var isNested = group.Primary.Declaration.Ancestors()
                .OfType<TypeDeclarationSyntax>()
                .Any(allPartNodes.Contains);
            if (isNested)
            {
                continue;
            }

            var primary = group.Primary;
            edits.Add(new ReplaceTypeEdit
            {
                FilePath = primary.SourceFilePath,
                Span = primary.Declaration.FullSpan,
                Replacement = group.MergedDeclaration.ToFullString(),
            });

            foreach (var otherPart in group.Parts.Skip(1))
            {
                edits.Add(new RemoveTypeEdit
                {
                    FilePath = otherPart.SourceFilePath,
                    Span = otherPart.Declaration.FullSpan,
                    Comment = "// cs2j-partial-merge: part of '" + group.TypeFullName +
                              "' merged into '" + primary.RelativePath + "'",
                });
            }
        }

        // Usings must follow the merged declaration: part-file attributes and
        // member bodies resolve through directives the primary file may lack.
        // Accumulate every built group's parts per primary file (nested groups
        // included — their parts may contribute directives) and emit one
        // zero-length insertion. Explicit global/static/alias forms are
        // preserved verbatim.
        foreach (var primaryFile in groups
                     .Where(g => g.MergedDeclaration is not null)
                     .GroupBy(g => g.Primary.SourceFilePath, StringComparer.OrdinalIgnoreCase))
        {
            var filePath = primaryFile.Key;
            var primaryUnit = primaryFile.First().Primary.Declaration
                .FirstAncestorOrSelf<CompilationUnitSyntax>()!;

            var existing = new HashSet<string>(
                primaryUnit.Usings.Select(UsingKey), StringComparer.Ordinal);
            var missing = new List<UsingDirectiveSyntax>();

            foreach (var group in primaryFile)
            {
                foreach (var part in group.Parts)
                {
                    var unit = part.Declaration.FirstAncestorOrSelf<CompilationUnitSyntax>()!;
                    foreach (var directive in unit.Usings)
                    {
                        if (existing.Add(UsingKey(directive)))
                        {
                            missing.Add(directive);
                        }
                    }
                }
            }

            if (missing.Count == 0)
            {
                continue;
            }

            var lines = string.Concat(missing.Select(directive =>
                directive.WithoutTrivia().ToFullString().TrimEnd() + "\r\n"));

            SyntaxNode? anchor = (SyntaxNode?)primaryUnit.Usings.LastOrDefault()
                ?? primaryUnit.Externs.LastOrDefault();
            int insertionPosition;
            string prefix;
            if (anchor is not null)
            {
                // Immediately after the last existing directive, start a new line.
                insertionPosition = anchor.Span.End;
                prefix = "\r\n";
            }
            else
            {
                // No directives at all: insert before the first member (namespace).
                insertionPosition = primaryUnit.Members.First().FullSpan.Start;
                prefix = string.Empty;
            }

            edits.Add(new InsertUsingsEdit
            {
                FilePath = filePath,
                Span = primaryFile.First().Primary.Declaration.FullSpan,
                InsertionPosition = insertionPosition,
                UsingsText = prefix + lines,
            });
        }

        // CS2J2017: heuristic analyzer/source-generator reference detection
        // (Info; only surfaced in verbose output).
        foreach (var projectFile in EnumerateProjectFiles(sourceRoot))
        {
            var text = File.ReadAllText(projectFile);
            if (text.Contains("<Analyzer Include", StringComparison.OrdinalIgnoreCase)
                || text.Contains("OutputItemType=\"Analyzer\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("SourceGenerator", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new PartialMergeDiagnostic(
                    PartialMergeSeverity.Info,
                    PartialMergeCodes.AnalyzerSeen,
                    "analyzer/source-generator reference detected; merged declaration keeps partial " +
                    "so generated parts remain valid (verify build is the semantic oracle)",
                    FilePath: projectFile));
            }
        }

        return new PartialMergePlan
        {
            Groups = groups,
            EditsByFile = edits.ToLookup(edit => edit.FilePath, StringComparer.OrdinalIgnoreCase),
            Diagnostics = diagnostics,
        };
    }

    private static string UsingKey(UsingDirectiveSyntax directive)
    {
        var kind = (directive.GlobalKeyword != default ? "g" : string.Empty)
            + (directive.StaticKeyword != default ? "s" : string.Empty);
        var alias = directive.Alias is null
            ? string.Empty
            : directive.Alias.Name.Identifier.Text + "=";
        return kind + alias + directive.Name.ToFullString().Trim();
    }

    private static IEnumerable<string> EnumerateProjectFiles(string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(
                     sourceRoot, "*.csproj", SearchOption.AllDirectories))
        {
            yield return file;
        }
    }
}
