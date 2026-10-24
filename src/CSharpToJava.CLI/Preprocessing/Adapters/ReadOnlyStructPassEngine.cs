using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Multi-phase post-processing engine for the make-readonly pass. Every phase here
/// was moved verbatim from the former ProjectReadOnlyStructPreprocessor facade so
/// the rewrite order and output stay byte-identical:
/// call-site update → field-read update → ref-field hoist → interface transform →
/// concrete implementer fix → class bridges → ctor normalization → disabled regions,
/// plus the csproj/InternalsVisibleTo/test-shim tree fix-ups that precede them.
/// </summary>
internal static class ReadOnlyStructPassEngine
{
    /// <summary>
    /// The basic metadata-reference set is identical for every cross-file pass in a
    /// run (it only reflects the fixed trusted-framework assembly list), so it is
    /// built once and shared by the four compilations the engine creates. The
    /// references are immutable; the compilation only enumerates them.
    /// </summary>
    private static List<MetadataReference>? _sharedBasicReferences;
    private static List<MetadataReference> BasicReferences()
        => _sharedBasicReferences ??= ProjectUnsignedTypePreprocessor.GetBasicReferences();

    /// <summary>
    /// Adhoc workspace shared by all cross-file passes for <see cref="Formatter.Format"/>:
    /// cross-file rewriters create new syntax nodes (via SyntaxFactory) that carry no
    /// trivia (no indentation, no newlines); writing such a tree directly via
    /// <c>ToFullString()</c> produces unformatted source (e.g.
    /// <c>var__cs2jOld_Field=new List&lt;T&gt;(cap);receiver=receiver.With_Field(__cs2jOld_Field);</c>).
    /// The shared workspace is stateless and thread-safe across passes.
    /// </summary>
    private static readonly AdhocWorkspace FormattingWorkspace = new();

    /// <summary>
    /// Writes <paramref name="root"/> to <paramref name="file"/> after a round of
    /// <see cref="Formatter.Format"/>: cross-file rewrites generate trivia-less
    /// syntax nodes that the formatter normalises back to properly indented,
    /// line-separated source.
    /// </summary>
    private static void WriteFormatted(string file, SyntaxNode root)
    {
        var formatted = Formatter.Format(root, FormattingWorkspace);
        File.WriteAllText(file, formatted.ToFullString(), new System.Text.UTF8Encoding(false));
    }

    /// <summary>
    /// Per-run cache of parsed output trees shared by every cross-file pass. Each
    /// pass re-reads the intermediate tree and re-parses every output file; most
    /// files are never written by any pass, so their tree is reused verbatim. The
    /// cache validates by comparing the freshly read source text against the text
    /// the tree was parsed from, so a file rewritten by an earlier pass is always
    /// re-parsed (identical input text still yields the identical tree).
    /// </summary>
    internal sealed class OutputTreeCache
    {
        private readonly Dictionary<string, (string Code, SyntaxTree Tree)> _map =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        /// <summary>
        /// Pre-populates an entry with a tree the caller already has (e.g. the parse
        /// the per-file maker produced). The entry is validated by the same text
        /// comparison as <see cref="Get"/>, so a stale seed is simply re-parsed.
        /// </summary>
        public void Seed(string file, string code, SyntaxTree tree)
        {
            lock (_gate)
            {
                _map[file] = (code, tree);
            }
        }

        public SyntaxTree Get(string file, string code)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(file, out var entry) &&
                    string.Equals(entry.Code, code, StringComparison.Ordinal))
                {
                    return entry.Tree;
                }
            }

            // Parse outside the lock so the (cold) first pass can parse in parallel;
            // each file is only parsed by the single thread that owns its slot.
            var tree = CSharpSyntaxTree.ParseText(code, path: file);
            lock (_gate)
            {
                _map[file] = (code, tree);
            }
            return tree;
        }
    }

    /// <summary>
    /// Reads and parses every output file in parallel, returning an ordered
    /// path→tree map (identical order to the serial loop) plus the freshly read
    /// source text of each file so callers can run their own textual probes without
    /// re-reading. Parsing is CPU-bound, so the cold first pass binds across cores.
    /// </summary>
    private static Dictionary<string, SyntaxTree> ReadOutputTreesParallel(
        IReadOnlyList<string> files, OutputTreeCache? treeCache, out string[] codes)
    {
        var trees = new SyntaxTree[files.Count];
        var localCodes = new string[files.Count];
        System.Threading.Tasks.Parallel.For(0, files.Count, i =>
        {
            var code = File.ReadAllText(files[i]);
            localCodes[i] = code;
            trees[i] = treeCache == null
                ? CSharpSyntaxTree.ParseText(code, path: files[i])
                : treeCache.Get(files[i], code);
        });
        codes = localCodes;

        var result = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
            result[files[i]] = trees[i];
        return result;
    }

    public static void MergeStatistics(ReadOnlyStructMakerStatistics target, ReadOnlyStructMakerStatistics source)
    {
        target.StructsScanned += source.StructsScanned;
        target.StructsConverted += source.StructsConverted;
        target.StructsSkipped += source.StructsSkipped;
        target.StructsFailed += source.StructsFailed;
        target.Level0_Skipped += source.Level0_Skipped;
        target.Level1_DirectAdd += source.Level1_DirectAdd;
        target.Level2_PropertyConvert += source.Level2_PropertyConvert;
        target.Level3_DataContainer += source.Level3_DataContainer;
        target.Level5_MethodMigrate += source.Level5_MethodMigrate;
        target.MethodsMigrated += source.MethodsMigrated;
        target.CallSitesUpdated += source.CallSitesUpdated;
    }

    /// <summary>
    /// Second pass: updates call sites of migrated void→struct methods across all files.
    /// </summary>
    public static Task UpdateCrossFileCallSites(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles,
        Dictionary<string, string> migratedStructs, OutputTreeCache? treeCache = null)
    {
        // Parse all output .cs files from the intermediate directory
        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        // Files whose text contains no `struct` keyword declare no structs, so the
        // struct-declaration discovery below can skip them entirely.
        var structTextFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputTrees = ReadOutputTreesParallel(outputCsFiles, treeCache, out var callsiteCodes);
        for (var i = 0; i < outputCsFiles.Count; i++)
        {
            if (callsiteCodes[i].IndexOf("struct", StringComparison.Ordinal) >= 0)
                structTextFiles.Add(outputCsFiles[i]);
        }

        // Find migrated method names by scanning struct declarations
        var migratedMethodToStruct = new Dictionary<string, HashSet<string>>();
        var outMigratedMethodToStruct = new Dictionary<string, HashSet<string>>();
        var migratedArities = new Dictionary<string, HashSet<int>>();
        var allMethodArities = new Dictionary<string, HashSet<int>>();
        // Every instance method name declared by any struct. The struct-returning call
        // rewrite below only ever matches a method that belongs to a struct, so a call
        // site whose method name is absent from this (over-approximated) set can never
        // be rewritten — its semantic resolution is skipped entirely.
        var structMethodNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, tree) in outputTrees)
        {
            if (!structTextFiles.Contains(file)) continue;
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                var structName = structDecl.Identifier.Text;
                foreach (var member in structDecl.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
                {
                    if (member.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)))
                        continue;
                    structMethodNames.Add(member.Identifier.Text);
                }
                if (!migratedStructs.ContainsKey(structName))
                {
                    // Diagnostic StructName values carry type parameters for generic
                    // structs ("ArrayBuilder<T>") while the syntax identifier is bare;
                    // accept those keys too.
                    var matchesGeneric = migratedStructs.Keys.Any(k =>
                        k.Length > structName.Length &&
                        k.StartsWith(structName, StringComparison.Ordinal) &&
                        k[structName.Length] == '<');
                    if (!matchesGeneric) continue;
                }

                // Find methods that return the struct type (migrated from void)
                foreach (var method in structDecl.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
                {
                    if (method.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)))
                        continue;
                    // Explicit interface implementations are not callable by name at
                    // ordinary call sites — excluding them keeps the arity sets exact.
                    if (method.ExplicitInterfaceSpecifier != null)
                        continue;

                    var name = method.Identifier.Text;
                    var arity = method.ParameterList.Parameters.Count;
                    var arityKey = structName + "." + name;

                    // Record every non-generated overload arity (WithXxx methods are
                    // generated helpers, not candidates at original call sites).
                    if (!name.StartsWith("With", StringComparison.Ordinal))
                    {
                        if (!allMethodArities.TryGetValue(arityKey, out var aset))
                            allMethodArities[arityKey] = aset = new HashSet<int>();
                        aset.Add(arity);
                    }

                    // Generic structs (e.g. ArrayBuilder<T>) return `ArrayBuilder<T>`
                    // (GenericNameSyntax), not a bare IdentifierNameSyntax.
                    var returnName = method.ReturnType switch
                    {
                        Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id => id.Identifier.Text,
                        Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax gn => gn.Identifier.Text,
                        _ => null
                    };
                    if (returnName == structName)
                    {
                        if (!migratedMethodToStruct.TryGetValue(name, out var ms))
                            migratedMethodToStruct[name] = ms = new HashSet<string>(StringComparer.Ordinal);
                        ms.Add(structName);
                        if (!migratedArities.TryGetValue(arityKey, out var mset))
                            migratedArities[arityKey] = mset = new HashSet<int>();
                        mset.Add(arity);
                    }

                    // Find methods migrated with an out parameter (OtherReturnToOut):
                    // they carry `out <StructName> newStatus`.
                    foreach (var parameter in method.ParameterList.Parameters)
                    {
                        var outParamTypeName = parameter.Type switch
                        {
                            Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id => id.Identifier.Text,
                            Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax gn => gn.Identifier.Text,
                            _ => null
                        };
                        if (parameter.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword)) &&
                            outParamTypeName == structName)
                        {
                            if (!outMigratedMethodToStruct.TryGetValue(name, out var os))
                                outMigratedMethodToStruct[name] = os = new HashSet<string>(StringComparer.Ordinal);
                            os.Add(structName);
                        }
                    }
                }
            }
        }

        if (migratedMethodToStruct.Count == 0 && outMigratedMethodToStruct.Count == 0) return Task.CompletedTask;

        // Create compilation for semantic analysis
        var references = BasicReferences();
        var outputCompilation = CSharpCompilation.Create(
            "callsite-update",
            outputTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Apply CallSiteUpdater to each file. Files are disjoint (each write targets
        // its own output path) and the semantic model is queried per tree, so the
        // per-file rewrite is pure and runs in parallel; the emitted bytes are
        // identical to the serial loop because no shared state is touched.
        System.Threading.Tasks.Parallel.ForEach(outputTrees, kvp =>
        {
            var file = kvp.Key;
            var tree = kvp.Value;
            var root = tree.GetRoot();
            var model = outputCompilation.GetSemanticModel(tree);
            var updater = new CallSiteUpdater(migratedMethodToStruct, model,
                outMigratedMethodToStruct: outMigratedMethodToStruct,
                migratedArities: migratedArities,
                allMethodArities: allMethodArities,
                structMethodNames: structMethodNames);
            var newRoot = updater.Visit(root);
            if (newRoot != null && !ReferenceEquals(newRoot, root) && newRoot.ToFullString() != root.ToFullString())
            {
                WriteFormatted(file, newRoot);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Final pass: rewrites constructors of readonly structs so every instance field is
    /// assigned exactly once (shadow locals), keeping the intermediate C# compilable and
    /// the generated Java final-field assignments legal.
    /// </summary>
    public static async Task NormalizeReadOnlyStructConstructors(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles, OutputTreeCache? treeCache = null)
    {
        var outputFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputFiles.Add(outputFile);
        }

        // Each file is read, normalized and written independently and writes only its
        // own path, so the loop runs in parallel with byte-identical output (the tree
        // cache is thread-safe and per-file).
        System.Threading.Tasks.Parallel.ForEach(outputFiles, outputFile =>
        {
            var code = File.ReadAllText(outputFile);

            // Fast pre-check: constructor normalization only touches readonly structs,
            // so a file whose text contains no `struct` keyword cannot be affected —
            // skip the parse entirely for the (vast majority) struct-free files.
            if (code.IndexOf("struct", StringComparison.Ordinal) < 0) return;

            var tree = treeCache == null
                ? CSharpSyntaxTree.ParseText(code, path: outputFile)
                : treeCache.Get(outputFile, code);
            var root = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)tree.GetRoot();

            // Fast pre-check: skip files without readonly structs.
            if (!root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>()
                .Any(s => s.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReadOnlyKeyword))))
                return;

            var newRoot = ConstructorNormalizer.Normalize(root);
            if (newRoot.ToFullString() != root.ToFullString())
            {
                WriteFormatted(outputFile, newRoot);
            }
        });
        await Task.CompletedTask;
    }

    /// <summary>
    /// Cross-file pass: updates read accesses (obj.Field → obj.getField()) for L4-converted
    /// struct fields in ALL files, not just the file containing the struct definition.
    /// </summary>
    public static Task UpdateCrossFileFieldReads(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles, OutputTreeCache? treeCache = null)
    {
        // Parse all output .cs files from the intermediate directory
        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        // Files whose text contains no `struct` keyword declare no structs, so the
        // struct-declaration discovery loops below can skip them entirely.
        var structTextFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputTrees = ReadOutputTreesParallel(outputCsFiles, treeCache, out var fieldreadCodes);
        for (var i = 0; i < outputCsFiles.Count; i++)
        {
            if (fieldreadCodes[i].IndexOf("struct", StringComparison.Ordinal) >= 0)
                structTextFiles.Add(outputCsFiles[i]);
        }

        // Find L4-converted structs: those with private fields + getXxx() getter methods
        var publicFieldStructs = new Dictionary<string, HashSet<string>>();
        // struct NAME -> qualified names (namespace + containing-type chain). Same-named
        // structs inside different types (e.g. XmlTextWriter.Namespace vs the non-converted
        // XmlWellFormedWriter.Namespace) are distinguished so receivers resolving to the
        // wrong struct are never rewritten (CS1061 WithXxx on a non-converted struct).
        var qualifiedNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void RegisterQualified(string structName, Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax structDecl)
        {
            if (!qualifiedNames.TryGetValue(structName, out var set))
                qualifiedNames[structName] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(AssignmentRewriter.GetQualifiedStructName(structDecl));
        }

        // Detect L3-converted structs (DataContainer) BEFORE the WithXxx scan: a
        // hand-written functional method (e.g. `WithRetries`) on a struct that is
        // otherwise a full data container must not claim only that one property's
        // writes — the constructor reconstruction covers ALL properties uniformly.
        //
        // The L4 field/getter discovery and the L3 eligibility scan are independent
        // per-struct predicates, so they share a single walk over the struct-bearing
        // output trees (identical results to the former two passes).
        var l3Eligible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, tree) in outputTrees)
        {
            if (!structTextFiles.Contains(file)) continue;
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                if (TryGetL3DataContainerInfo(structDecl, out _, out _))
                    l3Eligible.Add(structDecl.Identifier.Text);

                var structName = structDecl.Identifier.Text;
                var privateFields = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>()
                    .Where(f => f.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword)))
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                    .ToHashSet();

                if (privateFields.Count == 0) continue;

                // Verify getter methods exist for these fields
                var getterNames = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.Text.StartsWith("get") && m.ParameterList.Parameters.Count == 0)
                    .Select(m => m.Identifier.Text[3..]) // strip "get" prefix
                    .ToHashSet();

                var convertedFields = privateFields.Where(f => getterNames.Contains(f)).ToHashSet();
                if (convertedFields.Count > 0)
                {
                    publicFieldStructs[structName] = convertedFields;
                    RegisterQualified(structName, structDecl);
                }
            }
        }

        // Also detect L5-migrated structs: those with WithXxx() methods for property setters.
        // L5 migration converts public property setters to WithXxx() methods and removes setters.
        // Object initializers and external assignments using those setters must be updated.
        // L5 structs have public get-only properties (not private fields + getXxx() getters),
        // so the AssignmentRewriter must use property access for reads, not getXxx().
        var l5StructNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var withChainStructs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, tree) in outputTrees)
        {
            if (!structTextFiles.Contains(file)) continue;
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                var structName = structDecl.Identifier.Text;

                // L3 data containers are handled by constructor reconstruction
                // regardless of any (possibly hand-written) WithXxx helper.
                if (l3Eligible.Contains(structName))
                    continue;

                var withMethodProps = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                    .Where(m => m.Identifier.Text.StartsWith("With", StringComparison.Ordinal)
                                && m.ParameterList.Parameters.Count == 1
                                && ((m.ReturnType is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax rt
                                        && rt.Identifier.Text == structName)
                                    || (m.ReturnType is Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax grt
                                        && grt.Identifier.Text == structName)))
                    .Select(m => m.Identifier.Text.Substring(4)) // strip "With" prefix
                    .ToHashSet();

                if (withMethodProps.Count > 0)
                {
                    withChainStructs.Add(structName);
                    RegisterQualified(structName, structDecl);
                    // Only treat as L4 (getXxx read rewriting) when the struct truly has
                    // getXxx() getter methods for its PRIVATE fields. Methods merely named
                    // GetXxx (e.g. Rectangle.GetIntersection) must not trigger this.
                    var privateFieldNames = structDecl.Members
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>()
                        .Where(f => f.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword)))
                        .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                        .ToHashSet(StringComparer.Ordinal);
                    var hasGetterMethods = structDecl.Members
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                        .Any(m => m.ParameterList.Parameters.Count == 0
                                  && m.Identifier.Text.StartsWith("get", StringComparison.Ordinal)
                                  && privateFieldNames.Contains(m.Identifier.Text.Substring(3)));
                    if (!hasGetterMethods)
                    {
                        l5StructNames.Add(structName);
                    }

                    if (publicFieldStructs.TryGetValue(structName, out var existing))
                        existing.UnionWith(withMethodProps);
                    else
                        publicFieldStructs[structName] = withMethodProps;
                }
            }
        }

        // Register L3-converted structs (DataContainer): structs with get-only properties
        // and a constructor whose parameters match all property names. These structs need
        // object initializers (new S { Prop = v }) converted to constructor calls
        // (new S(prop: v)), and external writes (x.Prop = v) reconstructed through the
        // constructor. Reads stay as property access (like L5) since there are no
        // getXxx() methods. For each L3 struct we record its non-underscore get-only
        // properties (the public surface) and the generated constructor's parameter
        // names, so writes become `x = new S(p1: x.P1, ..., pj: v)`.
        var l3Structs = new Dictionary<string, (HashSet<string> Props, List<string> CtorParams)>(StringComparer.Ordinal);
        foreach (var (file, tree) in outputTrees)
        {
            if (!structTextFiles.Contains(file)) continue;
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                var structName = structDecl.Identifier.Text;
                if (publicFieldStructs.ContainsKey(structName))
                    continue;

                if (!TryGetL3DataContainerInfo(structDecl, out var publicProps, out var ctorParams))
                    continue;

                publicFieldStructs[structName] = publicProps;
                l5StructNames.Add(structName);
                l3Structs[structName] = (publicProps, ctorParams);
                RegisterQualified(structName, structDecl);
            }
        }
        if (publicFieldStructs.Count == 0) return Task.CompletedTask;

        // Create compilation for semantic analysis
        var references = BasicReferences();
        var outputCompilation = CSharpCompilation.Create(
            "field-read-update",
            outputTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // Apply AssignmentRewriter to each file (handles both read and write accesses).
        // Each file has its own semantic model and its own output path, so the rewrite
        // is pure per file and runs in parallel with byte-identical output.
        System.Threading.Tasks.Parallel.ForEach(outputTrees, kvp =>
        {
            var file = kvp.Key;
            var tree = kvp.Value;
            var root = tree.GetRoot();
            var model = outputCompilation.GetSemanticModel(tree);
            var rewriter = new AssignmentRewriter(publicFieldStructs, model, l5StructNames, withChainStructs, l3Structs,
                qualifiedNames: qualifiedNames);
            rewriter.BuildVariableTypeMap(root);
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && !ReferenceEquals(newRoot, root) && newRoot.ToFullString() != root.ToFullString())
            {
                WriteFormatted(file, newRoot);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Detects an L3-converted DataContainer struct: one or more get-only
    /// properties plus a constructor whose parameters cover EVERY get-only
    /// property (case-insensitive, leading underscores stripped). Returns the
    /// public (non-underscore) property surface and the constructor parameter
    /// names in declaration order.
    /// </summary>
    private static bool TryGetL3DataContainerInfo(
        Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax structDecl,
        out HashSet<string> publicProps,
        out List<string> ctorParams)
    {
        publicProps = new HashSet<string>(StringComparer.Ordinal);
        ctorParams = new List<string>();

        // Get-only properties: { get; } auto-properties AND custom getter
        // bodies (get => _backing / get { return _backing; }). Both were made
        // get-only by the L3 conversion and their storage is assignable through
        // the generated constructor, so writes map to named constructor
        // arguments. Expression-bodied properties (IsContinuation => ...) have
        // no accessor list and are excluded — they are computed read-only.
        var getOnlyProps = structDecl.Members
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>()
            .Where(p => p.AccessorList?.Accessors.Count == 1
                        && p.AccessorList.Accessors[0].IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration))
            .Select(p => p.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);
        if (getOnlyProps.Count == 0)
            return false;

        var matchingCtor = structDecl.Members
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax>()
            .FirstOrDefault(c =>
            {
                var paramNames = c.ParameterList.Parameters
                    .Select(p => p.Identifier.Text.TrimStart('_'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return getOnlyProps.All(prop => paramNames.Contains(prop.TrimStart('_')));
            });
        if (matchingCtor == null)
            return false;

        publicProps = getOnlyProps
            .Where(p => !p.StartsWith("_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        ctorParams = matchingCtor.ParameterList.Parameters
            .Select(p => p.Identifier.Text)
            .ToList();
        return true;
    }

    /// <summary>
    /// Cross-file pass: replaces ref/out arguments that target fields of readonly structs
    /// (CS0192) with hoisted temps plus a WithXxx write-back after the call:
    /// <code>
    /// mult(ref t, ref p0.aRot);  →  var __cs2jRef0 = p0.aRot;
    ///                               mult(ref t, ref __cs2jRef0);
    ///                               p0 = p0.WithaRot(__cs2jRef0);
    /// </code>
    /// </summary>
    public static Task HoistRefReadonlyFieldArguments(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles, OutputTreeCache? treeCache = null)
    {
        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        // The hoister only rewrites ref/out arguments, so a file whose text contains
        // neither `ref ` nor `out ` can never be modified — record the candidates so
        // the semantic model and whole-tree visit are skipped for the rest.
        var refOutCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var structTextFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputTrees = ReadOutputTreesParallel(outputCsFiles, treeCache, out var hoistCodes);
        for (var i = 0; i < outputCsFiles.Count; i++)
        {
            var code = hoistCodes[i];
            if (code.IndexOf("struct", StringComparison.Ordinal) >= 0)
                structTextFiles.Add(outputCsFiles[i]);
            if (code.IndexOf("ref ", StringComparison.Ordinal) >= 0 ||
                code.IndexOf("out ", StringComparison.Ordinal) >= 0)
                refOutCandidates.Add(outputCsFiles[i]);
        }

        // Collect instance field names of readonly structs (all of them are readonly fields),
        // plus get-only properties (fields converted by L3/L4/L5 — same storage, and
        // ref/out uses of them must be hoisted into temps + WithXxx write-backs too).
        // A file whose text contains no `struct` keyword declares no struct, so its
        // whole-tree walk is skipped (it can contribute no readonly struct fields).
        var readonlyStructFields = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (file, tree) in outputTrees)
        {
            if (!structTextFiles.Contains(file)) continue;
            var root = tree.GetRoot();
            foreach (var structDecl in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                if (!structDecl.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReadOnlyKeyword)))
                    continue;
                var fields = structDecl.Members
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>()
                    .Where(f => !f.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StaticKeyword)
                                                     || m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ConstKeyword)))
                    .SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                    .Concat(structDecl.Members
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax>()
                        .Where(p => p.AccessorList?.Accessors.Count == 1
                                    && p.AccessorList.Accessors[0].IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.GetAccessorDeclaration))
                        .Select(p => p.Identifier.Text))
                    .ToHashSet(StringComparer.Ordinal);
                if (fields.Count == 0) continue;
                var name = structDecl.Identifier.Text;
                if (readonlyStructFields.TryGetValue(name, out var existing))
                    existing.UnionWith(fields);
                else
                    readonlyStructFields[name] = fields;
            }
        }

        if (readonlyStructFields.Count == 0) return Task.CompletedTask;

        var references = BasicReferences();
        var outputCompilation = CSharpCompilation.Create(
            "ref-hoist",
            outputTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        System.Threading.Tasks.Parallel.ForEach(outputTrees, kvp =>
        {
            var file = kvp.Key;
            var tree = kvp.Value;
            if (!refOutCandidates.Contains(file)) return;
            var root = tree.GetRoot();
            var model = outputCompilation.GetSemanticModel(tree);
            var rewriter = new RefReadonlyFieldHoister(readonlyStructFields, model);
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && !ReferenceEquals(newRoot, root) && newRoot.ToFullString() != root.ToFullString())
            {
                WriteFormatted(file, newRoot);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Rewrites statement-level invocations whose ref/out arguments target readonly struct
    /// fields into hoisted temp + call + WithXxx write-back. Constructors are skipped
    /// (ref field access is legal there).
    /// </summary>
    private sealed class RefReadonlyFieldHoister : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, HashSet<string>> _readonlyStructFields;
        private readonly SemanticModel _model;
        private int _counter;

        public RefReadonlyFieldHoister(Dictionary<string, HashSet<string>> readonlyStructFields, SemanticModel model)
        {
            _readonlyStructFields = readonlyStructFields;
            _model = model;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitBlock(Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax node)
        {
            // Pre-collect the name→type context from the ORIGINAL node (whose parent
            // chain is intact). Nodes produced by base.VisitBlock lose their link to
            // ancestors above the block while we process them.
            var context = BuildNameTypeContext(node);

            var original = node;
            node = (Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax)base.VisitBlock(node)!;
            var newStatements = new List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>();
            // Returning the ORIGINAL block node when nothing was hoisted keeps the whole
            // rewritten tree reference-equal to its input, letting the caller's
            // reference-equality change check skip the full-text comparison.
            var changed = !ReferenceEquals(node, original);
            foreach (var statement in node.Statements)
            {
                var (leading, middle, trailing) = RewriteStatement(statement, context);
                if (leading.Count > 0 || trailing.Count > 0 || !ReferenceEquals(middle, statement))
                    changed = true;
                newStatements.AddRange(leading);
                newStatements.Add(middle);
                newStatements.AddRange(trailing);
            }
            return changed
                ? node.WithStatements(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.List(newStatements))
                : node;
        }

        private sealed record HoistContext(
            Dictionary<string, string> NameToType,
            string? ThisStructName);

        private static HoistContext BuildNameTypeContext(Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax node)
        {
            var nameToType = new Dictionary<string, string>(StringComparer.Ordinal);

            var enclosingMethod = node.Ancestors()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseMethodDeclarationSyntax>().FirstOrDefault();
            if (enclosingMethod != null)
            {
                foreach (var param in enclosingMethod.ParameterList.Parameters)
                {
                    var tn = param.Type != null ? ExtractSimpleTypeName(param.Type) : null;
                    if (tn != null) nameToType[param.Identifier.Text] = tn;
                }
            }

            var enclosingType = node.Ancestors()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>().FirstOrDefault();
            if (enclosingType != null)
            {
                foreach (var field in enclosingType.Members.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>())
                {
                    var tn = ExtractSimpleTypeName(field.Declaration.Type);
                    if (tn == null) continue;
                    foreach (var variable in field.Declaration.Variables)
                        nameToType[variable.Identifier.Text] = tn;
                }
            }

            // Locals declared anywhere inside this block (explicit types only).
            foreach (var decl in node.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax>())
            {
                var tn = ExtractSimpleTypeName(decl.Type);
                if (tn == null || tn == "var") continue;
                foreach (var variable in decl.Variables)
                    nameToType[variable.Identifier.Text] = tn;
            }

            // `var result = this;` locals inside a type declaration (migrated methods'
            // accumulator) resolve to the containing type. Semantic resolution is
            // unavailable for nodes rebuilt by the rewriter, so this syntactic mapping
            // is required for the hoist fallback.
            var enclosingTypeForThis = node.Ancestors()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>().FirstOrDefault();
            if (enclosingTypeForThis != null)
            {
                foreach (var decl in node.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclarationSyntax>())
                {
                    if (decl.Type is not Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax { Identifier.Text: "var" })
                        continue;
                    foreach (var variable in decl.Variables)
                    {
                        if (variable.Initializer?.Value is Microsoft.CodeAnalysis.CSharp.Syntax.ThisExpressionSyntax &&
                            !nameToType.ContainsKey(variable.Identifier.Text))
                            nameToType[variable.Identifier.Text] = enclosingTypeForThis.Identifier.Text;
                    }
                }
            }

            var enclosingStruct = node.Ancestors()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>().FirstOrDefault();
            return new HoistContext(nameToType, enclosingStruct?.Identifier.Text);
        }

        private (List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>,
                 Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax,
                 List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>)
            RewriteStatement(Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax statement, HoistContext context)
        {
            var leading = new List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>();
            var trailing = new List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax>();
            var current = statement;

            // Repeatedly hoist the first hoistable ref/out argument until none remain.
            while (true)
            {
                var invocations = current.DescendantNodesAndSelf()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>().ToList();
                var done = true;
                foreach (var invocation in invocations)
                {
                    var hoisted = TryHoist(invocation, current, leading, trailing, context);
                    if (hoisted.changed)
                    {
                        current = hoisted.NewStatement;
                        done = false;
                        break; // restart scan on the rewritten statement
                    }
                }
                if (done) break;
            }

            return (leading, current, trailing);
        }

        private (bool changed,
                 Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax NewStatement,
                 Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax? NewInvocation)
            TryHoist(
                Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation,
                Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax statement,
                List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax> leading,
                List<Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax> trailing,
                HoistContext context)
        {
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (!argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.RefKeyword) &&
                    !argument.RefKindKeyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.OutKeyword))
                    continue;
                if (argument.Expression is not Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma ||
                    ma.Name is not Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax fieldNameId)
                    continue;

                var fieldName = fieldNameId.Identifier.Text;
                string? structName = null;
                try
                {
                    var type = _model.GetTypeInfo(ma.Expression).Type;
                    if (type != null && type.TypeKind == TypeKind.Struct &&
                        _readonlyStructFields.TryGetValue(type.Name, out var fields) &&
                        fields.Contains(fieldName))
                        structName = type.Name;
                }
                catch (ArgumentException)
                {
                    // Node was synthesized by an earlier pass and is not part of the
                    // semantic model's tree — fall back to syntactic resolution.
                }
                if (structName == null)
                {
                    structName = ResolveStructTypeSyntactic(ma.Expression, fieldName, context);
                }
                if (structName == null) continue;

                var tempName = "__cs2jRef" + (_counter++);

                leading.Add(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.LocalDeclarationStatement(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.VariableDeclaration(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName("var")
                                .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))
                        .WithVariables(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SingletonSeparatedList(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.VariableDeclarator(tempName)
                                .WithInitializer(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.EqualsValueClause(
                                    ma.WithoutTrivia())
                                    .WithEqualsToken(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.EqualsToken)
                                        .WithLeadingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)
                                        .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))))))
                    .WithLeadingTrivia(statement.GetLeadingTrivia())
                    .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed));

                var newArguments = invocation.ArgumentList.Arguments.Replace(
                    argument,
                    argument.WithExpression(
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(tempName)));
                var newInvocation = invocation.WithArgumentList(
                    invocation.ArgumentList.WithArguments(newArguments));
                var newStatement = statement.ReplaceNode(invocation, newInvocation);

                // Write-back so any modification through the ref is preserved.
                var receiver = ma.Expression.WithoutTrivia();
                trailing.Add(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ExpressionStatement(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.AssignmentExpression(
                        Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression,
                        receiver,
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.InvocationExpression(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.MemberAccessExpression(
                                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleMemberAccessExpression,
                                receiver,
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName("With" + fieldName)),
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ArgumentList(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SingletonSeparatedList(
                                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Argument(
                                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(tempName)))))))
                    .WithLeadingTrivia(statement.GetLeadingTrivia())
                    .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed));

                return (true, newStatement, newInvocation);
            }

            return (false, statement, null);
        }

        /// <summary>
        /// Syntactic fallback for receiver type resolution when the semantic model
        /// cannot see the node (synthesized by an earlier pass). Uses the pre-collected
        /// name→type context (parameters, locals, fields) plus `this`.
        /// </summary>
        private string? ResolveStructTypeSyntactic(Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression, string fieldName, HoistContext context)
        {
            // this.field — enclosing struct
            if (expression is Microsoft.CodeAnalysis.CSharp.Syntax.ThisExpressionSyntax)
            {
                if (context.ThisStructName != null &&
                    _readonlyStructFields.TryGetValue(context.ThisStructName, out var sf) &&
                    sf.Contains(fieldName))
                    return context.ThisStructName;
                return null;
            }

            // Root identifier of the receiver chain
            Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax current = expression;
            while (true)
            {
                switch (current)
                {
                    case Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma2:
                        current = ma2.Expression;
                        continue;
                    case Microsoft.CodeAnalysis.CSharp.Syntax.ElementAccessExpressionSyntax ea:
                        current = ea.Expression;
                        continue;
                    case Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id:
                        if (context.NameToType.TryGetValue(id.Identifier.Text, out var typeName) &&
                            _readonlyStructFields.TryGetValue(typeName, out var fields2) &&
                            fields2.Contains(fieldName))
                            return typeName;
                        return null;
                    default:
                        return null;
                }
            }
        }

        private static string? ExtractSimpleTypeName(Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax type) => type switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id => id.Identifier.Text,
            Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qn => qn.Right.Identifier.Text,
            Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax gn => gn.Identifier.Text,
            _ => null
        };
    }

    /// <summary>
    /// Ensures every intermediate .csproj pins <c>&lt;LangVersion&gt;latest&lt;/LangVersion&gt;</c>
    /// (inserted into the first PropertyGroup when absent).
    /// </summary>
    public static void EnsureLangVersionLatest(string intermediateRoot)
    {
        foreach (var csproj in Directory.EnumerateFiles(intermediateRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csproj);
            if (content.Contains("<LangVersion>", StringComparison.OrdinalIgnoreCase))
                continue;
            var marker = "<PropertyGroup";
            var index = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var tagEnd = content.IndexOf('>', index);
            if (tagEnd < 0) continue;
            var patched = content.Insert(tagEnd + 1, "\n    <LangVersion>latest</LangVersion>");
            File.WriteAllText(csproj, patched);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex InternalsVisibleToPublicKeyRegex = new(
        "InternalsVisibleTo\\(\"(?<name>[^\"]+?),\\s*PublicKey=[0-9a-fA-F]+\"\\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SignAssemblyRegex = new(
        "<SignAssembly>\\s*true\\s*</SignAssembly>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Strips the <c>PublicKey=...</c> portion of <c>InternalsVisibleTo</c> attributes when
    /// no project in the intermediate tree enables strong-name signing. An unsigned
    /// granting assembly cannot expose internals to a key-qualified friend, so leaving the
    /// key in place fails the build with CS0281 (and hides internal virtual members from
    /// test overrides, CS0115).
    /// </summary>
    public static void NormalizeFriendAssemblyAccess(string intermediateRoot)
    {
        if (AnyProjectSigned(intermediateRoot))
        {
            return;
        }

        foreach (var csFile in Directory.EnumerateFiles(intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csFile);
            if (!content.Contains("InternalsVisibleTo", StringComparison.Ordinal))
            {
                continue;
            }

            var patched = InternalsVisibleToPublicKeyRegex.Replace(
                content,
                match => $"InternalsVisibleTo(\"{match.Groups["name"].Value}\")");
            if (!string.Equals(patched, content, StringComparison.Ordinal))
            {
                File.WriteAllText(csFile, patched);
            }
        }
    }

    private const string TestUiCultureShimFileName = "Cs2jTestUiCultureShim.cs";

    private const string TestUiCultureShimAssemblyPlaceholder = "__CS2J_TEST_ASSEMBLY__";

    private const string TestUiCultureShimSource = """
        // <auto-generated>
        // Injected by CSharpToJava make-readonly preprocessing.
        //
        // .NET Framework localizes framework exception messages (for example the
        // "Parameter name: xxx" suffix of ArgumentException) using the thread UI
        // culture. Test suites written and validated on en-US CI machines assert
        // those English messages and fail on localized operating systems.
        //
        // On .NET Framework only an EXPLICIT per-thread culture assignment
        // switches the native resource lookup language (inherited defaults do
        // not), so this shim installs a thin custom xunit test framework that
        // pins both cultures to en-US on the discovery/execution thread. The
        // module initializer additionally pins the AppDomain-wide defaults for
        // any auxiliary threads.
        //
        // A local ModuleInitializerAttribute polyfill is declared because some
        // referenced assemblies ship their own internal copy, which the compiler
        // would otherwise bind to (CS0122) instead of synthesizing one.
        // </auto-generated>
        #pragma warning disable

        using System;
        using System.Collections.Generic;
        using System.Globalization;
        using System.Reflection;
        using System.Threading;
        using Xunit;
        using Xunit.Abstractions;
        using Xunit.Sdk;

        [assembly: TestFramework("Cs2jTestSupport.Cs2jEnUsCultureTestFramework", "__CS2J_TEST_ASSEMBLY__")]

        namespace Cs2jTestSupport
        {
            internal static class Cs2jEnUsCulture
            {
                internal static void Pin()
                {
                    var enUs = new CultureInfo("en-US");
                    CultureInfo.DefaultThreadCurrentCulture = enUs;
                    CultureInfo.DefaultThreadCurrentUICulture = enUs;
                    Thread.CurrentThread.CurrentCulture = enUs;
                    Thread.CurrentThread.CurrentUICulture = enUs;
                }
            }

            public sealed class Cs2jEnUsCultureTestFramework : XunitTestFramework
            {
                public Cs2jEnUsCultureTestFramework(IMessageSink messageSink)
                    : base(messageSink)
                {
                    Cs2jEnUsCulture.Pin();
                }

                protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
                    => new Cs2jEnUsCultureExecutor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);
            }

            internal sealed class Cs2jEnUsCultureExecutor : XunitTestFrameworkExecutor
            {
                public Cs2jEnUsCultureExecutor(
                    AssemblyName assemblyName,
                    ISourceInformationProvider sourceInformationProvider,
                    IMessageSink diagnosticMessageSink)
                    : base(assemblyName, sourceInformationProvider, diagnosticMessageSink)
                {
                }

                protected override void RunTestCases(
                    IEnumerable<IXunitTestCase> testCases,
                    IMessageSink executionMessageSink,
                    ITestFrameworkExecutionOptions executionOptions)
                {
                    Cs2jEnUsCulture.Pin();
                    base.RunTestCases(testCases, executionMessageSink, executionOptions);
                }
            }
        }

        namespace System.Runtime.CompilerServices
        {
            [AttributeUsage(AttributeTargets.Method, Inherited = false)]
            internal sealed class ModuleInitializerAttribute : Attribute
            {
            }
        }

        internal static class Cs2jTestUiCultureShim
        {
            [System.Runtime.CompilerServices.ModuleInitializer]
            internal static void PinUiCultureToEnUs()
            {
                Cs2jTestSupport.Cs2jEnUsCulture.Pin();
            }
        }
        """;

    /// <summary>
    /// Writes a UI-culture pinning shim into every supported C# test project of the
    /// intermediate tree, so framework exception messages match the en-US format the
    /// upstream tests assert (see <see cref="TestUiCultureShimSource"/>).
    /// </summary>
    public static void InjectTestUiCultureShims(
        string intermediateRoot,
        string sourceRoot,
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        bool verbose)
    {
        if (projectGraph == null)
        {
            return;
        }

        // Track every directory where a shim was written so we can later patch
        // ancestor csproj files to exclude child-directory shims.
        var shimDirectories = new List<string>();

        foreach (var project in projectGraph.ProjectsInTopologicalOrder)
        {
            if (project.Kind != ProjectKind.Test)
            {
                continue;
            }

            var fullProjectPath = Path.GetFullPath(project.ProjectFilePath);
            if (!fullProjectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue; // F#/other-language test projects are not shimmed.
            }

            if (exclusionInfo.ExcludedProjectPaths.Contains(fullProjectPath))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(fullProjectPath);
            if (projectDirectory == null)
            {
                continue;
            }

            var relativeDirectory = Path.GetRelativePath(sourceRoot, projectDirectory);
            var intermediateProjectDirectory = Path.Combine(intermediateRoot, relativeDirectory);
            if (!Directory.Exists(intermediateProjectDirectory))
            {
                continue;
            }

            // The shim is xunit-specific (custom TestFramework); only inject it into
            // test projects that actually reference xunit.
            var projectContent = File.ReadAllText(fullProjectPath);
            if (!projectContent.Contains("Include=\"xunit\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var assemblyName = Path.GetFileNameWithoutExtension(fullProjectPath);
            var shimSource = TestUiCultureShimSource.Replace(TestUiCultureShimAssemblyPlaceholder, assemblyName);
            var shimPath = Path.Combine(intermediateProjectDirectory, TestUiCultureShimFileName);
            File.WriteAllText(shimPath, shimSource, new System.Text.UTF8Encoding(false));
            shimDirectories.Add(intermediateProjectDirectory);
            if (verbose)
            {
                Console.WriteLine($"[make-readonly] Injected test UI-culture shim: {shimPath}");
            }
        }

        // SDK-style csproj files glob-include all .cs files recursively. When a
        // test project directory contains a child test project (e.g. Tests\XPath\
        // has XPath.Tests.csproj and sub-dirs XmlDocument\ and XPathDocument\ with
        // their own csproj), the parent csproj would also compile the child's shim,
        // causing duplicate-definition errors (CS0101). For each csproj in the tree,
        // add <Compile Remove> entries for every shim that lives in a descendant
        // directory that belongs to a DIFFERENT project.
        ExcludeDescendantShimsFromAncestorCsprojs(intermediateRoot, shimDirectories, verbose);
    }

    /// <summary>
    /// For each .csproj in the intermediate tree, adds &lt;Compile Remove&gt; entries
    /// for shim files that live in descendant directories belonging to other projects,
    /// preventing SDK-style glob inclusion from pulling in duplicate shims.
    /// </summary>
    private static void ExcludeDescendantShimsFromAncestorCsprojs(
        string intermediateRoot,
        List<string> shimDirectories,
        bool verbose)
    {
        var sep = Path.DirectorySeparatorChar;
        foreach (var csproj in Directory.EnumerateFiles(intermediateRoot, "*.csproj", SearchOption.AllDirectories))
        {
            var csprojDir = Path.GetDirectoryName(csproj)!;
            // Collect relative paths of descendant shim files (not the csproj's own shim).
            var descendantShimRels = new List<string>();
            foreach (var shimDir in shimDirectories)
            {
                if (string.Equals(shimDir, csprojDir, StringComparison.OrdinalIgnoreCase))
                    continue; // own shim — should be included
                if (!shimDir.StartsWith(csprojDir + sep, StringComparison.OrdinalIgnoreCase))
                    continue; // not a descendant
                var rel = Path.GetRelativePath(csprojDir, Path.Combine(shimDir, TestUiCultureShimFileName));
                descendantShimRels.Add(rel.Replace('\\', '/'));
            }

            if (descendantShimRels.Count == 0)
                continue;

            var content = File.ReadAllText(csproj);
            var removeLines = string.Join("\n    ",
                descendantShimRels.Select(rel => $"<Compile Remove=\"{rel}\" />"));

            // Skip if already present
            if (content.Contains(removeLines, StringComparison.Ordinal))
                continue;

            // Insert a new ItemGroup before </Project>
            var itemGroup = $"\n  <ItemGroup>\n    {removeLines}\n  </ItemGroup>\n";
            var closeProjectIdx = content.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
            if (closeProjectIdx < 0)
                continue;
            var patched = content.Insert(closeProjectIdx, itemGroup);
            File.WriteAllText(csproj, patched, new System.Text.UTF8Encoding(false));
            if (verbose)
            {
                Console.WriteLine($"[make-readonly] Excluded {descendantShimRels.Count} descendant shim(s) from: {csproj}");
            }
        }
    }

    private static bool AnyProjectSigned(string intermediateRoot)
    {
        foreach (var csproj in Directory.EnumerateFiles(intermediateRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (SignAssemblyRegex.IsMatch(File.ReadAllText(csproj)))
            {
                return true;
            }
        }

        // Signing can also be enabled centrally through Directory.Build.props copied
        // to the intermediate root (e.g. a repository-wide strong-name policy).
        foreach (var fileName in ProjectPreprocessSource.MsBuildDirectoryFileNames)
        {
            var propsFile = Path.Combine(intermediateRoot, fileName);
            if (File.Exists(propsFile) && SignAssemblyRegex.IsMatch(File.ReadAllText(propsFile)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Textual pass: adds the <c>readonly</c> modifier to struct declarations that live
    /// inside inactive #if regions (disabled text — invisible to Roslyn and never
    /// compiled), so every struct declaration in the output is readonly.
    /// </summary>
    public static async Task MarkDisabledRegionStructsReadonly(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles, OutputTreeCache? treeCache = null)
    {
        var structPattern = new System.Text.RegularExpressions.Regex(
            @"(?<=^|\n)(?<indent>[ \t]*)(?<mods>(?:(?:public|internal|private|protected|file|unsafe|partial|ref)\s+)*)(?<kw>struct)(?<name>\s+\w+)",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        var outputFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputFiles.Add(outputFile);
        }

        // Textual rewriting: every file is read/replaced/written independently and the
        // regex is thread-safe for matching, so the loop runs in parallel with
        // byte-identical output.
        System.Threading.Tasks.Parallel.ForEach(outputFiles, outputFile =>
        {
            var code = File.ReadAllText(outputFile);

            // Fast pre-check: the pass only adds `readonly` to struct declarations
            // (including ones inside inactive #if regions). A file whose text contains
            // no `struct` keyword has no such declaration — skip the parse entirely.
            if (code.IndexOf("struct", StringComparison.Ordinal) < 0) return;

            // An inactive (#if-false) region can only exist when the file carries a
            // conditional directive, so struct-declaration files without one have
            // nothing the pass can change — the whole-tree walk and regex scan are
            // skipped entirely.
            if (!ContainsConditionalDirective(code)) return;

            var tree = treeCache == null
                ? CSharpSyntaxTree.ParseText(code, path: outputFile)
                : treeCache.Get(outputFile, code);

            // Collect line spans of ACTIVE struct declarations (already handled by the maker).
            // Use the line of the `struct` KEYWORD, not the declaration start: when the
            // struct carries attributes on preceding lines the node starts at the
            // attribute, while the regex below matches the keyword line.
            var activeLines = new HashSet<int>();
            foreach (var structDecl in tree.GetRoot().DescendantNodes()
                         .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>())
            {
                activeLines.Add(tree.GetLineSpan(structDecl.Keyword.Span).StartLinePosition.Line);
            }

            var changed = false;
            var newCode = structPattern.Replace(code, match =>
            {
                var line = tree.GetLineSpan(Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(match.Index, match.Index)).StartLinePosition.Line;
                if (activeLines.Contains(line))
                    return match.Value; // active struct — handled by the maker
                var mods = match.Groups["mods"].Value;
                if (mods.Contains("readonly"))
                    return match.Value;
                changed = true;
                return match.Groups["indent"].Value + mods + "readonly " + match.Groups["kw"].Value + match.Groups["name"].Value;
            });

            if (changed)
            {
                File.WriteAllText(outputFile, newCode, new System.Text.UTF8Encoding(false));
            }
        });
        await Task.CompletedTask;
    }

    /// <summary>
    /// True when the text contains a <c>#if</c> preprocessor directive (the only way
    /// an inactive region — and thus disabled struct text — can appear). Whitespace
    /// between the <c>#</c> and the directive name is tolerated; a directive embedded
    /// in a comment/string only yields a harmless false positive.
    /// </summary>
    private static bool ContainsConditionalDirective(string code)
    {
        var idx = code.IndexOf('#');
        while (idx >= 0)
        {
            var j = idx + 1;
            while (j < code.Length && (code[j] == ' ' || code[j] == '\t')) j++;
            if (j + 1 < code.Length && code[j] == 'i' && code[j + 1] == 'f') return true;
            idx = code.IndexOf('#', idx + 1);
        }
        return false;
    }

    /// <summary>
    /// Companion to TransformMigratedInterfaces: for every concrete type that directly
    /// declares a transformed interface, rewrites the matching void implementation
    /// methods to return the concrete type (<c>return this;</c>). Struct implementers
    /// are excluded — their methods were already migrated by the readonly-struct maker.
    /// </summary>
    public static async Task FixConcreteInterfaceImplementers(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles, OutputTreeCache? treeCache = null)
    {
        // Load the members transformed by TransformMigratedInterfaces (persisted in a
        // metadata file): interface name -> member names.
        var affectedMembersByInterface = await LoadTransformedInterfaceMembers(intermediateRoot);
        if (affectedMembersByInterface.Count == 0) return;

        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        var outputTrees = ReadOutputTreesParallel(outputCsFiles, treeCache, out _);

        System.Threading.Tasks.Parallel.ForEach(outputTrees, kvp =>
        {
            var file = kvp.Key;
            var tree = kvp.Value;
            var root = tree.GetRoot();
            var rewriter = new ConcreteImplementerRewriter(affectedMembersByInterface);
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && !ReferenceEquals(newRoot, root) && newRoot.ToFullString() != root.ToFullString())
            {
                WriteFormatted(file, newRoot);
            }
        });
    }

    /// <summary>
    /// Metadata describing transformed interface members:
    /// interface name -> (its type parameter names, member name -> overload parameter
    /// type keys in the interface's GENERIC form, e.g. "P" or "IRectangle&lt;P&gt;").
    /// </summary>
    private sealed class TransformedInterfaceInfo
    {
        public List<string> TypeParams { get; set; } = new();
        public Dictionary<string, List<string>> Members { get; set; } = new();
    }

    /// <summary>
    /// Reads the <c>.cs2j-iface-members.json</c> metadata written by
    /// TransformMigratedInterfaces.
    /// </summary>
    private static async Task<Dictionary<string, TransformedInterfaceInfo>> LoadTransformedInterfaceMembers(string intermediateRoot)
    {
        var metadataPath = Path.Combine(intermediateRoot, ".cs2j-iface-members.json");
        if (!File.Exists(metadataPath))
            return new Dictionary<string, TransformedInterfaceInfo>(StringComparer.Ordinal);
        try
        {
            var json = await File.ReadAllTextAsync(metadataPath);
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TransformedInterfaceInfo>>(json);
            return map ?? new Dictionary<string, TransformedInterfaceInfo>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return new Dictionary<string, TransformedInterfaceInfo>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Widens void implementation methods of transformed interface members to return
    /// the declaring (concrete) type, appending <c>return this;</c> semantics.
    /// </summary>
    private sealed class ConcreteImplementerRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, TransformedInterfaceInfo> _affectedMembersByInterface;

        public ConcreteImplementerRewriter(Dictionary<string, TransformedInterfaceInfo> affectedMembersByInterface)
        {
            _affectedMembersByInterface = affectedMembersByInterface;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitClassDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax node)
            => RewriteType(node, node.Identifier.Text, node.BaseList);

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitStructDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax node)
        {
            // Struct implementers were already migrated (methods return the struct);
            // only classes need the in-place `return this` widening.
            return base.VisitStructDeclaration(node);
        }

        private Microsoft.CodeAnalysis.SyntaxNode RewriteType(
            Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax node,
            string typeName,
            Microsoft.CodeAnalysis.CSharp.Syntax.BaseListSyntax? baseList)
        {
            if (baseList == null)
                return base.VisitClassDeclaration((Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax)node);

            var affectedMembers = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            foreach (var baseType in baseList.Types)
            {
                var baseName = baseType.Type switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id => id.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax gn => gn.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qn => qn.Right.Identifier.Text,
                    _ => null
                };
                if (baseName == null || !_affectedMembersByInterface.TryGetValue(baseName, out var ifaceInfo) ||
                    ifaceInfo.Members == null)
                    continue;
                foreach (var (memberName, paramKeys) in ifaceInfo.Members)
                {
                    if (!affectedMembers.TryGetValue(memberName, out var set))
                        affectedMembers[memberName] = set = new HashSet<int>();
                    foreach (var pk in paramKeys)
                        set.Add(pk.Length == 0 ? 0 : pk.Split(',').Length);
                }
            }

            if (affectedMembers.Count == 0)
                return base.VisitClassDeclaration((Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax)node);

            var newMembers = node.Members;
            for (var i = 0; i < newMembers.Count; i++)
            {
                if (newMembers[i] is not Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax method ||
                    method.Body == null ||
                    !affectedMembers.TryGetValue(method.Identifier.Text, out var memberArities) ||
                    !memberArities.Contains(method.ParameterList.Parameters.Count))
                    continue;
                if (method.ReturnType is not Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax pre ||
                    !pre.Keyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword))
                    continue;

                // Rewritten methods keep mutating in place but return the instance.
                var newBody = (Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax)
                    new BareReturnToThisRewriter().Visit(method.Body)!;
                var last = newBody.Statements.LastOrDefault();
                var endsWithReturn = last is Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax;
                if (!endsWithReturn)
                {
                    newBody = newBody.AddStatements(
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ReturnStatement(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ThisExpression())
                        .WithReturnKeyword(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)
                            .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));
                }

                newMembers = newMembers.Replace(method, method
                    .WithReturnType(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(typeName)
                        .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))
                    .WithBody(newBody));
            }

            var rewritten = node.WithMembers(newMembers);
            return rewritten == node
                ? base.VisitClassDeclaration((Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax)node)
                : rewritten;
        }

        /// <summary>Replaces bare `return;` with `return this;`.</summary>
        private sealed class BareReturnToThisRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
        {
            public override Microsoft.CodeAnalysis.SyntaxNode? VisitReturnStatement(
                Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax node)
            {
                if (node.Expression == null)
                {
                    return node
                        .WithReturnKeyword(node.ReturnKeyword.WithTrailingTrivia(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))
                        .WithExpression(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ThisExpression());
                }
                return base.VisitReturnStatement(node);
            }
        }
    }

    /// <summary>
    /// Adds bridge explicit interface implementations to CLASSES implementing
    /// transformed interfaces: <c>IFace&lt;T&gt; IFace.M(args) { return M(args); }</c>.
    /// Structs are skipped (their bridges come from the readonly-struct maker + widener).
    /// </summary>
    public static async Task AddClassInterfaceBridges(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles, OutputTreeCache? treeCache = null)
    {
        // Load the members transformed by TransformMigratedInterfaces (metadata file).
        var affectedMembersByInterface = await LoadTransformedInterfaceMembers(intermediateRoot);
        if (affectedMembersByInterface.Count == 0) return;

        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        var outputTrees = ReadOutputTreesParallel(outputCsFiles, treeCache, out _);

        System.Threading.Tasks.Parallel.ForEach(outputTrees, kvp =>
        {
            var file = kvp.Key;
            var tree = kvp.Value;
            var root = tree.GetRoot();
            var rewriter = new ClassInterfaceBridgeRewriter(affectedMembersByInterface);
            var newRoot = rewriter.Visit(root);
            if (newRoot != null && !ReferenceEquals(newRoot, root) && newRoot.ToFullString() != root.ToFullString())
            {
                WriteFormatted(file, newRoot);
            }
        });
    }

    /// <summary>
    /// Adds bridge explicit implementations to classes for widened interface members.
    /// </summary>
    private sealed class ClassInterfaceBridgeRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly Dictionary<string, TransformedInterfaceInfo> _affectedMembersByInterface;

        public ClassInterfaceBridgeRewriter(Dictionary<string, TransformedInterfaceInfo> affectedMembersByInterface)
        {
            _affectedMembersByInterface = affectedMembersByInterface;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitClassDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax node)
        {
            node = (Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
            if (node.BaseList == null) return node;

            var newMembers = node.Members;
            foreach (var baseType in node.BaseList.Types)
            {
                var baseName = baseType.Type switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax id => id.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax gn => gn.Identifier.Text,
                    Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qn => qn.Right.Identifier.Text,
                    _ => null
                };
                if (baseName == null || !_affectedMembersByInterface.TryGetValue(baseName, out var info) ||
                    info.Members == null)
                    continue;

                // Substitute the interface type parameters with the concrete type
                // arguments used in the class's base list (IRectangle<double> → P=double).
                var substitution = new Dictionary<string, string>(StringComparer.Ordinal);
                if (baseType.Type is Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax baseGeneric &&
                    info.TypeParams.Count == baseGeneric.TypeArgumentList.Arguments.Count)
                {
                    for (var ti = 0; ti < info.TypeParams.Count; ti++)
                        substitution[info.TypeParams[ti]] = baseGeneric.TypeArgumentList.Arguments[ti].ToString();
                }

                foreach (var (memberName, paramKeys) in info.Members)
                {
                    foreach (var paramKey in paramKeys)
                    {
                        var concreteKey = paramKey;
                        foreach (var (tp, arg) in substitution)
                        {
                            concreteKey = System.Text.RegularExpressions.Regex.Replace(
                                concreteKey, $@"\b{System.Text.RegularExpressions.Regex.Escape(tp)}\b",
                                arg.Replace("$", "$$"));
                        }
                        var arity = paramKey.Length == 0 ? 0 : paramKey.Split(',').Length;

                        // Skip when an explicit implementation already exists.
                        if (newMembers.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>().Any(m =>
                            m.ExplicitInterfaceSpecifier != null &&
                            m.ExplicitInterfaceSpecifier.Name.ToString().StartsWith(baseName, StringComparison.Ordinal) &&
                            m.Identifier.Text == memberName &&
                            ParamTypesMatch(m, concreteKey)))
                            continue;

                        // Find the concrete implementation whose parameter types match the
                        // substituted interface signature.
                        var concrete = newMembers.OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                            .FirstOrDefault(m => m.Identifier.Text == memberName &&
                                                 m.ExplicitInterfaceSpecifier == null &&
                                                 m.Body != null &&
                                                 ParamTypesMatch(m, concreteKey));
                        if (concrete == null) continue;

                        var arguments = concrete.ParameterList.Parameters
                            .Select(p => Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Argument(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(p.Identifier.Text)))
                            .ToList();
                        var call = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.InvocationExpression(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(memberName),
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ArgumentList(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SeparatedList(arguments)));

                        var bridge = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.MethodDeclaration(
                                baseType.Type.WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space),
                                memberName)
                            .WithExplicitInterfaceSpecifier(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ExplicitInterfaceSpecifier(
                                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseName(baseType.Type.ToString())))
                            .WithParameterList(concrete.ParameterList)
                            .WithBody(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ReturnStatement(call)
                                    .WithReturnKeyword(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)
                                        .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))))
                            .WithLeadingTrivia(
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Comment("// cs2j-class-bridge"),
                                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed);

                        newMembers = newMembers.Add(bridge);
                    }
                }
            }

            return node.WithMembers(newMembers);
        }

        /// <summary>
        /// True when the method's parameter type list equals the substituted interface
        /// parameter key (comma-joined type strings, whitespace-insensitive).
        /// </summary>
        private static bool ParamTypesMatch(
            Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax method, string concreteKey)
        {
            var methodKey = string.Join(",", method.ParameterList.Parameters
                .Select(p => p.Type?.ToString() ?? ""));
            static string Normalize(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
            return Normalize(methodKey) == Normalize(concreteKey);
        }
    }

    /// <summary>
    /// Cross-file pass: readonly-struct migration turns void mutating methods into
    /// struct-returning methods, which breaks interface implementation (return types
    /// must match). For every interface member affected, this pass:
    ///   1. changes the interface method's return type to the interface type itself
    ///      (satisfied via C# 9 covariant returns),
    ///   2. rewrites interface call sites `recv.M(args);` → `recv = recv.M(args);`,
    ///   3. removes the generated explicit interface implementations (the migrated
    ///      public method now implements the member implicitly).
    /// </summary>
    public static async Task TransformMigratedInterfaces(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles, OutputTreeCache? treeCache = null)
    {
        var outputCsFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var outputFile = Path.Combine(intermediateRoot, relativePath);
            if (File.Exists(outputFile))
                outputCsFiles.Add(outputFile);
        }

        var outputTrees = ReadOutputTreesParallel(outputCsFiles, treeCache, out var ifaceCodes);

        // Files whose text contains no `struct` keyword declare no struct, so the
        // readonly-struct discovery below (and the whole-tree walk it needs) only has
        // to run for the struct-bearing files.
        var structTextFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < outputCsFiles.Count; i++)
        {
            if (ifaceCodes[i].IndexOf("struct", StringComparison.Ordinal) >= 0)
                structTextFiles.Add(outputCsFiles[i]);
        }

        var references = BasicReferences();
        var outputCompilation = CSharpCompilation.Create(
            "iface-transform",
            outputTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // 1) Discover conflicts: interface members (void) whose implementing method in a
        //    readonly struct now returns the struct type.
        var transformedInterfaceMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var interfaceMethodFiles = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
        var explicitImplsToWiden = new List<(string File, string IfaceTypeName, string MemberName, INamedTypeSymbol Interface)>();
        var seenWidenKeys = new HashSet<(string, string, string)>();

        // Interfaces that have CLASS implementers cannot have their members' return
        // types changed: classes cannot covariantly return the interface type without
        // explicit bridges, and derived-class overrides break (CS0738/CS0508). Their
        // struct implementers keep the generated explicit interface implementations
        // (void IFace.M(args) { M(args); }), which satisfy the original contract.
        //
        // The set is computed lazily on the first candidate interface: when no source-
        // declared interface is implemented by a readonly struct, this whole-tree class
        // scan is never needed, so it is skipped entirely.
        var classImplementedInterfaces = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var classScanDone = false;
        void EnsureClassScan()
        {
            if (classScanDone) return;
            classScanDone = true;
            foreach (var (_, tree) in outputTrees)
            {
                var classDecls = tree.GetRoot().DescendantNodes()
                    .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                    .Where(c => c.BaseList != null)
                    .ToList();
                if (classDecls.Count == 0) continue;
                var model = outputCompilation.GetSemanticModel(tree);
                foreach (var classDecl in classDecls)
                {
                    if (model.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol) continue;
                    foreach (var iface in classSymbol.AllInterfaces)
                    {
                        if (iface.DeclaringSyntaxReferences.Length > 0)
                            classImplementedInterfaces.Add(iface);
                    }
                }
            }
        }

        foreach (var (file, tree) in outputTrees)
        {
            // Files without a readonly struct declaration cannot contribute; decide
            // syntactically first so the semantic model is only created for candidates.
            if (!structTextFiles.Contains(file)) continue;
            var structDecls = tree.GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax>()
                .Where(s => s.Modifiers.Any(m => m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReadOnlyKeyword)))
                .ToList();
            if (structDecls.Count == 0) continue;
            var model = outputCompilation.GetSemanticModel(tree);
            foreach (var structDecl in structDecls)
            {
                if (model.GetDeclaredSymbol(structDecl) is not INamedTypeSymbol structSymbol)
                    continue;

                foreach (var iface in structSymbol.AllInterfaces)
                {
                    if (iface.DeclaringSyntaxReferences.Length == 0)
                        continue; // external interface — cannot rewrite
                    EnsureClassScan();
                    if (classImplementedInterfaces.Contains(iface))
                        continue; // interface also implemented by classes — do not transform
                    foreach (var member in iface.GetMembers())
                    {
                        if (member is not IMethodSymbol ifaceMethod ||
                            ifaceMethod.MethodKind != MethodKind.Ordinary ||
                            !ifaceMethod.ReturnsVoid)
                            continue;
                        // The migrated overload returns the struct type. It may be hidden
                        // behind a generated explicit implementation (which satisfies the
                        // interface today), so search the struct's members directly.
                        var migratedImpl = structSymbol.GetMembers(ifaceMethod.Name)
                            .OfType<IMethodSymbol>()
                            .FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary &&
                                                 !m.IsStatic &&
                                                 m.Parameters.Length == ifaceMethod.Parameters.Length &&
                                                 SymbolEqualityComparer.Default.Equals(m.ReturnType, structSymbol));
                        if (migratedImpl == null) continue;

                        transformedInterfaceMethods.Add(ifaceMethod);
                        var ifaceSyntaxFile = ifaceMethod.DeclaringSyntaxReferences[0].SyntaxTree.FilePath;
                        interfaceMethodFiles[ifaceMethod] = outputTrees.ContainsKey(ifaceSyntaxFile)
                            ? ifaceSyntaxFile
                            : outputTrees.Keys.FirstOrDefault(f => string.Equals(
                                Path.GetFileName(f), Path.GetFileName(ifaceSyntaxFile),
                                StringComparison.OrdinalIgnoreCase)) ?? "";

                        // Structs cannot use covariant returns (CS8825), so their
                        // generated explicit implementations are WIDENED to return the
                        // interface type instead of being removed. Deduplicated by
                        // (file, interface, member) — overload arity does not matter
                        // because one explicit implementation is widened once.
                        var widenKey = (file,
                            iface.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                            ifaceMethod.Name);
                        if (!seenWidenKeys.Contains(widenKey))
                        {
                            seenWidenKeys.Add(widenKey);
                            explicitImplsToWiden.Add((widenKey.file, widenKey.Item2, widenKey.Name, iface));
                        }
                    }
                }
            }
        }

        if (transformedInterfaceMethods.Count == 0) return;

        // Persist the affected members for the companion passes (they re-parse the
        // files and cannot distinguish transformed members from members that always
        // returned the interface type, e.g. ICurve.Clone()). Each member entry carries
        // the interface-generic parameter type keys of the affected overloads.
        var affectedMap = transformedInterfaceMethods
            .GroupBy(m => m.ContainingType.Name)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    TypeParams = g.First().ContainingType.TypeParameters.Select(tp => tp.Name).ToList(),
                    Members = g.GroupBy(m => m.Name)
                        .ToDictionary(
                            mg => mg.Key,
                            // Record parameter types in the interface's GENERIC form
                            // (OriginalDefinition) so every implementer can substitute
                            // its own type arguments.
                            mg => mg.Select(m => string.Join(",",
                                m.OriginalDefinition.Parameters.Select(p =>
                                    p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)))).Distinct().ToList())
                });
        await File.WriteAllTextAsync(
            Path.Combine(intermediateRoot, ".cs2j-iface-members.json"),
            System.Text.Json.JsonSerializer.Serialize(affectedMap));

        // 2) Rewrite interface declarations, call sites, and remove explicit impls.
        var affectedMemberKeys = transformedInterfaceMethods
            .Select(m => (InterfaceName: m.ContainingType.Name, MemberName: m.Name))
            .ToHashSet();

        System.Threading.Tasks.Parallel.ForEach(outputTrees, kvp =>
        {
            var file = kvp.Key;
            var tree = kvp.Value;
            var root = tree.GetRoot();
            var ifaceRewriter = new InterfaceReturnTypeRewriter(affectedMemberKeys);
            var callSiteRewriter = new InterfaceCallSiteRewriter(transformedInterfaceMethods, outputCompilation);
            var explicitWidener = new ExplicitInterfaceImplWidener(explicitImplsToWiden);
            var newRoot = ifaceRewriter.Visit(root);
            newRoot = callSiteRewriter.Visit(newRoot);
            newRoot = explicitWidener.Visit(newRoot);
            if (newRoot != null && !ReferenceEquals(newRoot, root) && newRoot.ToFullString() != root.ToFullString())
            {
                WriteFormatted(file, newRoot);
            }
        });
    }

    /// <summary>
    /// Changes the return type of affected interface methods to the declaring
    /// interface type (including its type parameters). Affected members are
    /// identified by (interface name, member name) pairs.
    /// </summary>
    private sealed class InterfaceReturnTypeRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly HashSet<(string InterfaceName, string MemberName)> _affected;

        public InterfaceReturnTypeRewriter(HashSet<(string InterfaceName, string MemberName)> affected)
        {
            _affected = affected;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitInterfaceDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.InterfaceDeclarationSyntax node)
        {
            var ifaceName = node.Identifier.Text;

            var ifaceTypeSyntax = node.TypeParameterList != null
                ? (Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax)Microsoft.CodeAnalysis.CSharp.SyntaxFactory
                    .GenericName(node.Identifier)
                    .WithTypeArgumentList(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TypeArgumentList(
                        Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SeparatedList(
                            node.TypeParameterList.Parameters.Select(p =>
                                (Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax)Microsoft.CodeAnalysis.CSharp.SyntaxFactory
                                    .IdentifierName(p.Identifier.Text)))))
                : Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(node.Identifier);

            var newMembers = node.Members;
            for (var i = 0; i < newMembers.Count; i++)
            {
                if (newMembers[i] is Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax method &&
                    method.ReturnType is Microsoft.CodeAnalysis.CSharp.Syntax.PredefinedTypeSyntax pre &&
                    pre.Keyword.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.VoidKeyword) &&
                    _affected.Contains((ifaceName, method.Identifier.Text)))
                {
                    newMembers = newMembers.Replace(method,
                        method.WithReturnType(ifaceTypeSyntax.WithTrailingTrivia(
                            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));
                }
            }

            return base.VisitInterfaceDeclaration(node.WithMembers(newMembers));
        }
    }

    /// <summary>
    /// Rewrites statement-level calls through transformed interface methods:
    /// `recv.M(args);` → `recv = recv.M(args);`
    /// </summary>
    private sealed class InterfaceCallSiteRewriter : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly HashSet<IMethodSymbol> _methods;
        private readonly CSharpCompilation _compilation;

        public InterfaceCallSiteRewriter(HashSet<IMethodSymbol> methods, CSharpCompilation compilation)
        {
            _methods = methods;
            _compilation = compilation;
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitExpressionStatement(
            Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax node)
        {
            if (node.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation &&
                invocation.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax ma)
            {
                try
                {
                    var tree = node.SyntaxTree;
                    if (tree != null)
                    {
                        var model = _compilation.GetSemanticModel(tree);
                        var symbol = model.GetSymbolInfo(invocation).Symbol;
                        if (symbol is IMethodSymbol methodSymbol &&
                            _methods.Contains(methodSymbol) &&
                            IsAssignableLValue(ma.Expression))
                        {
                            var assignment = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.AssignmentExpression(
                                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleAssignmentExpression,
                                ma.Expression.WithoutTrivia(),
                                invocation.WithoutTrivia());
                            return node.WithExpression(assignment);
                        }
                    }
                }
                catch (ArgumentException)
                {
                }
            }
            return base.VisitExpressionStatement(node);
        }

        private static bool IsAssignableLValue(Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionSyntax expression) => expression switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax => true,
            Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax m => IsAssignableLValue(m.Expression),
            Microsoft.CodeAnalysis.CSharp.Syntax.ElementAccessExpressionSyntax e => IsAssignableLValue(e.Expression),
            _ => false
        };
    }

    /// <summary>
    /// Widens the generated explicit interface implementations of transformed members:
    /// <code>
    /// void IFace.M(args) { M(args); }  →  IFace&lt;T&gt; IFace.M(args) { return M(args); }
    /// </code>
    /// C# forbids covariant returns for structs (CS8825), but explicit implementations
    /// may return the interface type directly, preserving the interface contract.
    /// </summary>
    private sealed class ExplicitInterfaceImplWidener : Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter
    {
        private readonly Dictionary<(string Iface, string Member), INamedTypeSymbol> _toWiden;
        private readonly HashSet<string> _files;
        private string _currentFile = "";

        public ExplicitInterfaceImplWidener(List<(string File, string IfaceTypeName, string MemberName, INamedTypeSymbol Interface)> widens)
        {
            _toWiden = new Dictionary<(string Iface, string Member), INamedTypeSymbol>();
            foreach (var w in widens)
                _toWiden[(w.IfaceTypeName, w.MemberName)] = w.Interface;
            _files = widens.Select(r => r.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitCompilationUnit(
            Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax node)
        {
            _currentFile = node.SyntaxTree?.FilePath ?? "";
            return base.VisitCompilationUnit(node);
        }

        public override Microsoft.CodeAnalysis.SyntaxNode? VisitMethodDeclaration(
            Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax node)
        {
            if (node.ExplicitInterfaceSpecifier == null || !_files.Contains(_currentFile))
                return base.VisitMethodDeclaration(node);

            var ifaceName = node.ExplicitInterfaceSpecifier.Name.ToString();
            var memberName = node.Identifier.Text;
            if (!_toWiden.TryGetValue((ifaceName, memberName), out var ifaceSymbol))
                return base.VisitMethodDeclaration(node);

            // Interface return type including its type parameters, e.g. IRectangle<P>.
            var ifaceTypeSyntax = BuildInterfaceTypeSyntax(node.ExplicitInterfaceSpecifier.Name, ifaceSymbol);

            // Argument list from the explicit implementation's parameters.
            var arguments = node.ParameterList.Parameters
                .Select(p => Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Argument(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(p.Identifier.Text)))
                .ToList();
            var call = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.InvocationExpression(
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(memberName),
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ArgumentList(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SeparatedList(arguments)));

            var body = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Block(
                Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ReturnStatement(call)
                    .WithReturnKeyword(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Token(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReturnKeyword)
                        .WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space)));

            return node
                .WithReturnType(ifaceTypeSyntax.WithTrailingTrivia(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Space))
                .WithBody(body)
                .WithLeadingTrivia(node.GetLeadingTrivia()
                    .Add(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Comment("// cs2j-iface-bridge"))
                    .Add(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.CarriageReturnLineFeed));
        }

        private static Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax BuildInterfaceTypeSyntax(
            Microsoft.CodeAnalysis.CSharp.Syntax.NameSyntax declaredName, INamedTypeSymbol ifaceSymbol)
        {
            if (ifaceSymbol.TypeParameters.Length == 0)
                return declaredName;
            if (declaredName is Microsoft.CodeAnalysis.CSharp.Syntax.GenericNameSyntax generic)
                return generic; // already carries concrete type arguments
            return Microsoft.CodeAnalysis.CSharp.SyntaxFactory.GenericName(
                    declaredName is Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified
                        ? qualified.Right.Identifier
                        : ((Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax)declaredName).Identifier)
                .WithTypeArgumentList(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.TypeArgumentList(
                    Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SeparatedList(
                        ifaceSymbol.TypeParameters.Select(tp =>
                            (Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax)Microsoft.CodeAnalysis.CSharp.SyntaxFactory
                                .IdentifierName(tp.Name)))));
        }
    }
}
