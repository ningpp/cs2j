using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Whole-tree analysis → PartialMergePlan (design §4.1). Collects groups, builds
/// merged declarations depth-first (so nested merged nodes exist when the
/// containing type is built), emits one Replace edit for the primary and one
/// Remove edit per other part. Baseline snapshot/skip, CS2J2016 shape gates,
/// recompile-diff attribution and M3 member-pair collapse.
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
        var stats = new MutablePartialMergeStats();
        List<TypeGroupPlan> multiGroups;
        List<TypeGroupPlan> singleTypes;
        int topLevelSkipped;
        int typesScanned;
        (multiGroups, singleTypes, topLevelSkipped, typesScanned) =
            PartialTypePartCollector.Collect(
                compilation, sourceRoot, nameSinglePartTypes: options.AssumeNoGeneratedPartial);
        stats.TypesScanned = typesScanned;
        stats.SkippedTopLevel = topLevelSkipped;

        // M3: single-part types participate only under the opt-in generator-absence
        // assumption, and only when they actually carry a removable classic method.
        var units = new List<TypeGroupPlan>(multiGroups);
        var collapse = new PartialMemberCollapse(
            compilation, options, stats, diagnostics, sourceRoot);

        // Baseline-first (design §4.1): groups already covered by a compiler error
        // are marked skip — the pass does not compete with compiler diagnostics.
        //
        // DELIBERATE NARROWING (authorised; corpus-verified byte-identical). The gate
        // reads only Roslyn's two compilation-wide, internally-memoized stages — parse
        // and declaration — and drops the method-body stage the earlier per-part
        // GetDiagnostics(part.FullSpan) still folded in. A group is skipped exactly
        // when a parse or declaration error starts inside one of its part
        // declarations.
        //
        // No cheaper narrowing that KEEPS the method-body stage exists (three rounds
        // of measurement): passing part.FullSpan to GetDiagnostics /
        // GetMethodBodyDiagnostics does not skip any binding — the engine computes the
        // tree's diagnostics and then filters by span — and per-tree
        // GetMethodBodyDiagnostics re-binds and costs more. The original whole-tree
        // GetDiagnostics was exactly the union of the three stages, of which
        // method-body is ~2.9s of the gate's ~4.4s while parse + declaration, paid
        // once and memoized, are ~1.5s. Risk surface on this corpus: 16 part spans
        // hold a method-body-only error, but every one of their groups is also
        // parse/declaration-covered, so 0 groups change verdict (oldSkip = newSkip =
        // 10). The adapter's BaselineKeysForFile keeps all three stages, so a
        // pre-existing method-body error in a rewritten file still suppresses a
        // spurious diff.
        // CoversBaselineError only matches an error whose location lies INSIDE one of
        // a group's part declarations, i.e. in that part's own syntax tree, so the
        // single compilation-wide declaration stage is indexed by tree once: a part
        // then only ever scans its own tree's errors instead of all ~11000. The parse
        // stage is not read separately — GetDeclarationDiagnostics() already includes
        // the earlier (parse) stage, so it carries nothing the declaration stage does
        // not (corpus-verified: the two-stage view equals the per-part-tree
        // GetDeclarationDiagnostics view on all 112 parts, id/span/message identical).
        Dictionary<SyntaxTree, List<Diagnostic>>? declErrorsByTreeCache = null;
        Dictionary<SyntaxTree, List<Diagnostic>> DeclErrorsByTree()
        {
            if (declErrorsByTreeCache is not null)
            {
                return declErrorsByTreeCache;
            }

            var byTree = new Dictionary<SyntaxTree, List<Diagnostic>>();
            foreach (var diagnostic in compilation.GetDeclarationDiagnostics())
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error
                    || diagnostic.Location.SourceTree is null)
                {
                    continue;
                }

                if (!byTree.TryGetValue(diagnostic.Location.SourceTree, out var list))
                {
                    byTree[diagnostic.Location.SourceTree] = list = new List<Diagnostic>();
                }

                list.Add(diagnostic);
            }

            return declErrorsByTreeCache = byTree;
        }

        static bool CoversBaselineError(TypePartInfo part, Diagnostic error)
            => error.Location.SourceTree is not null
                && string.Equals(part.Declaration.SyntaxTree.FilePath,
                    error.Location.SourceTree.FilePath, StringComparison.OrdinalIgnoreCase)
                && part.Declaration.FullSpan.Contains(error.Location.SourceSpan.Start);

        bool IsCoveredByBaselineError(TypeGroupPlan unit)
            => unit.Parts.Any(part =>
                DeclErrorsByTree().TryGetValue(part.Declaration.SyntaxTree, out var errors)
                && errors.Any(error => CoversBaselineError(part, error)));

        // Bind the declaration stage once (Roslyn memoizes it); the
        // per-group predicate then only reads the in-memory lists, and the skip flags
        // are applied serially in unit order so the result is deterministic.
        _ = DeclErrorsByTree();
        var coveredByBaseline = new bool[units.Count];
        System.Threading.Tasks.Parallel.For(0, units.Count, i =>
        {
            coveredByBaseline[i] = IsCoveredByBaselineError(units[i]);
        });
        for (var i = 0; i < units.Count; i++)
        {
            if (coveredByBaseline[i])
            {
                units[i].SkipBaseline = true;
            }
        }

        // Shape gate (CS2J2016): a record primary-constructor parameter list may
        // appear on at most one part; multiple parts with a parameter list is a
        // baseline CS error and an explicitly unsupported merge shape (§4.5).
        foreach (var group in units)
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

        // Using-conflict gate (part-file directives that must be carried into the
        // primary): a needed namespace using can make the primary file's existing
        // simple names ambiguous (CS0104) even though each file compiled alone.
        // The merge is then unsafe as-is → leave the group multi-part.
        foreach (var group in units.Where(g =>
                     g.Parts.Count > 1
                     && !g.SkipBaseline && !g.SkipUnsupportedShape && !g.SkipEnclosingInactive))
        {
            var conflict = FindUsingConflict(compilation, group);
            if (conflict == null)
            {
                continue;
            }

            group.SkipUnsupportedShape = true;
            diagnostics.Add(new PartialMergeDiagnostic(
                PartialMergeSeverity.Warning,
                PartialMergeCodes.GroupRolledBack,
                "group left multi-part: carrying the part using directive '" + conflict +
                "' would make an existing name in the primary file ambiguous",
                TypeFullName: group.TypeFullName));
        }

        // Nested groups whose containing group is already inactive cannot be
        // merged (their merged node only rides a merged container): flag them so
        // they are neither built nor counted active. Re-run after M3 single-part
        // units join below.
        foreach (var blocked in PartialMergePreprocessor.EnclosedByInactiveGroup(units))
        {
            blocked.SkipEnclosingInactive = true;
        }

        // M3 pairing decisions for every active multi-part group. Done before any
        // node is built and (under the opt-in flag) before call-site finalization,
        // which must see the complete candidate set exactly once.
        var activeMultiGroups = multiGroups
            .Where(g => !g.SkipBaseline && !g.SkipUnsupportedShape && !g.SkipEnclosingInactive)
            .ToList();
        foreach (var group in activeMultiGroups)
        {
            collapse.PrepareGroup(group);
        }

        if (options.AssumeNoGeneratedPartial)
        {
            // Probe single-part types for removable classics, then classify every
            // candidate's call sites once; only survivors join the plan as
            // member-only rewrites.
            foreach (var single in singleTypes)
            {
                if (IsCoveredByBaselineError(single))
                {
                    single.SkipBaseline = true;
                    continue;
                }

                collapse.PrepareGroup(single);
            }

            collapse.FinalizeClassicRemovals();

            foreach (var single in singleTypes)
            {
                if (single.SkipBaseline)
                {
                    continue;
                }

                var singlePlan = collapse.ForGroup(single);
                if (singlePlan is null
                    || (singlePlan.DroppedMembers.Count == 0
                        && singlePlan.ImplementationToDefinition.Count == 0))
                {
                    continue;
                }

                single.IsMemberOnlyRewrite = true;
                units.Add(single);
            }
        }

        stats.SkippedBaseline = multiGroups.Count(g => g.SkipBaseline)
            + (options.AssumeNoGeneratedPartial
                ? singleTypes.Count(g => g.SkipBaseline)
                : 0);

        // Second pass now that M3 member-only single-part units have joined: a
        // single-part container that is baseline-skipped blocks its nested
        // multi-part groups too.
        foreach (var blocked in PartialMergePreprocessor.EnclosedByInactiveGroup(units))
        {
            blocked.SkipEnclosingInactive = true;
        }

        // Static lookup is per-analysis; reset so successive runs in one process
        // never see a previous tree's aliases.
        PartialMemberMerge.NestedOverrides.Clear();

        // Build deepest-first; after a unit is built, register each part's text as
        // an alias for its merged node so containing types (built afterwards)
        // replace the nested part members. Switch to annotation keys if real
        // fixtures produce text-key collisions.
        foreach (var group in units
                     .Where(g => !g.SkipBaseline && !g.SkipUnsupportedShape && !g.SkipEnclosingInactive)
                     .OrderByDescending(g => g.TypeFullName.Count(c => c == '.')))
        {
            TypeDeclarationSyntax merged;
            if (group.IsMemberOnlyRewrite)
            {
                // Member-only rewrite (M3 opt-in): keep the declaration byte-stable
                // apart from dropped members; no banner/modifier/base rewrites.
                var declaration = group.Primary.Declaration;
                merged = declaration.WithMembers(
                    SyntaxFactory.List(PartialMemberMerge.CollectMembers(group, collapse)));
            }
            else
            {
                merged = PartialTypeMergeBuilder.Build(compilation, group, collapse);
            }

            merged = collapse.StripCallSites(merged);
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
        var allPartNodes = units
            .SelectMany(group => group.Parts.Select(part => part.Declaration))
            .ToHashSet();

        var edits = new List<FileEdit>();
        foreach (var group in units)
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
        // zero-length insertion PER SCOPE: compilation-unit directives land at
        // compilation-unit scope, while directives declared inside a namespace
        // block (or after a file-scoped namespace) land inside the matching
        // namespace declaration of the primary. Keeping the original scope
        // matters for textual placement (a file-scoped namespace has no block
        // after it where a global insertion would belong) and avoids widening a
        // directive's binding scope beyond what the part file granted.
        // Explicit global/static/alias forms are preserved verbatim.
        // Member-only rewrites are single-part and never add directives their
        // primary file lacks.
        foreach (var primaryFile in units
                     .Where(g => g.MergedDeclaration is not null && !g.IsMemberOnlyRewrite)
                     .GroupBy(g => g.Primary.SourceFilePath, StringComparer.OrdinalIgnoreCase))
        {
            var filePath = primaryFile.Key;
            var primaryUnit = primaryFile.First().Primary.Declaration
                .FirstAncestorOrSelf<CompilationUnitSyntax>()!;

            // Compilation-unit scope: directives already present plus every
            // directive scheduled into the global bucket while scanning parts.
            var globalSeen = new HashSet<string>(
                primaryUnit.Usings.Select(UsingKey), StringComparer.Ordinal);
            var globalMissing = new List<UsingDirectiveSyntax>();

            // Namespace scope: one bucket per namespace declaration enclosing a
            // primary declaration (a primary file may hold several namespaces).
            var nsBuckets = new Dictionary<BaseNamespaceDeclarationSyntax, NamespaceUsingBucket>();
            foreach (var group in primaryFile)
            {
                var primaryNs = group.Primary.Declaration
                    .FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
                if (primaryNs is null || nsBuckets.ContainsKey(primaryNs))
                {
                    continue;
                }

                // Visible at the primary declaration: compilation-unit usings
                // plus the usings of every enclosing namespace declaration.
                var visible = new HashSet<string>(
                    primaryUnit.Usings.Select(UsingKey), StringComparer.Ordinal);
                for (var ns = primaryNs; ns is not null; ns = EnclosingNamespace(ns))
                {
                    foreach (var directive in ns.Usings)
                    {
                        visible.Add(UsingKey(directive));
                    }
                }

                nsBuckets[primaryNs] = new NamespaceUsingBucket
                {
                    Seen = visible,
                    Missing = new List<UsingDirectiveSyntax>(),
                    SpanOwner = group.Primary.Declaration.FullSpan,
                };
            }

            // Pass 1: compilation-unit-level directives from every part.
            foreach (var group in primaryFile)
            {
                foreach (var part in group.Parts)
                {
                    var unit = part.Declaration.FirstAncestorOrSelf<CompilationUnitSyntax>()!;
                    foreach (var directive in unit.Usings)
                    {
                        if (!globalSeen.Add(UsingKey(directive))) continue;
                        // 只带部件成员体真正用到的 using（见 PartUsesUsing）。
                        if (PartUsesUsing(compilation, part.Declaration, directive))
                        {
                            globalMissing.Add(directive);
                        }
                    }
                }
            }

            // Pass 2: namespace-scoped directives (block namespaces and
            // usings following a file-scoped namespace). Directives from outer
            // namespace declarations of the part land in the primary's
            // innermost namespace — visibility is preserved for the merged
            // type regardless of dotted-vs-nested namespace shape.
            foreach (var group in primaryFile)
            {
                var primaryNs = group.Primary.Declaration
                    .FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
                if (primaryNs is null || !nsBuckets.TryGetValue(primaryNs, out var bucket))
                {
                    continue;
                }

                foreach (var part in group.Parts)
                {
                    for (var ns = part.Declaration.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
                         ns is not null;
                         ns = EnclosingNamespace(ns))
                    {
                        foreach (var directive in ns.Usings)
                        {
                            var key = UsingKey(directive);
                            if (globalSeen.Contains(key) || !bucket.Seen.Add(key))
                            {
                                continue;
                            }

                            if (PartUsesUsing(compilation, part.Declaration, directive))
                            {
                                bucket.Missing.Add(directive);
                            }
                        }
                    }
                }
            }

            if (globalMissing.Count > 0)
            {
                var lines = string.Concat(globalMissing.Select(directive =>
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

            foreach (var (nsSyntax, bucket) in nsBuckets)
            {
                if (bucket.Missing.Count == 0)
                {
                    continue;
                }

                var lines = string.Concat(bucket.Missing.Select(directive =>
                    directive.WithoutTrivia().ToFullString().TrimEnd() + "\r\n"));

                var anchor = nsSyntax.Usings.LastOrDefault();
                int insertionPosition;
                if (anchor is not null)
                {
                    insertionPosition = anchor.Span.End;
                }
                else
                {
                    insertionPosition = nsSyntax switch
                    {
                        NamespaceDeclarationSyntax block => block.OpenBraceToken.Span.End,
                        FileScopedNamespaceDeclarationSyntax fileScoped =>
                            fileScoped.SemicolonToken.Span.End,
                        _ => nsSyntax.Span.End,
                    };
                }

                edits.Add(new InsertUsingsEdit
                {
                    FilePath = filePath,
                    Span = bucket.SpanOwner,
                    InsertionPosition = insertionPosition,
                    // Own leading newline in both shapes: the token at the
                    // insertion point ('{' or ';' / preceding directive) does
                    // not own the newline of the trivia that follows it.
                    UsingsText = "\r\n" + lines,
                });
            }
        }

        // CS2J2017: heuristic analyzer/source-generator reference detection
        // (Info; only surfaced in verbose output).
        foreach (var projectFile in EnumerateProjectFiles(sourceRoot))
        {
            var text = File.ReadAllText(projectFile);
            if (text.Contains("<Analyzer Include", StringComparison.OrdinalIgnoreCase)
                || text.Contains("OutputItemType=\"Analyzer\"", StringComparison.OrdinalIgnoreCase)
                || text.Contains("SourceGenerator", StringComparison.Ordinal))
            {
                diagnostics.Add(new PartialMergeDiagnostic(
                    PartialMergeSeverity.Info,
                    PartialMergeCodes.AnalyzerSeen,
                    "analyzer/source-generator reference detected; merged declaration keeps partial " +
                    "so generated parts remain valid (verify build is the semantic oracle)",
                    FilePath: projectFile));
            }
        }

        stats.GroupsMerged = units.Count(g =>
            g.MergedDeclaration is not null && !g.IsMemberOnlyRewrite);
        stats.PartsRemoved = units
            .Where(g => g.MergedDeclaration is not null && !g.IsMemberOnlyRewrite)
            .Sum(g => g.Parts.Count - 1);
        stats.GroupsRolledBack = units.Count(g => g.RolledBack);

        return new PartialMergePlan
        {
            Groups = units,
            EditsByFile = edits.ToLookup(edit => edit.FilePath, StringComparer.OrdinalIgnoreCase),
            Diagnostics = diagnostics,
            Statistics = stats.Build(),
        };
    }

    private static BaseNamespaceDeclarationSyntax? EnclosingNamespace(
        BaseNamespaceDeclarationSyntax ns)
        => ns.Parent?.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();

    /// <summary>
    /// 只把部件成员体真正用到的 using 带进主文件。无条件带入会把部件文件里
    /// 未使用的指令提升到合并文件（真实 SDK：JsonSerializerOptions.cs 的
    /// <c>using System.ComponentModel;</c>），使主文件原有同名引用二义
    /// （BooleanConverter 在 Serialization.Converters 与 System.ComponentModel
    /// 之间 → CS0104，部分合并树 build 失败）。
    /// 保守方向：别名/static/global/无法解析的指令、以及任何可能被间接使用
    /// （扩展方法）的情形一律保留（true）。
    /// </summary>
    private static bool PartUsesUsing(
        CSharpCompilation compilation,
        TypeDeclarationSyntax part,
        UsingDirectiveSyntax directive)
    {
        if (directive.Alias != null
            || directive.StaticKeyword != default
            || directive.GlobalKeyword != default
            || directive.Name is null)
        {
            return true;
        }

        var ns = ResolveNamespace(compilation.GlobalNamespace, directive.Name);
        if (ns == null)
        {
            return true; // 无法解析命名空间 → 不敢裁剪。
        }

        var provided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in ns.GetTypeMembers())
        {
            provided.Add(type.Name);
            // 特性短名：[Required] 绑定 RequiredAttribute，标识符里只出现短名。
            if (type.Name.EndsWith("Attribute", StringComparison.Ordinal)
                && type.Name.Length > "Attribute".Length)
            {
                provided.Add(type.Name[..^"Attribute".Length]);
            }

            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol { IsExtensionMethod: true })
                {
                    provided.Add(member.Name);
                }
            }
        }

        foreach (var node in part.DescendantNodes())
        {
            var name = node switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                _ => null,
            };
            if (name != null && provided.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 找出「必须携带进主文件的部件 using」中会与主文件现有简单名冲突的指令。
    /// 返回冲突指令的命名空间名（无冲突 null）。每个文件单独编译时都合法，
    /// 但合并后同一编译单位里两个 using 同时提供同名类型 → CS0104。
    /// </summary>
    private static string? FindUsingConflict(
        CSharpCompilation compilation, TypeGroupPlan group)
    {
        var primary = group.Primary.Declaration;
        var primaryUnit = primary.FirstAncestorOrSelf<CompilationUnitSyntax>()!;
        var visible = new HashSet<string>(
            primaryUnit.Usings.Select(UsingKey), StringComparer.Ordinal);
        for (var ns = primary.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
             ns != null;
             ns = EnclosingNamespace(ns))
        {
            foreach (var directive in ns.Usings)
            {
                visible.Add(UsingKey(directive));
            }
        }

        var primaryNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in primary.DescendantNodes())
        {
            var name = node switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                GenericNameSyntax generic => generic.Identifier.ValueText,
                _ => null,
            };
            if (name != null)
            {
                primaryNames.Add(name);
            }
        }

        foreach (var part in group.Parts.Skip(1))
        {
            foreach (var directive in EnumerateUsings(part.Declaration))
            {
                if (directive.Alias != null
                    || directive.StaticKeyword != default
                    || directive.GlobalKeyword != default
                    || directive.Name is null)
                {
                    continue;
                }

                if (visible.Contains(UsingKey(directive))) continue;
                if (!PartUsesUsing(compilation, part.Declaration, directive)) continue;

                var ns = ResolveNamespace(compilation.GlobalNamespace, directive.Name);
                if (ns == null) continue;
                foreach (var type in ns.GetTypeMembers())
                {
                    if (primaryNames.Contains(type.Name)
                        || (type.Name.EndsWith("Attribute", StringComparison.Ordinal)
                            && type.Name.Length > "Attribute".Length
                            && primaryNames.Contains(type.Name[..^"Attribute".Length])))
                    {
                        return directive.Name.ToString();
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<UsingDirectiveSyntax> EnumerateUsings(TypeDeclarationSyntax part)
    {
        var unit = part.FirstAncestorOrSelf<CompilationUnitSyntax>()!;
        foreach (var directive in unit.Usings)
        {
            yield return directive;
        }

        for (var ns = part.FirstAncestorOrSelf<BaseNamespaceDeclarationSyntax>();
             ns != null;
             ns = EnclosingNamespace(ns))
        {
            foreach (var directive in ns.Usings)
            {
                yield return directive;
            }
        }
    }

    private static INamespaceSymbol? ResolveNamespace(INamespaceSymbol global, NameSyntax name)
    {
        var current = global;
        foreach (var part in name.ToString().Split('.'))
        {
            var next = current.GetNamespaceMembers()
                .FirstOrDefault(member => string.Equals(member.Name, part, StringComparison.Ordinal));
            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current;
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

    /// <summary>
    /// Accumulated namespace-scoped using migration for one primary-file
    /// namespace declaration: Seen = directive keys already visible at the
    /// primary declaration position, Missing = part directives to insert.
    /// SpanOwner attributes the zero-length edit to an active primary part for
    /// the adapter's rollback edit filter.
    /// </summary>
    private sealed class NamespaceUsingBucket
    {
        public required HashSet<string> Seen { get; init; }
        public required List<UsingDirectiveSyntax> Missing { get; init; }
        public required TextSpan SpanOwner { get; init; }
    }
}
