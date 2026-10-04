using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.ExtensionMethodResolve.Declarations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests;

/// <summary>
/// C49 M2 rewrite tests (design §7.3): EMD-OP materializes block
/// binary/unary/checked operators (lifted included) and rewrites operator
/// sites; EMD-STATIC materializes block static methods/properties and
/// rewrites receiver-type-name qualified sites with explicit block type
/// arguments; two-level n+m generic mapping with constraints; receiver
/// ref/in/scoped/ref-readonly argument forms; CS2J1211 pointer gate;
/// CS2J1208 chain gate. Every positive case is recompiled clean and run a
/// second time with zero diff.
/// </summary>
public sealed class ExtensionBlockM2Tests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.IEnumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private static readonly CSharpCompilationOptions CompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable);

    private static string Compact(string code)
        => string.Concat(code.Where(c => !char.IsWhiteSpace(c)));

    private static (CSharpCompilation Compilation, SemanticModel Model, SyntaxTree Tree) CompileOne(
        string source,
        string fileName = "Test.cs")
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest), path: fileName);
        var compilation = CSharpCompilation.Create(
            "c49m2-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        return (compilation, compilation.GetSemanticModel(tree), tree);
    }

    private static ExtensionFileResult RunBlockRewrite(
        CSharpCompilation compilation,
        SemanticModel model,
        ExtensionBlockOptions? blockOptions = null,
        bool chainAvailable = true)
    {
        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(
            new ExtensionMethodResolveOptions(),
            blockOptions ?? new ExtensionBlockOptions { MaterializeExtensionBlocks = true },
            chainAvailable);
        return preprocessor.RewriteFile(model);
    }

    private static CSharpCompilation Recompile(string code)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "c49m2verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "rewritten code must compile: " + string.Join("; ", errors.Select(e => e.ToString())));
        return compilation;
    }

    private static void AssertSecondRunStable(string code)
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "c49m2second-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            CompilationOptions);
        var model = compilation.GetSemanticModel(tree);

        var preprocessor = new ExtensionMethodResolvePreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze(
            new ExtensionMethodResolveOptions(),
            new ExtensionBlockOptions { MaterializeExtensionBlocks = true },
            memberDesugarChainAvailable: true);
        var second = preprocessor.RewriteFile(model);

        if (second.Changed)
        {
            Assert.Fail(
                "a second run over materialized code must report Changed=false\n"
                + "diagnostics: " + string.Join("; ", second.Diagnostics.Select(d => d.Code + ":" + d.Message))
                + "\noutput:\n" + second.OutputCode);
        }

        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), second.OutputCode);
    }

    private static void AssertHostCalls(
        CSharpCompilation compilation,
        SyntaxTree tree,
        string hostDisplay,
        IReadOnlySet<string> expectedMembers)
    {
        var model = compilation.GetSemanticModel(tree);
        var found = new HashSet<string>();
        foreach (var invocation in tree.GetCompilationUnitRoot().DescendantNodes()
                     .OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
                || method.ContainingType.ToDisplayString() != hostDisplay)
            {
                continue;
            }

            found.Add(method.Name);
        }

        Assert.Equal(expectedMembers, found);
    }

    private static void AssertNoExtensionSugar(CSharpCompilation compilation, SyntaxTree tree)
    {
        var root = tree.GetCompilationUnitRoot();
        Assert.False(root.DescendantNodes().Any(n => n.IsKind(SyntaxKind.ExtensionBlockDeclaration)));

        foreach (var node in root.DescendantNodes()
                     .OfType<ExpressionSyntax>()
                     .Where(n => n.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                                 || n.IsKind(SyntaxKind.MemberBindingExpression)))
        {
            var symbol = compilation.GetSemanticModel(tree).GetSymbolInfo(node).Symbol;
            var container = symbol switch
            {
                IPropertySymbol property => property.ContainingType,
                IMethodSymbol method => method.ContainingType,
                _ => null,
            };

            Assert.NotEqual(TypeKind.Extension, container?.TypeKind);
        }
    }

    private static void AssertPositiveCase(
        string source,
        string hostDisplay,
        IReadOnlySet<string> expectedMembers,
        params string[] compactFragments)
    {
        var (compilation, model, _) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        Assert.True(result.Changed, "source should be transformed");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);

        var compact = Compact(result.OutputCode);
        foreach (var fragment in compactFragments)
        {
            Assert.True(compact.Contains(Compact(fragment)),
                $"fragment not found: {fragment}\nactual output:\n{result.OutputCode}");
        }

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        AssertHostCalls(verifyCompilation, verifyTree, hostDisplay, expectedMembers);
        AssertNoExtensionSugar(verifyCompilation, verifyTree);
        AssertSecondRunStable(result.OutputCode);
    }

    // ── EMD-OP: binary operators ───────────────────────────────────────────

    [Fact]
    public void Operator_Binary_BecomesHostStaticMethod()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static Vec operator +(Vec a, Vec b)
                            => new Vec { X = a.X + b.X };
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var a = new Vec();
                        var b = new Vec();
                        Vec c = a + b;
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Add" },
            "public static Vec Add(Vec a, Vec b)",
            "global::N.Holder.Add(a, b)");
    }

    [Fact]
    public void Operator_Checked_StrictNameAndUnwrappedShell()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static Vec operator checked +(Vec a, Vec b)
                            => new Vec { X = checked(a.X + b.X) };
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var a = new Vec();
                        var b = new Vec();
                        Vec c = checked(a + b);
                    }
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);

        var compact = Compact(result.OutputCode);
        Assert.Contains(Compact("public static Vec AddChecked(Vec a, Vec b)"), compact);
        Assert.Contains(Compact("global::N.Holder.AddChecked(a, b)"), compact);
        // checked wrapper removed at the operator site (checked semantics live in AddChecked).
        Assert.DoesNotContain(Compact("checked(global::N.Holder.AddChecked(a, b))"), compact);

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        AssertHostCalls(verifyCompilation, verifyTree, "N.Holder",
            new HashSet<string> { "AddChecked" });
        AssertSecondRunStable(result.OutputCode);
    }

    [Fact]
    public void Operator_LiftedBinary_UsesLiftedFormula()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static Vec operator +(Vec a, Vec b)
                            => new Vec { X = a.X + b.X };
                    }
                }

                public class User
                {
                    public void Go(Vec? x, Vec? y)
                    {
                        Vec? z = x + y;
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Add" },
            "x.HasValue & y.HasValue ? global::N.Holder.Add(x.Value, y.Value) : (Vec?)null");
    }

    // ── EMD-OP: unary operators ────────────────────────────────────────────

    [Fact]
    public void Operator_Unary_BecomesHostStaticMethod()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static Vec operator -(Vec a)
                            => new Vec { X = -a.X };
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var a = new Vec();
                        Vec b = -a;
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Negate" },
            "public static Vec Negate(Vec a)",
            "global::N.Holder.Negate(a)");
    }

    [Fact]
    public void Operator_LiftedUnary_UsesLiftedFormula()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static Vec operator -(Vec a)
                            => new Vec { X = -a.X };
                    }
                }

                public class User
                {
                    public void Go(Vec? x)
                    {
                        Vec? y = -x;
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Negate" },
            "x.HasValue ? global::N.Holder.Negate(x.Value) : (Vec?)null");
    }

    [Fact]
    public void Operator_DistinctKinds_BindSeparately()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static Vec operator +(Vec a, Vec b)
                            => new Vec { X = a.X + b.X };
                        public static Vec operator -(Vec a, Vec b)
                            => new Vec { X = a.X - b.X };
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var a = new Vec();
                        var b = new Vec();
                        Vec c = a + b;
                        Vec d = c - a;
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Add", "Subtract" },
            "global::N.Holder.Add(a, b)",
            "global::N.Holder.Subtract(c, a)");
    }

    [Fact]
    public void Operator_Compound_BecomesSimpleAssignment()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static Vec operator +(Vec a, Vec b)
                            => new Vec { X = a.X + b.X };
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var a = new Vec();
                        var b = new Vec();
                        a += b;
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Add" },
            "a = global::N.Holder.Add(a, b)");
    }

    [Fact]
    public void Operator_OrdinaryClassOperator_IsNotRewritten()
    {
        const string source = """
            namespace N
            {
                public struct Classic
                {
                    public int X;
                    public static Classic operator +(Classic a, Classic b)
                        => new Classic { X = a.X + b.X };
                }

                public static class Holder
                {
                    extension(Classic r)
                    {
                        public Classic Double() => new Classic { X = r.X * 2 };
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var x = new Classic();
                        var y = new Classic();
                        Classic z = x + y;
                    }
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        Assert.True(result.Changed, "block itself is still materialized");
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);
        Assert.Contains(Compact("Classic z = x + y;"), Compact(result.OutputCode));

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        // No host call from the ordinary operator site (Double member exists but is unused).
        AssertHostCalls(verifyCompilation, verifyTree, "N.Holder", new HashSet<string>());
        AssertSecondRunStable(result.OutputCode);
    }

    [Fact]
    public void IllegalSource_ConversionOperatorInBlock_IsCS9282()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static implicit operator int(Vec a) => a.X;
                    }
                }
            }
            """;

        var (compilation, _, _) = CompileOne(source);
        Assert.Contains(compilation.GetDiagnostics(), d => d.Id == "CS9282");
    }

    // ── EMD-STATIC: static methods ─────────────────────────────────────────

    [Fact]
    public void StaticMethod_TypeNameQualified_ViaReceiverType()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static string Describe() => "vec";
                    }
                }

                public class User
                {
                    public string Go() => Vec.Describe();
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Describe" },
            "public static string Describe()",
            "global::N.Holder.Describe()");
    }

    [Fact]
    public void StaticMethod_MemberTypeArgumentsExplicit_AreRetained()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static string Both<U>() => typeof(U).Name;
                    }
                }

                public class User
                {
                    public string Go() => Vec.Both<string>();
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Both" },
            "public static string Both<U>()",
            "global::N.Holder.Both<string>()");
    }

    [Fact]
    public void StaticMethod_GenericReceiver_OmittedArgsGetExplicitBlockArgs()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension<T>(T r)
                    {
                        public static string Describe() => typeof(T).Name;
                    }
                }

                public class Box<T> { }

                public class User
                {
                    public string Go() => Box<int>.Describe();
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Describe" },
            "public static string Describe<T>()",
            "global::N.Holder.Describe<global::N.Box<int>>()");
    }

    [Fact]
    public void StaticMethod_TwoLevel_FullNAndMArgs_AreRetained()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension<T>(T r) where T : struct
                    {
                        public static string Both<U>(U u) where U : class
                            => typeof(T).Name + typeof(U).Name;
                    }
                }

                public struct StructBox<T> { }

                public class User
                {
                    public string Go() => StructBox<int>.Both<StructBox<int>, string>("x");
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Both" },
            "public static string Both<T, U>(U u) where T : struct where U : class",
            "global::N.Holder.Both<StructBox<int>, string>(\"x\")");
    }

    // ── EMD-STATIC: static properties ──────────────────────────────────────

    [Fact]
    public void StaticProperty_ReadWrite_ViaReceiverType()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static int Marker { get => 0; set { } }
                    }
                }

                public class User
                {
                    public int Go()
                    {
                        int a = Vec.Marker;
                        Vec.Marker = a + 1;
                        return a;
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "get_Marker", "set_Marker" },
            "public static int get_Marker()",
            "public static void set_Marker(int value)",
            "global::N.Holder.get_Marker()",
            "global::N.Holder.set_Marker(a + 1)");
    }

    [Fact]
    public void StaticProperty_GenericReceiver_ExplicitBlockArgs()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension<T>(T r)
                    {
                        public static int Marker { get => 0; set { } }
                    }
                }

                public class Box<T> { }

                public class User
                {
                    public int Go()
                    {
                        int a = Box<int>.Marker;
                        Box<int>.Marker = 7;
                        return a;
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "get_Marker", "set_Marker" },
            "public static int get_Marker<T>()",
            "global::N.Holder.get_Marker<global::N.Box<int>>()",
            "global::N.Holder.set_Marker<global::N.Box<int>>(7)");
    }

    [Fact]
    public void StaticProperty_CompoundAndIncDec_SugarRetainedWithDiagnostic()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static int Marker { get => 0; set { } }
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        Vec.Marker += 2;
                        Vec.Marker++;
                    }
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        // Block retained entirely (two deferred compound sites).
        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ExtensionSeverity.Error);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == ExtensionSeverity.Warning
                 && d.Message.Contains("static property", StringComparison.OrdinalIgnoreCase));

        var compact = Compact(result.OutputCode);
        Assert.Contains(Compact("Vec.Marker += 2"), compact);
        Assert.Contains(Compact("Vec.Marker++"), compact);
    }

    // ── nameof ─────────────────────────────────────────────────────────────

    [Fact]
    public void NameOf_IsNotPlannedOrRewritten()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static string Describe() => "v";
                    }
                }

                public class User
                {
                    public string Go() => nameof(Vec);
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        Assert.True(result.Changed);
        Assert.Contains(Compact("=> nameof(Vec)"), Compact(result.OutputCode));

        var verifyCompilation = Recompile(result.OutputCode);
        var verifyTree = verifyCompilation.SyntaxTrees.Single();
        AssertHostCalls(verifyCompilation, verifyTree, "N.Holder", new HashSet<string>());
        AssertSecondRunStable(result.OutputCode);
    }

    // ── EMD-REF: receiver ref kinds ────────────────────────────────────────

    [Fact]
    public void Receiver_RefMutableStruct_CallGetsRefKeyword()
    {
        const string source = """
            namespace N
            {
                public struct Mut { public int X; }

                public static class Holder
                {
                    extension(ref Mut m)
                    {
                        public void Bump() { m.X++; }
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var m = new Mut();
                        m.Bump();
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Bump" },
            "public static void Bump(this ref global::N.Mut m)",
            "global::N.Holder.Bump(ref m)");
    }

    [Fact]
    public void Receiver_InStruct_CallGetsInKeyword()
    {
        const string source = """
            namespace N
            {
                public struct Mut { public int X; }

                public static class Holder
                {
                    extension(in Mut m)
                    {
                        public int Read() => m.X;
                    }
                }

                public class User
                {
                    public int Go()
                    {
                        var m = new Mut();
                        return m.Read();
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Read" },
            "public static int Read(this in global::N.Mut m)",
            "global::N.Holder.Read(in m)");
    }

    [Fact]
    public void Receiver_RefReadonly_CallGetsInKeyword()
    {
        const string source = """
            namespace N
            {
                public struct Mut { public int X; }

                public static class Holder
                {
                    extension(ref readonly Mut m)
                    {
                        public int Read() => m.X;
                    }
                }

                public class User
                {
                    public int Go()
                    {
                        var m = new Mut();
                        return m.Read();
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Read" },
            "public static int Read(this ref readonly global::N.Mut m)",
            "global::N.Holder.Read(in m)");
    }

    [Fact]
    public void Receiver_ScopedRef_CallGetsRefKeyword()
    {
        const string source = """
            namespace N
            {
                public struct Mut { public int X; }

                public static class Holder
                {
                    extension(scoped ref Mut m)
                    {
                        public void Bump() { m.X++; }
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var m = new Mut();
                        m.Bump();
                    }
                }
            }
            """;

        AssertPositiveCase(
            source,
            "N.Holder",
            new HashSet<string> { "Bump" },
            "public static void Bump(this scoped ref global::N.Mut m)",
            "global::N.Holder.Bump(ref m)");
    }

    [Fact]
    public void IllegalReceiver_RefOnReferenceType_IsRejectedByRoslyn()
    {
        const string source = """
            namespace N
            {
                public class Box { }

                public static class Holder
                {
                    extension(ref Box b)
                    {
                        public void M() { }
                    }
                }
            }
            """;

        var (compilation, _, _) = CompileOne(source);
        Assert.Contains(compilation.GetDiagnostics(), d => d.Id == "CS9300");
    }

    [Fact]
    public void IllegalReceiver_RefOnGenericParameter_IsRejectedByRoslyn()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension<T>(ref T v)
                    {
                        public void M() { }
                    }
                }
            }
            """;

        var (compilation, _, _) = CompileOne(source);
        Assert.Contains(compilation.GetDiagnostics(),
            d => d.Severity == DiagnosticSeverity.Error);
    }

    // ── Pointer receiver: CS2J1211 ─────────────────────────────────────────

    [Fact]
    public void PointerReceiver_BlockRejectedWithCS2J1211_FileUnchanged()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int* p)
                    {
                        public void M() { }
                    }
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == ExtensionSeverity.Error
                 && d.Code == ExtensionBlockCodes.UnsupportedReceiver);
    }

    // ── CS2J1208 gate ─────────────────────────────────────────────────────

    [Fact]
    public void Gate_OperatorWithoutMemberDesugarChain_CS2J1208()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static Vec operator +(Vec a, Vec b)
                            => new Vec { X = a.X + b.X };
                    }
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model, chainAvailable: false);

        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == ExtensionSeverity.Error
                 && d.Code == ExtensionBlockCodes.PropertyChainGate);
    }

    [Fact]
    public void Gate_PolicyDisabled_DoesNotGate()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static Vec operator +(Vec a, Vec b)
                            => new Vec { X = a.X + b.X };
                    }
                }

                public class User
                {
                    public void Go()
                    {
                        var a = new Vec();
                        var b = new Vec();
                        Vec c = a + b;
                    }
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model,
            new ExtensionBlockOptions
            {
                MaterializeExtensionBlocks = true,
                RequireMemberDesugarChain = false,
            },
            chainAvailable: false);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == ExtensionBlockCodes.PropertyChainGate);
        Recompile(result.OutputCode);
    }

    [Fact]
    public void Gate_OnlyStaticMembersEnabled_DoesNotGate()
    {
        const string source = """
            namespace N
            {
                public struct Vec { public int X; }

                public static class Holder
                {
                    extension(Vec r)
                    {
                        public static string Describe() => "v";
                    }
                }

                public class User
                {
                    public string Go() => Vec.Describe();
                }
            }
            """;

        var (compilation, model, _) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model,
            new ExtensionBlockOptions
            {
                MaterializeExtensionBlocks = true,
                EnableOperators = false,
                EnableProperties = false,
            },
            chainAvailable: false);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Code == ExtensionBlockCodes.PropertyChainGate);
        Recompile(result.OutputCode);
    }

    // ── Subrule disable ────────────────────────────────────────────────────

    [Fact]
    public void SubruleDisabled_MethodMemberRejected_BlockSkipped()
    {
        const string source = """
            namespace N
            {
                public static class Holder
                {
                    extension(int v)
                    {
                        public void M() { }
                    }
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model,
            new ExtensionBlockOptions
            {
                MaterializeExtensionBlocks = true,
                EnableMethods = false,
            });

        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == ExtensionSeverity.Error
                 && d.Code == ExtensionBlockCodes.Unsupported);
    }

    // ── Indexer member: CS2J1209 ──────────────────────────────────────────

    [Fact]
    public void IndexerMember_RejectedWithCS2J1209_BlockRetained()
    {
        const string source = """
            namespace N
            {
                public sealed class Box { public int V; }

                public static class Holder
                {
                    extension(Box box)
                    {
                        public int this[int index] => box.V + index;
                    }
                }
            }
            """;

        var (compilation, model, tree) = CompileOne(source);
        var result = RunBlockRewrite(compilation, model);

        // CS2J1209: indexer members are out of scope; the whole block is left
        // as valid extension sugar (nothing is half-materialized).
        Assert.False(result.Changed);
        Assert.Equal(tree.GetCompilationUnitRoot().ToFullString(), result.OutputCode);
        Assert.Contains(result.Diagnostics,
            d => d.Severity == ExtensionSeverity.Error
                 && d.Code == ExtensionBlockCodes.Unsupported);
    }
}
