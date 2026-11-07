using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// R13 / VS-0 step 4 first cut: the final-tree semantic closure scanner only
/// enumerates constructs, maps them to enabled+successful owner passes and
/// registers the leftovers as NORM-SEMANTIC-CLOSURE. Finalizers are a bounded
/// framework with no real implementation yet (every contract entry is a no-op).
/// </summary>
public sealed class SemanticClosureScannerTests
{
    [Fact]
    public void Type_parameter_constructs_are_classified_and_scoped_to_the_file()
    {
        var tree = CreateTree(("Generic.cs", """
            class C<T>
            {
                void M<U>()
                {
                    var a = typeof(T);
                    var b = default(T);
                    var c = new T();
                    var d = default(int);
                    var e = typeof(string);
                    var f = new C<int>();
                    var g = typeof(U);
                }
            }
            """));

        try
        {
            var result = SemanticClosureScanner.Scan(tree, NoPasses());

            Assert.Equal(
                new[]
                {
                    SemanticClosureScanner.GenericTypeofTypeParameter,
                    SemanticClosureScanner.GenericDefaultTypeParameter,
                    SemanticClosureScanner.GenericNewTypeParameter,
                    SemanticClosureScanner.GenericTypeofTypeParameter,
                },
                result.Constructs.Select(construct => construct.Id).ToArray());

            Assert.All(
                result.Constructs,
                construct => Assert.Equal("JCS.GENERIC", construct.Capability));
            Assert.All(result.Constructs, construct => Assert.Equal("Generic.cs", construct.File));
            Assert.All(result.Constructs, construct => Assert.True(construct.Line > 0));
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Collection_invoke_and_condition_effects_are_enumerated()
    {
        var tree = CreateTree(("Constructs.cs", """
            class C
            {
                static bool F(out int value) { value = 0; return true; }

                void M(int i, int[] xs)
                {
                    int[] local = [1, 2];
                    xs.Invoke();
                    while (i++ < 3) { }
                    do { } while (--i > 0);
                    if (F(out i)) { }
                    var q = (i = 2) > 0 ? 1 : 2;
                }
            }
            """));

        try
        {
            var result = SemanticClosureScanner.Scan(tree, NoPasses());

            Assert.Equal(
                new[]
                {
                    SemanticClosureScanner.CollectionExpression,
                    SemanticClosureScanner.ExplicitInvoke,
                    SemanticClosureScanner.ConditionEffect,
                    SemanticClosureScanner.ConditionEffect,
                    SemanticClosureScanner.ConditionEffect,
                    SemanticClosureScanner.ConditionEffect,
                },
                result.Constructs.Select(construct => construct.Id).ToArray());

            Assert.Equal(
                new[] { "JCS.CONVERSION", "JCS.CALL", "JCS.EVAL", "JCS.EVAL", "JCS.EVAL", "JCS.EVAL" },
                result.Constructs.Select(construct => construct.Capability).ToArray());
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void A_condition_without_side_effects_is_not_a_construct()
    {
        var tree = CreateTree(("Clean.cs", """
            class C
            {
                void M(int i)
                {
                    if (i > 0) { }
                    while (i < 3) { i++; }
                    var q = i > 0 ? 1 : 2;
                }
            }
            """));

        try
        {
            var result = SemanticClosureScanner.Scan(tree, NoPasses());

            Assert.Empty(result.Constructs);
            Assert.Empty(result.UnownedConstructs);
            Assert.True(result.IsClosed);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void An_enabled_successful_owner_takes_the_construct()
    {
        var tree = CreateTree(("Generic.cs", """
            class C<T>
            {
                void M()
                {
                    var a = typeof(T);
                    var b = default(T);
                    var c = new T();
                }
            }
            """));

        try
        {
            var result = SemanticClosureScanner.Scan(
                tree,
                new[]
                {
                    Pass("generic-deepen", "JCS.GENERIC"),
                    Pass("generic", "JCS.GENERIC"),
                });

            var owners = result.Constructs
                .ToDictionary(construct => construct.Id, construct => construct.Owner);

            Assert.Equal("generic-deepen", owners[SemanticClosureScanner.GenericTypeofTypeParameter]);
            Assert.Equal("generic", owners[SemanticClosureScanner.GenericDefaultTypeParameter]);
            Assert.Equal("generic", owners[SemanticClosureScanner.GenericNewTypeParameter]);
            Assert.Empty(result.UnownedConstructs);
            Assert.True(result.IsClosed);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void A_missing_owner_registers_norm_semantic_closure_with_file_line_and_reason()
    {
        var tree = CreateTree(("Generic.cs", """
            class C<T>
            {
                void M()
                {
                    var a = typeof(T);
                }
            }
            """));

        try
        {
            var result = SemanticClosureScanner.Scan(tree, NoPasses());

            var residual = Assert.Single(result.UnownedConstructs);
            Assert.Equal(PassConstructIds.SemanticClosure, residual.Code);
            Assert.Equal(SemanticClosureScanner.GenericTypeofTypeParameter, residual.Construct);
            Assert.Equal("JCS.GENERIC", residual.Capability);
            Assert.Equal("Generic.cs", residual.File);
            Assert.Equal(result.Constructs[0].Line, residual.Line);
            Assert.Equal("generic-deepen", residual.CandidateOwner);
            Assert.False(string.IsNullOrWhiteSpace(residual.Reason));
            Assert.Null(result.Constructs[0].Owner);
            Assert.False(result.IsClosed);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void A_failed_owner_does_not_take_the_construct()
    {
        var tree = CreateTree(("Generic.cs", """
            class C<T>
            {
                void M()
                {
                    var a = typeof(T);
                }
            }
            """));

        try
        {
            var result = SemanticClosureScanner.Scan(
                tree,
                new[] { FailedPass("generic-deepen", "JCS.GENERIC") });

            var residual = Assert.Single(result.UnownedConstructs);
            Assert.Contains("did not succeed", residual.Reason, StringComparison.Ordinal);
            Assert.Equal("generic-deepen", residual.CandidateOwner);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void A_disabled_owner_does_not_take_the_construct()
    {
        var tree = CreateTree(("Collection.cs", "class C { void M() { int[] xs = [1, 2]; } }"));

        try
        {
            var result = SemanticClosureScanner.Scan(
                tree,
                new[] { Pass("generic", "JCS.GENERIC") });

            var residual = Assert.Single(result.UnownedConstructs);
            Assert.Equal(SemanticClosureScanner.CollectionExpression, residual.Construct);
            Assert.Equal("expression-desugar", residual.CandidateOwner);
            Assert.Contains("not enabled", residual.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Finalizers_run_once_each_in_chain_order()
    {
        var calls = new List<string>();
        var passes = new[]
        {
            Pass("generic", "JCS.GENERIC"),
            Finalizer("finalize-generic", "JCS.GENERIC"),
            Pass("struct-value-semantics", "JCS.VALUE"),
            Finalizer("finalize-value", "JCS.VALUE"),
        };

        var implements = new ISemanticFinalizer[]
        {
            new RecordingFinalizer("finalize-generic", "JCS.GENERIC", calls),
            new RecordingFinalizer("finalize-value", "JCS.VALUE", calls),
        };

        var tree = CreateTree(("Clean.cs", "class C { }"));

        try
        {
            var result = SemanticClosureScanner.Scan(tree, passes, implements);

            Assert.Equal(new[] { "finalize-generic", "finalize-value" }, calls);
            Assert.Equal(2, result.Finalizers.Count);
            Assert.All(result.Finalizers, entry => Assert.True(entry.Executed));
            Assert.True(result.IsClosed);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void A_declared_finalizer_without_an_implementation_is_a_no_op()
    {
        var tree = CreateTree(("Clean.cs", "class C { }"));

        try
        {
            var result = SemanticClosureScanner.Scan(
                tree,
                new[]
                {
                    Pass("generic", "JCS.GENERIC"),
                    Finalizer("finalize-generic", "JCS.GENERIC"),
                });

            var entry = Assert.Single(result.Finalizers);
            Assert.Equal("finalize-generic", entry.PassId);
            Assert.False(entry.Executed);
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
            Assert.Empty(result.UnownedConstructs);
            Assert.True(result.IsClosed);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void An_unknown_finalizer_capability_registers_a_residual()
    {
        var tree = CreateTree(("Clean.cs", "class C { }"));

        try
        {
            var result = SemanticClosureScanner.Scan(
                tree,
                new[] { Finalizer("finalize-bad", "JCS.NOTREAL") });

            Assert.False(result.IsClosed);
            var residual = Assert.Single(result.UnownedConstructs);
            Assert.Equal(PassConstructIds.SemanticClosure, residual.Code);
            Assert.Equal(SemanticClosureScanner.FinalizerDeclaration, residual.Construct);
            Assert.Contains("invalid FinalizerFor", residual.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void A_finalizer_without_a_declaring_owner_registers_a_residual()
    {
        var tree = CreateTree(("Clean.cs", "class C { }"));

        try
        {
            var result = SemanticClosureScanner.Scan(
                tree,
                new[] { Finalizer("finalize-value", "JCS.VALUE") });

            Assert.False(result.IsClosed);
            var residual = Assert.Single(result.UnownedConstructs);
            Assert.Equal("finalize-value", residual.CandidateOwner);
            Assert.Contains("no enabled pass declares", residual.Reason, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Constructs_are_sorted_by_file_line_and_column()
    {
        var tree = CreateTree(
            ("b/Second.cs", "class C { void M() { int[] xs = [1]; } }"),
            ("a/First.cs", "class C { void M() { int[] ys = [2]; } }"));

        try
        {
            var result = SemanticClosureScanner.Scan(tree, NoPasses());

            Assert.Equal(
                new[] { "a/First.cs", "b/Second.cs" },
                result.Constructs.Select(construct => construct.File).ToArray());
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void An_empty_tree_is_closed()
    {
        var tree = CreateEmptyTree();

        try
        {
            var result = SemanticClosureScanner.Scan(tree, NoPasses());

            Assert.Empty(result.Constructs);
            Assert.Empty(result.UnownedConstructs);
            Assert.Empty(result.Finalizers);
            Assert.True(result.IsClosed);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    private static IReadOnlyList<JcsCapabilityScanPass> NoPasses()
        => Array.Empty<JcsCapabilityScanPass>();

    private static JcsCapabilityScanPass Pass(string id, params string[] capabilities)
        => new(
            id,
            Contract(capabilities),
            Succeeded: true,
            Diagnostics: Array.Empty<string>());

    private static JcsCapabilityScanPass FailedPass(string id, params string[] capabilities)
        => new(
            id,
            Contract(capabilities),
            Succeeded: false,
            Diagnostics: new[] { "[Error] failed" });

    private static JcsCapabilityScanPass Finalizer(string id, string finalizerFor)
        => new(
            id,
            Contract(Array.Empty<string>(), PassPhase.Finalize, finalizerFor),
            Succeeded: true,
            Diagnostics: Array.Empty<string>());

    private static PassContract Contract(
        string[] capabilities,
        PassPhase phase = PassPhase.Rewrite,
        string? finalizerFor = null)
        => new(
            Consumes: Array.Empty<string>(),
            Produces: Array.Empty<string>(),
            Preconditions: Array.Empty<string>(),
            Postconditions: Array.Empty<string>(),
            MayLeave: Array.Empty<string>(),
            Owners: Array.Empty<string>(),
            Policy: ResidualPolicy.Fail,
            Idempotent: true,
            RequiresWholeTree: false,
            CapabilityIds: capabilities,
            Phase: phase,
            FinalizerFor: finalizerFor);

    private sealed class RecordingFinalizer : ISemanticFinalizer
    {
        private readonly List<string> _calls;

        public RecordingFinalizer(string passId, string capability, List<string> calls)
        {
            PassId = passId;
            Capability = capability;
            _calls = calls;
        }

        public string PassId { get; }

        public string Capability { get; }

        public void Execute(SemanticClosureContext context) => _calls.Add(PassId);
    }

    private static string CreateTree(params (string Name, string Text)[] files)
    {
        var root = CreateEmptyTree();
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        File.WriteAllText(
            Path.Combine(root, "App.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        return root;
    }

    private static string CreateEmptyTree()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "cs2j-semantic-closure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTree(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
