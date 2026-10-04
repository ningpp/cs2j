using CSharpToJava.Core.AnonymousTypeLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.ProjectTests;

public class AnonymousTypeLiftUnitTests
{
    private readonly ITestOutputHelper _output;

    public AnonymousTypeLiftUnitTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Latest);

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private sealed class Session
    {
        public required AnonymousTypeLiftPreprocessor Pp { get; init; }
        public required AnonymousTypeLiftStatistics Stats { get; init; }
        public required List<AnonymousTypeLiftDiagnostic> Diags { get; init; }
        public required IReadOnlyList<string> Outputs { get; init; }
        public required IReadOnlyList<bool> Changed { get; init; }
        public string? GeneratedFile { get; set; }
    }

    private static Session RunSession(params string[] sources)
        => RunSessionNamed(sources.Select((s, i) => ($"file{i}.cs", s)).ToArray());

    /// <summary>
    /// 多棵 tree 加同一 CSharpCompilation、共享一个 preprocessor；逐 tree RewriteFile，
    /// 最后一次 GenerateTypeFile。
    /// </summary>
    private static Session RunSessionNamed(params (string File, string Source)[] files)
    {
        var trees = files
            .Select(f => (SyntaxTree)CSharpSyntaxTree.ParseText(f.Source, ParseOptions, path: f.File))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "anon-test",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new AnonymousTypeLiftPreprocessor();
        var stats = new AnonymousTypeLiftStatistics();
        var diagnostics = new List<AnonymousTypeLiftDiagnostic>();
        var outputs = new List<string>();
        var changed = new List<bool>();

        for (var i = 0; i < trees.Length; i++)
        {
            var model = compilation.GetSemanticModel(trees[i]);
            var result = preprocessor.RewriteFile(files[i].Source, model, stats, diagnostics);
            outputs.Add(result.OutputCode);
            changed.Add(result.Changed);
        }

        return new Session
        {
            Pp = preprocessor,
            Stats = stats,
            Diags = diagnostics,
            Outputs = outputs,
            Changed = changed,
            GeneratedFile = preprocessor.GenerateTypeFile(),
        };
    }

    /// <summary>全部改写输出 + 生成文件必须能联合编译，零 error。</summary>
    private static void AssertSessionCompiles(Session session)
    {
        var trees = session.Outputs
            .Select(o => (SyntaxTree)CSharpSyntaxTree.ParseText(o, ParseOptions))
            .ToList();
        if (session.GeneratedFile != null)
        {
            trees.Add(CSharpSyntaxTree.ParseText(session.GeneratedFile, ParseOptions));
        }

        var compilation = CSharpCompilation.Create(
            "anon-verify",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        if (errors.Count != 0)
        {
            foreach (var output in session.Outputs)
            {
                Console.WriteLine("--- output ---");
                Console.WriteLine(output);
            }

            Console.WriteLine("--- generated ---");
            Console.WriteLine(session.GeneratedFile);
        }

        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>编译给定源码集合，返回全部 error 诊断（负例断言用）。</summary>
    private static List<Diagnostic> CompileErrors(params string[] sources)
    {
        var trees = sources
            .Select(s => (SyntaxTree)CSharpSyntaxTree.ParseText(s, ParseOptions))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "anon-verify",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    // ------------------------------------------------------------------
    // 1. clean 文件
    // ------------------------------------------------------------------

    [Fact]
    public void CleanFile_NoAnonymousCreation_Unchanged()
    {
        var src = "public class C { public int Value; public int Add(int a, int b) => a + b; }";
        var session = RunSession(src);
        Assert.Equal(src, session.Outputs[0]);
        Assert.False(session.Changed[0]);
        Assert.Null(session.GeneratedFile);
        Assert.Equal(1, session.Stats.FilesScanned);
        Assert.Equal(1, session.Stats.FilesSkippedClean);
        Assert.Equal(0, session.Stats.ShapesRegistered);
        Assert.Equal(0, session.Stats.CreationsRewritten);
    }

    // ------------------------------------------------------------------
    // 2. 基本形状
    // ------------------------------------------------------------------

    [Fact]
    public void BasicShape_RewrittenAndGeneratedClassCompiles()
    {
        var src = """
            class C
            {
                object M()
                {
                    var p = new { X = 1, Y = "a" };
                    return p;
                }
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.Outputs[0]);
        _output.WriteLine(session.GeneratedFile);
        AssertSessionCompiles(session);

        Assert.Contains("global::Cs2jAnonymous.Anon_X_Int32_Y_String(1, \"a\")", session.Outputs[0]);
        Assert.Contains("internal sealed class Anon_X_Int32_Y_String", session.GeneratedFile);
        Assert.Contains("public Anon_X_Int32_Y_String(", session.GeneratedFile);
        Assert.Contains("public global::System.Int32 X { get; }", session.GeneratedFile);
        Assert.Contains("public global::System.String Y { get; }", session.GeneratedFile);
        Assert.Equal(1, session.Stats.CreationsRewritten);
        Assert.Equal(0, session.Stats.ResidualCreations);
    }

    // ------------------------------------------------------------------
    // 3. 同形状跨方法/跨类/跨文件去重
    // ------------------------------------------------------------------

    [Fact]
    public void SameShape_AcrossMethodsClassesAndFiles_Deduplicated()
    {
        var file1 = """
            class C1
            {
                object A() => new { X = 1, Y = "a" };
                object B() { return new { X = 2, Y = "b" }; }
            }

            class C2
            {
                object C() => new { X = 3, Y = "c" };
            }
            """;
        var file2 = """
            class C3
            {
                object D() => new { X = 4, Y = "d" };
            }
            """;
        var session = RunSessionNamed(("one.cs", file1), ("two.cs", file2));
        AssertSessionCompiles(session);

        Assert.Equal(1, CountOccurrences(
            session.GeneratedFile!, "internal sealed class Anon_X_Int32_Y_String"));
        Assert.Equal(1, session.Stats.ShapesRegistered);
        Assert.Equal(4, session.Stats.CreationsRewritten);

        var byFile = session.Pp.ShapesByFile;
        Assert.True(byFile.ContainsKey("one.cs"));
        Assert.True(byFile.ContainsKey("two.cs"));
        Assert.Contains("(X:global::System.Int32;Y:global::System.String)",
            byFile["two.cs"].Single());
    }

    // ------------------------------------------------------------------
    // 4. 顺序/大小写/类型/dynamic 差异
    // ------------------------------------------------------------------

    [Fact]
    public void ShapeDistinguishes_Order_Case_StringObject_DynamicObject()
    {
        var src = """
            class C
            {
                object A() => new { X = 1, Y = 2 };
                object B() => new { Y = 2, X = 1 };
                object Cc() => new { X = 1, x = 2 };
                object D() => new { X = "a" };
                object E() => new { X = (object)"a" };
                object F() => new { X = (dynamic)1 };
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.GeneratedFile);
        AssertSessionCompiles(session);

        Assert.Contains("Anon_X_Int32_Y_Int32", session.Outputs[0]);
        Assert.Contains("Anon_Y_Int32_X_Int32", session.Outputs[0]);
        Assert.Contains("Anon_X_Int32_x_Int32", session.Outputs[0]);
        Assert.Contains("Anon_X_String", session.Outputs[0]);
        Assert.Contains("Anon_X_Object", session.Outputs[0]);
        Assert.Contains("Anon_X_Dynamic", session.Outputs[0]);

        var generated = session.GeneratedFile!;
        Assert.Contains("internal sealed class Anon_X_Int32_Y_Int32", generated);
        Assert.Contains("internal sealed class Anon_Y_Int32_X_Int32", generated);
        Assert.Contains("internal sealed class Anon_X_Int32_x_Int32", generated);
        Assert.Contains("internal sealed class Anon_X_String", generated);
        Assert.Contains("internal sealed class Anon_X_Object", generated);
        Assert.Contains("internal sealed class Anon_X_Dynamic", generated);
        // dynamic 属性必须保留 dynamic 声明，且与 object 是不同形状（D2/D9）。
        Assert.Contains("dynamic X { get; }", generated);
        var dynamicClassBlock = generated[generated.IndexOf("internal sealed class Anon_X_Dynamic", StringComparison.Ordinal)..];
        dynamicClassBlock = dynamicClassBlock[..dynamicClassBlock.IndexOf("    }", StringComparison.Ordinal)];
        Assert.DoesNotContain("global::System.Object X", dynamicClassBlock);
        Assert.Equal(1, session.Stats.DynamicShapes);
    }

    // ------------------------------------------------------------------
    // 5. 跨运行/输入顺序打乱，生成文件字节级稳定
    // ------------------------------------------------------------------

    [Fact]
    public void GeneratedFile_ByteStable_AcrossRunsAndInputOrder()
    {
        var a = "class A { object M() => new { X = 1 }; }";
        var b = "class B { object M() => new { Y = \"b\" }; }";
        var c = "class C { object M() => new { Z = 1L }; }";

        var run1 = RunSessionNamed(("a.cs", a), ("b.cs", b), ("c.cs", c));
        var run2 = RunSessionNamed(("c.cs", c), ("a.cs", a), ("b.cs", b));
        var run3 = RunSessionNamed(("b.cs", b), ("c.cs", c), ("a.cs", a));

        Assert.NotNull(run1.GeneratedFile);
        Assert.Equal(run1.GeneratedFile, run2.GeneratedFile);
        Assert.Equal(run1.GeneratedFile, run3.GeneratedFile);
    }

    // ------------------------------------------------------------------
    // 6. 嵌套投影
    // ------------------------------------------------------------------

    [Fact]
    public void NestedProjection_InnerRegisteredFirst_OuterPropertyTypedAsInner()
    {
        var src = """
            class C
            {
                object M()
                {
                    var p = new { Outer = new { K = 1 } };
                    return p;
                }
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.Outputs[0]);
        _output.WriteLine(session.GeneratedFile);
        AssertSessionCompiles(session);

        // 内层先注册（注册序：嵌套先于外层）。
        Assert.Equal("Anon_K_Int32", session.Pp.TypeNames.First());
        Assert.Contains("new global::Cs2jAnonymous.Anon_K_Int32(1)", session.Outputs[0]);
        Assert.Contains("Anon_Outer_Anon_K_Int32(", session.Outputs[0]);
        Assert.Contains("internal sealed class Anon_K_Int32", session.GeneratedFile);
        Assert.Contains("internal sealed class Anon_Outer_Anon_K_Int32", session.GeneratedFile);
        // 外层类属性类型为内层类实例化（同命名空间兄弟类型，简单名即可）。
        Assert.Contains("public Anon_K_Int32 Outer { get; }", session.GeneratedFile);
        Assert.Equal(2, session.Stats.ShapesRegistered);
        Assert.Equal(1, session.Stats.NestedShapes);
    }

    // ------------------------------------------------------------------
    // 7. 隐式数组 / List 初始化 / 三元同形合并
    // ------------------------------------------------------------------

    [Fact]
    public void ImplicitArray_ListInitializer_TernaryMerge_Compile()
    {
        var src = """
            using System.Collections.Generic;
            using System.Linq;

            class C
            {
                int M(bool cond)
                {
                    var arr = new[] { new { X = 1 }, new { X = 2 } };
                    var list = new[] { new { X = 3 } }.ToList();
                    var merged = cond ? new { X = 4 } : new { X = 5 };
                    return arr[0].X + arr[1].X + list[0].X + merged.X;
                }
            }
            """;
        var session = RunSession(src);
        AssertSessionCompiles(session);
        Assert.Equal(5, session.Stats.CreationsRewritten);
        Assert.Equal(0, session.Stats.ResidualCreations);
        // 隐式数组语法原样保留，元素类型重编译时推断为生成类（联合编译零 error 即证明）。
        Assert.Contains("new[]", session.Outputs[0]);
        Assert.Contains("Anon_X_Int32(1)", session.Outputs[0]);
        Assert.Contains("Anon_X_Int32(2)", session.Outputs[0]);
    }

    // ------------------------------------------------------------------
    // 8. 泛型方法/泛型类投影
    // ------------------------------------------------------------------

    [Fact]
    public void GenericProjection_GenericClassAndMethod_SingleShape()
    {
        var src = """
            class C<T>
            {
                public object Project(T v) => new { V = v };
            }

            static class D
            {
                public static object M<U>(U v) => new { V = v };
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.GeneratedFile);
        AssertSessionCompiles(session);

        Assert.Contains("internal sealed class Anon_V_T0<__P0>", session.GeneratedFile);
        Assert.DoesNotContain("where __P", session.GeneratedFile);
        Assert.Contains("Anon_V_T0<T>", session.Outputs[0]);
        Assert.Contains("Anon_V_T0<U>", session.Outputs[0]);
        Assert.Equal(1, session.Stats.ShapesRegistered);
        Assert.Equal(1, session.Stats.GenericShapes);
        Assert.Equal(2, session.Stats.CreationsRewritten);
    }

    // ------------------------------------------------------------------
    // 9. 约束拷贝 + 同结构不同约束必须分成两个类
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("new()", "where __P0 : new()")]
    [InlineData("class", "where __P0 : class")]
    [InlineData("struct", "where __P0 : struct")]
    [InlineData("System.IComparable<T>", "where __P0 : global::System.IComparable<__P0>")]
    public void GenericConstraints_CopiedToGeneratedClass(string constraint, string expectedClause)
    {
        var src = $$"""
            class C
            {
                object M<T>(T v) where T : {{constraint}}
                {
                    return new { V = v };
                }
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.GeneratedFile);
        AssertSessionCompiles(session);
        Assert.Contains(expectedClause, session.GeneratedFile);
        Assert.Contains("Anon_V_T0<T>", session.Outputs[0]);
    }

    [Fact]
    public void SameStructure_DifferentConstraints_TwoClasses_BothSitesCompile()
    {
        var src = """
            class C
            {
                object A<T>(T v) where T : class => new { V = v };
                object B<U>(U v) where U : struct => new { V = v };
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.Outputs[0]);
        _output.WriteLine(session.GeneratedFile);
        AssertSessionCompiles(session);

        Assert.Equal(2, session.Stats.ShapesRegistered);
        Assert.Contains("internal sealed class Anon_V_T0<__P0>", session.GeneratedFile);
        Assert.Contains("internal sealed class Anon_V_T0_2<__P0>", session.GeneratedFile);
        Assert.Contains("where __P0 : class", session.GeneratedFile);
        Assert.Contains("where __P0 : struct", session.GeneratedFile);
        Assert.Contains("Anon_V_T0<T>", session.Outputs[0]);
        Assert.Contains("Anon_V_T0_2<U>", session.Outputs[0]);
    }

    // ------------------------------------------------------------------
    // 10. 投影缩写
    // ------------------------------------------------------------------

    [Fact]
    public void ProjectionShorthand_KeepsInferredPropertyName()
    {
        var src = """
            class C
            {
                public int X;

                int M(C c)
                {
                    var p = new { c.X };
                    return p.X;
                }
            }
            """;
        var session = RunSession(src);
        AssertSessionCompiles(session);
        Assert.Contains("global::Cs2jAnonymous.Anon_X_Int32(c.X)", session.Outputs[0]);
        Assert.Contains("public global::System.Int32 X { get; }", session.GeneratedFile);
        Assert.Equal(1, session.Stats.CreationsRewritten);
    }

    // ------------------------------------------------------------------
    // 11. 极端长属性名：截断 + _2 消歧
    // ------------------------------------------------------------------

    [Fact]
    public void VeryLongPropertyNames_TruncatedAndNumbered()
    {
        var longName = new string('A', 500);
        var file1 = $"class C1 {{ object M() {{ return new {{ {longName} = 1 }}; }} }}";
        var file2 = $"class C2 {{ object M() {{ return new {{ {longName} = 1, Z = 2 }}; }} }}";
        var session = RunSessionNamed(("a.cs", file1), ("b.cs", file2));
        _output.WriteLine(session.GeneratedFile?.Substring(0, Math.Min(2000, session.GeneratedFile!.Length)));
        AssertSessionCompiles(session);

        var baseName = ("Anon_" + longName + "_Int32")[..480];
        Assert.Equal(baseName, session.Pp.TypeNames.First());
        Assert.Contains(baseName + "_2", session.Pp.TypeNames);
        Assert.Contains("internal sealed class " + baseName, session.GeneratedFile);
        Assert.Contains("internal sealed class " + baseName + "_2", session.GeneratedFile);
        Assert.Contains("global::Cs2jAnonymous." + baseName + "(1)", session.Outputs[0]);
        Assert.Contains("global::Cs2jAnonymous." + baseName + "_2(1, 2)", session.Outputs[1]);
    }

    // ------------------------------------------------------------------
    // 12. 幂等：输出再跑零改写、零生成
    // ------------------------------------------------------------------

    [Fact]
    public void Idempotent_SecondRunCleanAndNoTypeFile()
    {
        var src = """
            class C
            {
                object M() => new { X = 1, Y = "a" };
            }
            """;
        var first = RunSession(src);
        AssertSessionCompiles(first);
        var rewritten = first.Outputs[0];

        // 第二次运行的编译单元包含改写输出与首跑生成文件，语义模型完整。
        var outputTree = CSharpSyntaxTree.ParseText(rewritten, ParseOptions, path: "rewritten.cs");
        var generatedTree = CSharpSyntaxTree.ParseText(first.GeneratedFile!, ParseOptions, path: "gen.cs");
        var compilation = CSharpCompilation.Create(
            "anon-idempotent",
            new SyntaxTree[] { outputTree, generatedTree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var second = new AnonymousTypeLiftPreprocessor();
        var stats = new AnonymousTypeLiftStatistics();
        var diagnostics = new List<AnonymousTypeLiftDiagnostic>();
        var result = second.RewriteFile(
            rewritten, compilation.GetSemanticModel(outputTree), stats, diagnostics);

        Assert.False(result.Changed);
        Assert.Equal(rewritten, result.OutputCode);
        Assert.Null(second.GenerateTypeFile());
        Assert.Equal(1, stats.FilesScanned);
        Assert.Equal(1, stats.FilesSkippedClean);
        Assert.Equal(0, stats.CreationsRewritten);
    }

    // ------------------------------------------------------------------
    // 13A. 语义成员文本：Equals / GetHashCode / ToString（D5/D6/D7）
    // ------------------------------------------------------------------

    [Fact]
    public void SemanticMembers_BasicShape_ExactTextAndCompiles()
    {
        var src = """
            class C
            {
                object M()
                {
                    var p = new { X = 1, Y = "a" };
                    return p;
                }
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.GeneratedFile);
        AssertSessionCompiles(session);
        var gen = session.GeneratedFile!;

        // Equals：全限定自引用 is 模式 + 逐属性 EqualityComparer，按声明顺序。
        Assert.Contains("obj is global::Cs2jAnonymous.Anon_X_Int32_Y_String other", gen);
        Assert.Contains("EqualityComparer<global::System.Int32>.Default.Equals(X, other.X)", gen);
        Assert.Contains("EqualityComparer<global::System.String>.Default.Equals(Y, other.Y)", gen);
        Assert.Contains("public override global::System.Boolean Equals(global::System.Object obj)", gen);

        // GetHashCode：种子 17、*31 累加。
        Assert.Contains("public override global::System.Int32 GetHashCode()", gen);
        Assert.Contains("global::System.Int32 hash = 17;", gen);
        Assert.Contains("hash = hash * 31 +", gen);
        Assert.Contains("EqualityComparer<global::System.Int32>.Default.GetHashCode(X)", gen);
        Assert.Contains("EqualityComparer<global::System.String>.Default.GetHashCode(Y)", gen);

        // ToString：String.Concat(params object?[]) 精确 token 序列。
        Assert.Contains("public override global::System.String ToString()", gen);
        Assert.Contains(
            "global::System.String.Concat(\"{ X = \", X, \", Y = \", Y, \" }\")", gen);

        // D1 红线：绝不生成 IEquatable/运算符/record/with。
        Assert.DoesNotContain("IEquatable", gen);
        Assert.DoesNotContain("public static", gen);
        Assert.DoesNotContain(" != ", gen);
        Assert.DoesNotContain("record", gen);
        Assert.DoesNotContain("with", gen);
    }

    [Fact]
    public void SemanticMembers_ZeroPropertyShape_ExactTextAndCompiles()
    {
        var src = "class C { object M() => new { }; }";
        var session = RunSession(src);
        _output.WriteLine(session.GeneratedFile);
        AssertSessionCompiles(session);
        var gen = session.GeneratedFile!;

        // 零属性形状：Equals 仅精确类型检查（if 语句形式，不同形状永不等）；
        // hash 常量 17；"{ }"。
        Assert.Contains("if (!(obj is global::Cs2jAnonymous.Anon))", gen);
        Assert.Contains("return false;", gen);
        Assert.Contains("return true;", gen);
        Assert.Contains("return 17;", gen);
        Assert.Contains("return \"{ }\";", gen);
    }

    // ------------------------------------------------------------------
    // 13B. 行为可编译性：nameof 不动 / 属性 get-only 负例 / 嵌套可变正例
    // ------------------------------------------------------------------

    [Fact]
    public void NameofSite_LeftUntouched_AndCompiles()
    {
        var src = """
            class C
            {
                string M(C p)
                {
                    var n = p == null ? (int?)null : p.X;
                    return nameof(p.X) + n;
                }

                public int X;
            }
            """;
        var session = RunSession(src);
        AssertSessionCompiles(session);
        // nameof(p.X) 不是创建点，原样保留。
        Assert.Contains("nameof(p.X)", session.Outputs[0]);
    }

    [Fact]
    public void GeneratedProperty_IsGetOnly_AssignmentFailsToCompile()
    {
        var src = "class C { object M() => new { X = 1, Y = \"a\" }; }";
        var session = RunSession(src);
        var useSite = """
            class UseSite
            {
                static void N()
                {
                    var p = new global::Cs2jAnonymous.Anon_X_Int32_Y_String(1, "a");
                    p.X = 1;
                }
            }
            """;
        var errors = CompileErrors(session.Outputs[0], session.GeneratedFile!, useSite);
        // CS0200: 属性是只读的；CS1023 也是 get-only 赋值的可能码位之一。
        Assert.Contains(errors, d => d.Id is "CS0200" or "CS1023");
    }

    [Fact]
    public void NestedMutableMember_RemainsMutable_Compiles()
    {
        var src = """
            class C
            {
                void M()
                {
                    var q = new { L = new global::System.Collections.Generic.List<int>() };
                    q.L.Add(1);
                }
            }
            """;
        var session = RunSession(src);
        AssertSessionCompiles(session);
        Assert.Contains(".L.Add(1)", session.Outputs[0]);
    }

    // ------------------------------------------------------------------
    // 13C. 表达式树内创建点跳过（AL020 + 语义残余 + AL060）
    // ------------------------------------------------------------------

    [Fact]
    public void ExpressionTreeCreation_Skipped_AL020_Residual_AL060_OtherSiteRewritten()
    {
        var src = """
            class C
            {
                void M()
                {
                    global::System.Linq.Expressions.Expression<global::System.Func<int, object>> e = x => new { V = x };
                    var z = new { Z = 1 };
                }
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.Outputs[0]);

        // 表达式树创建点原样保留；同文件普通站点正常改写。
        Assert.Contains("new { V = x }", session.Outputs[0]);
        Assert.DoesNotContain("Anon_V_", session.Outputs[0]);
        Assert.Contains("new global::Cs2jAnonymous.Anon_Z_Int32(1)", session.Outputs[0]);

        Assert.Equal(1, session.Stats.CreationsRewritten);
        Assert.Equal(1, session.Stats.ResidualCreations);

        var al020 = session.Diags.Where(d => d.Code == "AL020").ToList();
        Assert.Single(al020);
        Assert.Equal(AnonymousTypeLiftSeverity.Warning, al020[0].Severity);
        Assert.Contains(session.Diags, d => d.Code == "AL060" && d.Severity == AnonymousTypeLiftSeverity.Warning);

        // 保留的表达式树 lambda 本身合法，与生成文件联合编译零 error。
        AssertSessionCompiles(session);
    }

    // ------------------------------------------------------------------
    // 13D. AL030：dynamic 形状生命周期内只发一次
    // ------------------------------------------------------------------

    [Fact]
    public void DynamicShape_AL030_OncePerShapeAcrossSitesAndFiles()
    {
        var file1 = """
            class C1
            {
                object A() => new { D = (dynamic)1 };
                object B() => new { D = (dynamic)2 };
            }
            """;
        var file2 = "class C2 { object C() => new { D = (dynamic)3 }; }";
        var session = RunSessionNamed(("one.cs", file1), ("two.cs", file2));
        AssertSessionCompiles(session);

        var al030 = session.Diags.Where(d => d.Code == "AL030").ToList();
        Assert.Single(al030);
        Assert.Equal(AnonymousTypeLiftSeverity.Info, al030[0].Severity);
        Assert.Equal(1, session.Stats.DynamicShapes);
        Assert.Equal(3, session.Stats.CreationsRewritten);
    }

    // ------------------------------------------------------------------
    // 13E. AL040：GetType 身份反射 + Serialize/SerializeObject 盘点
    // ------------------------------------------------------------------

    [Fact]
    public void GetTypeAndSerialize_OnAnonymous_AL040_RewriteUnaffected()
    {
        var src = """
            class Ser
            {
                public static string SerializeObject(object value) => value?.ToString() ?? "";
            }

            class C
            {
                string M()
                {
                    var p = new { X = 1 };
                    var t = p.GetType().Name;
                    var s = Ser.SerializeObject(p);
                    return t + s;
                }
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.Outputs[0]);
        AssertSessionCompiles(session);

        var al040 = session.Diags
            .Where(d => d.Code == "AL040" && d.Severity == AnonymousTypeLiftSeverity.Warning)
            .ToList();
        Assert.Equal(2, al040.Count);

        // 只盘点不改写：创建点仍提升、零残余、联合编译零 error。
        Assert.Contains("new global::Cs2jAnonymous.Anon_X_Int32(1)", session.Outputs[0]);
        Assert.Equal(1, session.Stats.CreationsRewritten);
        Assert.Equal(0, session.Stats.ResidualCreations);
    }

    // ------------------------------------------------------------------
    // 13F. 查询语法：方法链 / 查询式 / let transparent identifier
    // ------------------------------------------------------------------

    [Fact]
    public void QuerySyntax_MethodChainAndQuery_LetTransparentIdentifier_NoResidual()
    {
        var src = """
            using System.Collections.Generic;
            using System.Linq;

            class C
            {
                IEnumerable<int> M(IEnumerable<int> xs)
                {
                    var a = xs.Select(x => new { X = x });
                    var b = from x in xs select new { X = x };
                    var c = from x in xs let y = x + 1 select new { A = x, B = y };
                    return xs;
                }
            }
            """;
        var session = RunSession(src);
        _output.WriteLine(session.Outputs[0]);
        AssertSessionCompiles(session);

        Assert.Equal(3, session.Stats.CreationsRewritten);
        // transparent identifier 无创建语法节点，豁免 residual 闸门（§7.4）。
        Assert.Equal(0, session.Stats.ResidualCreations);
        Assert.DoesNotContain(session.Diags, d => d.Code == "AL020");
        Assert.Contains("Anon_X_Int32(x)", session.Outputs[0]);
        // let 透明标识符 y 在源码里就是范围变量名，实参自然是 (x, y)。
        Assert.Contains("Anon_A_Int32_B_Int32(x, y)", session.Outputs[0]);
    }

    // ------------------------------------------------------------------
    // 13G. 语义残余隔离：表达式树站点不影响同文件普通形状生成
    // ------------------------------------------------------------------

    [Fact]
    public void ExpressionTreeResidual_NormalSiteIsolation_GeneratedFileHasNormalClassOnly()
    {
        var src = """
            class C
            {
                void M()
                {
                    global::System.Linq.Expressions.Expression<global::System.Func<int, object>> e = x => new { V = x };
                    var z = new { Z = 1 };
                }
            }
            """;
        var session = RunSession(src);
        AssertSessionCompiles(session);

        Assert.Contains("internal sealed class Anon_Z_Int32", session.GeneratedFile);
        // 跳过的表达式树形状从未注册，不进生成文件。
        Assert.DoesNotContain("Anon_V_", session.GeneratedFile);
    }

    // ------------------------------------------------------------------
    // 13H. AL001：每个被改写文件一条 Info（站点数 + 形状名集合）
    // ------------------------------------------------------------------

    [Fact]
    public void AL001_OneInfoPerRewrittenFile_WithSiteCountAndShapeNames()
    {
        var file1 = """
            class C1
            {
                object A() => new { X = 1 };
                object B() => new { X = 2 };
                object Cc() => new { Y = "a" };
            }
            """;
        var file2 = "class C2 { object D() => new { Z = 1L }; }";
        var session = RunSessionNamed(("one.cs", file1), ("two.cs", file2));
        AssertSessionCompiles(session);

        var al001 = session.Diags
            .Where(d => d.Code == "AL001" && d.Severity == AnonymousTypeLiftSeverity.Info)
            .ToList();
        Assert.Equal(2, al001.Count);

        Assert.Contains("3 anonymous object creation site(s)", al001[0].Message);
        Assert.Contains("Anon_X_Int32", al001[0].Message);
        Assert.Contains("Anon_Y_String", al001[0].Message);

        Assert.Contains("1 anonymous object creation site(s)", al001[1].Message);
        Assert.Contains("Anon_Z_Int64", al001[1].Message);
    }
}
