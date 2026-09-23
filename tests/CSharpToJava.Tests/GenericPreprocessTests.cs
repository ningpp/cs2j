using CSharpToJava.Core.GenericPreprocess;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// 单文件 C#→C# 转换单元测试，针对 GenericPreprocessor 核心重写器。
/// 参考 GotoEliminatorTests 的风格：给定 C# 片段，断言重写产物。
/// 测试覆盖从简单模式到贴近真实 SDK 场景的复杂嵌套泛型用法。
/// </summary>
public class GenericPreprocessTests(ITestOutputHelper output)
{
    // ===================================================================
    // 基础选项 / 诊断模型
    // ===================================================================

    [Fact]
    public void Options_Default_AllRewritesEnabled()
    {
        var o = new GenericPreprocessOptions();
        Assert.True(o.RewriteDefaultExpression);
        Assert.True(o.RewriteNewTypeParam);
        Assert.True(o.RewriteNewTypeParamArray);
        Assert.True(o.TrackTypeOfExpression);
        Assert.True(o.TrackIsAsTypeParam);
    }

    [Fact]
    public void Diagnostics_Record_Properties_RoundTrip()
    {
        var d = new GenericPreprocessDiagnostic(
            GenericPreprocessSeverity.Warning, GenericPattern.DefaultExpression, "msg", "F.cs", 3);
        Assert.Equal(GenericPreprocessSeverity.Warning, d.Severity);
        Assert.Equal(GenericPattern.DefaultExpression, d.Pattern);
        Assert.Equal("msg", d.Message);
        Assert.Equal("F.cs", d.FilePath);
        Assert.Equal(3, d.Line);
    }

    [Fact]
    public void Statistics_Starts_Zero()
    {
        var s = new GenericPreprocessStatistics();
        Assert.Equal(0, s.FilesTransformed);
        Assert.Equal(0, s.DefaultExpressionsRewritten);
    }

    // ===================================================================
    // 无泛型模式 → 不改写
    // ===================================================================

    [Fact]
    public void Preprocess_NoGenericPatterns_ReturnsUnchanged()
    {
        var src = "class C { public int M() => 1; }";
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(0, result.Statistics.DefaultExpressionsRewritten);
    }

    // ===================================================================
    // default(T) — 基础场景
    // ===================================================================

    [Fact]
    public void Preprocess_DefaultTTypeParam_RewrittenToDefaultLiteral()
    {
        var src = """
        class C<T>
        {
            public T M() => default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.DefaultExpressionsRewritten);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("default", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultInt_NotRewritten()
    {
        var src = "class C { public int M() => default(int); }";
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.DefaultExpressionsRewritten);
    }

    [Fact]
    public void Preprocess_DefaultTInGenericMethod_Rewritten()
    {
        var src = """
        class C
        {
            public T M<T>() => default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.DefaultExpressionsRewritten);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultTInGenericMethodCallArg_NotRewritten()
    {
        // default(T) as an argument to a generic method that relies on type
        // inference (no explicit type args) must NOT be rewritten to bare
        // `default` — that would break type inference (CS0411).
        var src = """
        class C
        {
            public string M<T>(T x) => x?.ToString() ?? "";
            public string Call<T>() => M(default(T));
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(1, result.Statistics.DefaultExpressionsRewritten);
        Assert.Contains("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultTInGenericMethodCallWithExplicitTypeArg_Rewritten()
    {
        var src = """
        class C
        {
            public string M<T>(T x) => x?.ToString() ?? "";
            public string Call<T>() => M<T>(default(T));
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultTEqualityNull_NotRewritten()
    {
        var src = """
        class C<T> where T : class
        {
            public bool IsNull(T value) => default(T) == null;
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(1, result.Statistics.DefaultExpressionsRewritten);
        Assert.Contains("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // default(T) — 复杂场景
    // ===================================================================

    [Fact]
    public void Preprocess_DefaultT_AsFieldInitializer_Rewritten()
    {
        // default(T) as a field initializer — safe context (assignment).
        var src = """
        class C<T>
        {
            private T _field = default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultT_InEqualityComparerContext_NotRewritten()
    {
        // Real SDK pattern from JsonPropertyInfoOfT.cs:
        //   default(T) == null ? value == null : EqualityComparer<T>.Default.Equals(default, value)
        // The default(T) in the ternary condition is a == null comparison → NOT rewritten.
        // The `default` in Equals(default, value) is already the literal form → N/A.
        var src = """
        using System.Collections.Generic;
        class C<T>
        {
            public bool M(T value) =>
                default(T) == null ? value == null : EqualityComparer<T>.Default.Equals(value);
        }
        """;
        var result = Preprocess(src);
        // default(T) is counted, but NOT rewritten because it's in == null context.
        Assert.True(result.Statistics.DefaultExpressionsRewritten >= 1);
        Assert.Contains("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultT_AssignedToVar_Rewritten()
    {
        // var d = default(T); — safe context (assignment, target type unambiguous).
        var src = """
        class C<T>
        {
            public T M()
            {
                T d = default(T);
                return d;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultT_NotEqualsNull_NotRewritten()
    {
        // default(T) != null — also a comparison context, NOT rewritten.
        var src = """
        class C<T> where T : class
        {
            public bool M() => default(T) != null;
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Statistics.DefaultExpressionsRewritten >= 1);
        Assert.Contains("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // new T() — 基础场景
    // ===================================================================

    [Fact]
    public void Preprocess_NewT_RewrittenToActivatorCreateInstance()
    {
        var src = """
        class C<T> where T : new()
        {
            public T Make() => new T();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.NewTypeParamRewritten);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_NewSomeClass_NotRewritten()
    {
        var src = """
        class Foo { public Foo() {} }
        class C { public Foo M() => new Foo(); }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.NewTypeParamRewritten);
    }

    [Fact]
    public void Preprocess_NewTInGenericMethod_Rewritten()
    {
        var src = """
        class C
        {
            public T M<T>() where T : new() => new T();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.NewTypeParamRewritten);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // new T() — 复杂场景
    // ===================================================================

    [Fact]
    public void Preprocess_NewTWithConstructorArgs_NotRewritten()
    {
        // new T(42) has arguments — must NOT be rewritten because
        // Activator.CreateInstance<T>() only calls the parameterless ctor.
        var src = """
        class C<T> where T : new()
        {
            public T M(int n) => new T(n);
        }
        """;
        var result = Preprocess(src);
        // The new T(n) should not be rewritten (has arguments).
        Assert.DoesNotContain("Activator.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_NewTWithObjectInitializer_NotRewritten()
    {
        // new T { X = 1 } has an initializer — must NOT be rewritten.
        var src = """
        class C<T> where T : new()
        {
            public T M() => new T { X = 1 };
        }
        """;
        var result = Preprocess(src);
        Assert.DoesNotContain("Activator.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_MultipleNewT_AllRewritten()
    {
        var src = """
        class C<T> where T : new()
        {
            public T Make() => new T();
            public T MakeAgain() => new T();
            public T MakeThird() => new T();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(3, result.Statistics.NewTypeParamRewritten);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // new T[n] — 基础场景
    // ===================================================================

    [Fact]
    public void Preprocess_NewTArray_RewrittenToArrayCreateInstance()
    {
        var src = """
        class C<T>
        {
            public T[] M(int n)
            {
                var arr = new T[n];
                return arr;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.NewTypeParamArrayRewritten);
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("typeof(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_NewIntArray_NotRewritten()
    {
        var src = "class C { public int[] M(int n) => new int[n]; }";
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.NewTypeParamArrayRewritten);
    }

    /// <summary>
    /// Real-world regression (dotnet/runtime collections): the source project itself
    /// lives in <c>dotnet.System.Collections.Generic</c>. A bare <c>System.Array</c>
    /// / <c>System.Activator</c> name inside that namespace binds relatively to the
    /// nonexistent <c>dotnet.System.Array</c> (CS0234: 36 of the 44 preprocessor
    /// build errors). The injected calls MUST be <c>global::</c>-qualified and the
    /// rewritten code must actually COMPILE inside a shadowing namespace.
    /// </summary>
    [Fact]
    public void Preprocess_ShadowingNamespace_UsesGlobalAlias_OutputCompiles()
    {
        var src = """
        namespace dotnet.System.Collections.Generic
        {
            public class C<T>
            {
                public T[] MakeArray(int n) => new T[n];
                public T Make() => new T();
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        output.WriteLine(result.OutputCode);
        Assert.Contains("global::System.Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("global::System.Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);

        // Full semantic check against the BCL reference assemblies (using
        // typeof(object).Assembly together with ref assemblies causes CS0433/CS0518,
        // so only the ref-pack facades are referenced). Skip when no SDK ref pack is
        // installed on this machine.
        var refDir = GetLatestNetCoreRefDir();
        if (refDir is null)
        {
            output.WriteLine("Skipping semantic compile: Microsoft.NETCore.App.Ref pack not found.");
            return;
        }

        var rewrittenTree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var refs = Directory.GetFiles(refDir, "*.dll")
            .Select(f => MetadataReference.CreateFromFile(f))
            .ToArray();
        var compile = CSharpCompilation.Create("shadow-ns-verify",
            new[] { rewrittenTree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compile.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code should compile inside a namespace shadowing System. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }

    private static string? GetLatestNetCoreRefDir()
    {
        var packs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packs))
            return null;
        var latestPack = Directory.GetDirectories(packs)
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0))
            .FirstOrDefault();
        if (latestPack is null || !Directory.Exists(Path.Combine(latestPack, "ref")))
            return null;
        return Directory.GetDirectories(Path.Combine(latestPack, "ref"))
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0))
            .FirstOrDefault();
    }

    // ===================================================================
    // new T[n] — 复杂场景
    // ===================================================================

    [Fact]
    public void Preprocess_NewT2DArray_NotRewritten()
    {
        // new T[3, 4] is a 2D (rectangular) array — not handled by the
        // rewriter (single-dimensional only). Should NOT be rewritten.
        var src = """
        class C<T>
        {
            public T[,] M() => new T[3, 4];
        }
        """;
        var result = Preprocess(src);
        // Multi-dim arrays are not rewritten (current limitation).
        Assert.DoesNotContain("Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_NewTJaggedArray_NotRewritten()
    {
        // new T[n][] is a jagged array — element type is T[] (an array type,
        // not a type parameter). Should NOT be rewritten by the simple
        // type-parameter-element check.
        var src = """
        class C<T>
        {
            public T[][] M(int n) => new T[n][];
        }
        """;
        var result = Preprocess(src);
        // The element type is T[] (an array), not T itself — so IsTypeParameterElementType
        // should return false. The array creation should NOT be rewritten.
        Assert.DoesNotContain("Array.CreateInstance(typeof(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_NewTArrayWithInitializer_NotRewritten()
    {
        // new T[] { ... } has an initializer — must NOT be rewritten.
        var src = """
        class C<T> where T : new()
        {
            public T[] M() => new T[] { new T(), new T() };
        }
        """;
        var result = Preprocess(src);
        // The array creation has an initializer, so it should not be rewritten.
        // But the new T() inside the initializer should be rewritten.
        Assert.DoesNotContain("Array.CreateInstance(typeof(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // typeof(T) / is T / as T — tracking only
    // ===================================================================

    [Fact]
    public void Preprocess_TypeOfT_TrackedNotRewritten()
    {
        var src = """
        class C<T>
        {
            public System.Type M() => typeof(T);
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("typeof(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.Equal(1, result.Statistics.TypeOfExpressionsSeen);
    }

    [Fact]
    public void Preprocess_IsT_Tracked()
    {
        var src = """
        class C<T>
        {
            public bool M(object o) => o is T;
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(1, result.Statistics.IsTypeParamSeen);
    }

    [Fact]
    public void Preprocess_AsT_Tracked()
    {
        var src = """
        class C<T>
        {
            public object M(object o) => o as T;
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(1, result.Statistics.AsTypeParamSeen);
    }

    // ===================================================================
    // 多类型参数场景
    // ===================================================================

    [Fact]
    public void Preprocess_MultiTypeParam_AllRewritten()
    {
        // Generic class with two type parameters (T, K).
        var src = """
        class C<T, K>
            where T : new()
            where K : new()
        {
            public T MakeT() => new T();
            public K MakeK() => new K();
            public T GetDefault() => default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.NewTypeParamRewritten);
        Assert.True(result.Statistics.DefaultExpressionsRewritten >= 1);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_MultiTypeParamArray_BothRewritten()
    {
        var src = """
        class C<T, K>
        {
            public T[] MakeTArray(int n) => new T[n];
            public K[] MakeKArray(int n) => new K[n];
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.NewTypeParamArrayRewritten);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 混合多模式场景
    // ===================================================================

    [Fact]
    public void Preprocess_MultiplePatterns_AllRewritten()
    {
        var src = """
        class C<T> where T : new()
        {
            public T M(int n)
            {
                var arr = new T[n];
                var v = new T();
                var d = default(T);
                return v;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.NewTypeParamArrayRewritten);
        Assert.Equal(1, result.Statistics.NewTypeParamRewritten);
        Assert.Equal(1, result.Statistics.DefaultExpressionsRewritten);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_TypeOfAndIsAndAs_AllTracked()
    {
        var src = """
        class C<T>
        {
            public System.Type GetType_() => typeof(T);
            public bool CheckIs(object o) => o is T;
            public object CastAs(object o) => o as T;
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(1, result.Statistics.TypeOfExpressionsSeen);
        Assert.Equal(1, result.Statistics.IsTypeParamSeen);
        Assert.Equal(1, result.Statistics.AsTypeParamSeen);
    }

    // ===================================================================
    // 贴近 SDK 真实场景的复杂测试
    // ===================================================================

    [Fact]
    public void Preprocess_SdkLikeJsonPropertyInfo_RealWorldPattern()
    {
        // This mirrors the actual pattern from JsonPropertyInfoOfT.cs in
        // the dotnet/runtime SDK (System.Text.Json).
        var src = """
        using System.Collections.Generic;
        using System.Diagnostics;

        public class JsonPropertyInfo<T>
        {
            public T GetValue() => default(T);

            public bool GetMemberAndWriteJson(T value)
            {
                if (default(T) == null ? value == null : EqualityComparer<T>.Default.Equals(value))
                {
                    return true;
                }
                return false;
            }

            public void ReadJson()
            {
                Debug.Assert(default(T) == null);
                T value = default(T);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Statistics.DefaultExpressionsRewritten >= 3);
        // default(T) == null must NOT be rewritten.
        Assert.Contains("default(T) == null", result.OutputCode, StringComparison.Ordinal);
        // But T value = default(T) SHOULD be rewritten to `T value = default;`.
        Assert.Contains("T value = default;", result.OutputCode, StringComparison.Ordinal);
        // And GetValue() => default(T) SHOULD be rewritten to `=> default;`.
        Assert.Contains("=> default;", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericFactoryPattern()
    {
        // A realistic generic factory that creates instances and arrays.
        var src = """
        public class Factory<T> where T : new()
        {
            public T Create() => new T();

            public T[] CreateArray(int count)
            {
                var result = new T[count];
                for (int i = 0; i < count; i++)
                    result[i] = new T();
                return result;
            }

            public T CreateDefault() => default(T);

            public System.Type GetElementType() => typeof(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Should rewrite: new T() x2, new T[count], default(T).
        Assert.Equal(2, result.Statistics.NewTypeParamRewritten);
        Assert.Equal(1, result.Statistics.NewTypeParamArrayRewritten);
        Assert.True(result.Statistics.DefaultExpressionsRewritten >= 1);
        Assert.Equal(1, result.Statistics.TypeOfExpressionsSeen);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_NestedGenericClass_RepositoryPattern()
    {
        // Repository pattern with nested generics — similar to
        // ApiResponse<List<Order>> from the user's example.
        var src = """
        public class Repository<T>
        {
            public T Find(int id) => default(T);

            public T[] FindAll(int count) => new T[count];

            public T Create() => new T();
        }

        public class Service<T> where T : new()
        {
            private readonly Repository<T> _repo = new Repository<T>();

            public T GetOrCreate(int id)
            {
                var item = _repo.Find(id);
                if (item == null) return new T();
                return item;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        AssertNoSyntaxErrors(result.OutputCode);
        // default(T) in Find → rewritten.
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        // new T[count] in FindAll → rewritten.
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        // new T() in Create and GetOrCreate → rewritten.
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Preprocess_GenericMethodWithExplicitTypeArg_InferenceSafe()
    {
        // Static generic method with explicit type args — the rewriter
        // should handle default(T) safely when type args are explicit.
        var src = """
        class JsonSerializer
        {
            public static T Deserialize<T>(string json) => default(T);
            public static string Serialize<T>(T value) => value?.ToString() ?? "";
        }

        class C
        {
            public string RoundTrip<T>() => JsonSerializer.Serialize<T>(default(T));
        }
        """;
        var result = Preprocess(src);
        // Deserialize<T> returns default(T) — safe (return context), rewritten.
        // Serialize<T>(default(T)) — explicit type arg, safe to rewrite.
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_StaticGenericMethod_DeserializePattern()
    {
        // Mirrors the user's example: JsonSerializer.Deserialize<ApiResponse<List<Order>>>(json)
        // Here we test that default(T) in a static generic method is rewritten.
        var src = """
        class JsonSerializer
        {
            public static T Deserialize<T>(string json)
            {
                T result = default(T);
                // ... deserialize ...
                return result;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("default", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericLinkedList_SelfReferentialType()
    {
        // Self-referential generic type (linked list node) —
        // the type parameter T appears as the field type of Next.
        var src = """
        public class Node<T>
        {
            public T Value { get; set; }
            public Node<T>? Next { get; set; }

            public Node(T value)
            {
                Value = value;
            }

            public T GetDefault() => default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericClassWithConstraints_AllPatternsCovered()
    {
        // Complex class with multiple constraints and patterns.
        var src = """
        using System.Collections.Generic;

        public class Container<T> where T : new()
        {
            private List<T> _items = new List<T>();
            private T _default = default(T);

            public T Create() => new T();
            public T GetDefault() => default(T);
            public T[] ToArray(int n) => new T[n];

            public bool IsDefault(T value) =>
                default(T) == null ? value == null :
                EqualityComparer<T>.Default.Equals(value);

            public System.Type GetElementType() => typeof(T);
            public bool CheckType(object o) => o is T;
            public T? Cast(object o) => o as T;
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // new T() rewritten.
        Assert.Equal(1, result.Statistics.NewTypeParamRewritten);
        // new T[n] rewritten.
        Assert.Equal(1, result.Statistics.NewTypeParamArrayRewritten);
        // default(T) counted (some rewritten, some not).
        Assert.True(result.Statistics.DefaultExpressionsRewritten >= 3);
        // typeof(T), is T, as T tracked.
        Assert.Equal(1, result.Statistics.TypeOfExpressionsSeen);
        Assert.Equal(1, result.Statistics.IsTypeParamSeen);
        Assert.Equal(1, result.Statistics.AsTypeParamSeen);
        // default(T) == null NOT rewritten.
        Assert.Contains("default(T) == null", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_TwoTypeParametersDifferentPatterns()
    {
        // Class with two type parameters where each has different patterns.
        var src = """
        public class Map<TKey, TValue> where TValue : new()
        {
            public TKey GetDefaultKey() => default(TKey);
            public TValue GetDefaultValue() => default(TValue);
            public TValue CreateValue() => new TValue();
            public TValue[] CreateValueArray(int n) => new TValue[n];
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.NewTypeParamRewritten);
        Assert.Equal(1, result.Statistics.NewTypeParamArrayRewritten);
        Assert.True(result.Statistics.DefaultExpressionsRewritten >= 2);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 深度嵌套泛型类型参数（用户场景：ApiResponse<List<Order>>）
    // ===================================================================

    [Fact]
    public void Preprocess_DeeplyNestedGenericType_DefaultT_Rewritten()
    {
        // ApiResponse<List<Order>> 风格——T 在运行时被具现化为一个嵌套封闭泛型类型。
        // 在泛型类内部，T 仍然是类型参数，default(T) 应该被改写。
        // 注意：default(List<Order>) 是具名类型的 default，不是类型参数，
        // 重写器不会改写它——这是正确的行为。
        var src = """
        using System.Collections.Generic;

        public class ApiResponse<T>
        {
            public T? Data { get; set; }
            public bool Ok { get; set; }

            public ApiResponse()
            {
                Data = default(T);
            }
        }

        public class Order { public int Id { get; set; } }

        public class Handler
        {
            public ApiResponse<List<Order>> Parse(string json)
            {
                ApiResponse<List<Order>> resp = new ApiResponse<List<Order>>();
                resp.Data = new List<Order>();
                return resp;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.True(result.Statistics.DefaultExpressionsRewritten >= 1);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DeeplyNestedGenericType_GenericClassDefaultT()
    {
        // 类的泛型参数 T 本身可能是嵌套泛型（如 ApiResponse<List<Order>>），
        // 但在类内部 T 仍然是类型参数，default(T) 仍应被改写。
        var src = """
        public class ApiResponse<T>
        {
            public T? Data { get; set; }

            public ApiResponse()
            {
                Data = default(T);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("default", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DeeplyNestedGenericType_FactoryWithNewT()
    {
        // 泛型工厂方法内 new T()，T 在使用处可以是嵌套泛型
        var src = """
        using System.Collections.Generic;

        public class Factory<T> where T : new()
        {
            public T Create() => new T();
        }

        public class Use
        {
            public Factory<List<int>> GetFactory() => new Factory<List<int>>();
            public List<int> Make() => GetFactory().Create();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DeeplyNestedGenericType_ArrayOfNested()
    {
        // new T[n] 其中 T 在使用处是 List<Order> 这种嵌套类型
        var src = """
        using System.Collections.Generic;

        public class Container<T>
        {
            public T[] MakeArray(int n) => new T[n];
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("typeof(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 嵌套数组元素类型（IList<int[]> 等）
    // ===================================================================

    [Fact]
    public void Preprocess_IListOfIntArray_NotAffected()
    {
        // IList<int[]> —— 泛型类型参数不是类型参数，不受预处理影响。
        // 但如果类本身有 default(T) 或 new T()，这些应该被改写。
        var src = """
        using System.Collections.Generic;

        public class MatrixStore<T>
        {
            public IList<int[]> GetRows() => new List<int[]>();

            public T DefaultValue => default(T);

            public T[] CreateArray(int n) => new T[n];
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // IList<int[]> should remain unchanged.
        Assert.Contains("IList<int[]>", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("new List<int[]>()", result.OutputCode, StringComparison.Ordinal);
        // default(T) should be rewritten.
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        // new T[n] should be rewritten.
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_ListOfTArray_ElementNotRewritten()
    {
        // List<T[]> —— T[] is the type argument to List<>, not a type parameter
        // itself. The new List<T[]>() should NOT be rewritten as an array creation.
        var src = """
        using System.Collections.Generic;

        public class JaggedContainer<T>
        {
            public List<T[]> MakeList() => new List<T[]>();
        }
        """;
        var result = Preprocess(src);
        // List<T[]> is not a type parameter array creation — no rewrite.
        Assert.DoesNotContain("Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("new List<T[]>()", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_IListArrayOfT_NotAffected()
    {
        // List<int[]> and List<T[]> should both remain as-is (not array creations).
        var src = """
        using System.Collections.Generic;

        public class C<T>
        {
            public List<int[]> IntJagged => new List<int[]>();
            public List<T[]> GenericJagged => new List<T[]>();
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Contains("new List<int[]>()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("new List<T[]>()", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_TArrayOfArrayAsReturnType_NotAffected()
    {
        // T[][] as return type — not a new T[n][] array creation.
        var src = """
        using System.Collections.Generic;

        public class C<T>
        {
            public T[][] MakeJagged(int n)
            {
                T[][] result = new T[n][];
                for (int i = 0; i < n; i++)
                    result[i] = new T[n];
                return result;
            }
        }
        """;
        var result = Preprocess(src);
        // new T[n][] (jagged) should NOT be rewritten (element type is T[], not T).
        // new T[n] (inner) SHOULD be rewritten.
        Assert.Contains("new T[n][]", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 静态泛型方法的复杂调用模式
    // ===================================================================

    [Fact]
    public void Preprocess_StaticGenericMethod_DeserializeWithNestedTypeArg()
    {
        // JsonSerializer.Deserialize<ApiResponse<List<Order>>>(json)
        // —— 模拟用户的真实场景。类型参数在调用点是嵌套封闭泛型。
        var src = """
        using System.Collections.Generic;

        public class ApiResponse<T>
        {
            public T? Data { get; set; }
            public bool Ok { get; set; }
        }

        public class Order { public int Id { get; set; } }

        public static class JsonSerializer
        {
            public static T Deserialize<T>(string json)
            {
                T result = default(T);
                return result;
            }

            public static string Serialize<T>(T value) => value?.ToString() ?? "";
        }

        public class Program
        {
            public ApiResponse<List<Order>> Parse(string json)
            {
                ApiResponse<List<Order>> resp = JsonSerializer.Deserialize<ApiResponse<List<Order>>>(json);
                Order first = resp.Data[0];
                return resp;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // default(T) in Deserialize<T> should be rewritten (safe context: local var init).
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        // The call site with explicit nested type args should be unchanged.
        Assert.Contains("Deserialize<ApiResponse<List<Order>>>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_StaticGenericMethod_SerializeWithExplicitTypeArg()
    {
        // JsonSerializer.Serialize<T>(value) with explicit type arg —
        // default(T) as argument with explicit type args is safe to rewrite.
        var src = """
        using System.Collections.Generic;

        public class JsonSerializer
        {
            public static string Serialize<T>(T value) => value?.ToString() ?? "";
        }

        public class C
        {
            public string RoundTrip<T>() => JsonSerializer.Serialize<T>(default(T));
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Serialize<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_StaticGenericMethod_ActivatorPattern()
    {
        // Activator.CreateInstance<T>() is itself a static generic method —
        // the rewriter should rewrite new T() to Activator.CreateInstance<T>(),
        // but should NOT touch existing Activator.CreateInstance<T>() calls.
        var src = """
        public class Factory<T> where T : new()
        {
            public T Create() => new T();
            public T CreateViaActivator() => System.Activator.CreateInstance<T>();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.NewTypeParamRewritten);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        // The existing Activator call should remain.
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericMethodInference_SerializeDefaultT_NoExplicitArgs()
    {
        // JsonSerializer.Serialize(default(T)) — no explicit type args.
        // The rewriter must NOT rewrite default(T) here (CS0411 risk).
        var src = """
        public class JsonSerializer
        {
            public static string Serialize<T>(T value) => value?.ToString() ?? "";
        }

        public class C<T>
        {
            public string RoundTrip() => JsonSerializer.Serialize(default(T));
        }
        """;
        var result = Preprocess(src);
        // default(T) is counted but NOT rewritten (type inference context).
        Assert.True(result.Statistics.DefaultExpressionsRewritten >= 1);
        Assert.Contains("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 泛型方法中混合 default(T) 和 new T() 的复杂场景
    // ===================================================================

    [Fact]
    public void Preprocess_MixedDefaultAndNewInSameMethod_AllRewritten()
    {
        // A method that uses both default(T) and new T() — both should
        // be rewritten in safe contexts.
        var src = """
        using System.Collections.Generic;

        public class Cache<T> where T : new()
        {
            private List<T> _items = new List<T>();
            private T _default = default(T);

            public T GetOrCreate(int id)
            {
                T item = default(T);
                if (id > 0)
                    item = new T();
                return item;
            }

            public T[] GetAll(int n) => new T[n];
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericClassWithStaticMethod_DefaultT_Rewritten()
    {
        // Static method inside a generic class that uses the class type
        // parameter — default(T) should be rewritten.
        var src = """
        public class Repository<T> where T : new()
        {
            public static T CreateDefault()
            {
                T value = default(T);
                return value;
            }

            public static T[] CreateArray(int n) => new T[n];
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 泛型约束场景
    // ===================================================================

    [Fact]
    public void Preprocess_GenericClassStructConstraint_DefaultT_Rewritten()
    {
        // where T : struct — value type constraint.
        // default(T) for a struct T is zero-value; rewriting to `default`
        // is safe in assignment/return contexts.
        var src = """
        public class NumericOps<T> where T : struct
        {
            public T DefaultValue => default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericClassClassConstraint_DefaultT_Rewritten()
    {
        // where T : class — reference type constraint.
        // default(T) for a class T is null; rewriting to `default` is
        // safe in assignment/return contexts.
        var src = """
        public class RefOps<T> where T : class
        {
            public T DefaultValue => default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericMethodWithMultipleConstraints_AllRewritten()
    {
        // Method with multiple constraints: where T : class, new()
        var src = """
        public class Container
        {
            public T Create<T>() where T : class, new()
            {
                T item = new T();
                T def = default(T);
                return item;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 边界场景：深层嵌套泛型 + 混合模式
    // ===================================================================

    [Fact]
    public void Preprocess_DeepNesting_AllPatternsCombined()
    {
        // Complex scenario combining: nested generics, static methods,
        // default(T), new T(), new T[n], typeof(T), is T, as T
        var src = """
        using System;
        using System.Collections.Generic;

        public class ApiResponse<T>
        {
            public T? Data { get; set; }
            public bool Ok { get; set; }

            public ApiResponse()
            {
                Data = default(T);
            }
        }

        public class Order { public int Id { get; set; } public string? Name { get; set; } }

        public static class JsonSerializer
        {
            public static T Deserialize<T>(string json)
            {
                T result = default(T);
                return result;
            }
        }

        public class JsonRepository<T> where T : new()
        {
            private T _cache = default(T);

            public T Load(string json)
            {
                T item = JsonSerializer.Deserialize<T>(json);
                return item;
            }

            public T Create() => new T();
            public T[] CreateArray(int n) => new T[n];

            public Type GetElementType() => typeof(T);
            public bool Check(object o) => o is T;
            public T? Cast(object o) => o as T;
        }

        public class Program
        {
            public ApiResponse<List<Order>> Parse(string json)
            {
                ApiResponse<List<Order>> resp = JsonSerializer.Deserialize<ApiResponse<List<Order>>>(json);
                Order first = resp.Data[0];
                return resp;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // All default(T) in safe contexts should be rewritten.
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        // new T() should be rewritten.
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        // new T[n] should be rewritten.
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        // typeof(T), is T, as T tracked (not rewritten).
        Assert.True(result.Statistics.TypeOfExpressionsSeen >= 1);
        Assert.True(result.Statistics.IsTypeParamSeen >= 1);
        Assert.True(result.Statistics.AsTypeParamSeen >= 1);
        // The call site with nested type args should be unchanged.
        Assert.Contains("Deserialize<ApiResponse<List<Order>>>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericClassWithFieldAndPropertyDefaults_AllRewritten()
    {
        // default(T) in field initializers and property bodies.
        var src = """
        public class Config<T>
        {
            private T _value = default(T);
            public T Value => _value;
            public T DefaultValue => default(T);
            public T CachedValue { get; set; } = default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericExtensionMethodPattern()
    {
        // Extension method style: static generic method in a static class.
        var src = """
        public static class Extensions
        {
            public static T GetOrNew<T>(this T value) where T : new()
            {
                if (value == null) return new T();
                return value;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericClassWithNullableContext()
    {
        // Generic class using nullable annotations (T?, default(T)?).
        var src = """
        #nullable enable
        public class Optional<T> where T : class
        {
            private T? _value = default(T);

            public T? Value => _value;

            public T GetOrThrow() => _value ?? throw new System.InvalidOperationException();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_ThreeTypeParameters_AllRewritten()
    {
        // Three type parameters: TKey, TValue, TElement
        var src = """
        public class TripleMap<TKey, TValue, TElement>
            where TValue : new()
            where TElement : new()
        {
            public TKey GetDefaultKey() => default(TKey);
            public TValue GetDefaultValue() => default(TValue);
            public TElement GetDefaultElement() => default(TElement);
            public TValue CreateValue() => new TValue();
            public TElement CreateElement() => new TElement();
            public TValue[] CreateValueArray(int n) => new TValue[n];
            public TElement[] CreateElementArray(int n) => new TElement[n];
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(TKey)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("default(TValue)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("default(TElement)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new TValue()", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new TElement()", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new TValue[", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new TElement[", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 前向类型参数（forwarded-only type param）插入 typeof 声明
    // ===================================================================

    /// <summary>
    /// 当方法体中某个类型参数仅作为前向类型参数出现在泛型转发调用中
    /// （没有被 default(T)/typeof(T)/new T()/is T 等模式使用时），预处理器
    /// 应在方法体前部插入 ``var __cs2j_typeof_<T> = typeof(<T>);`` 以
    /// 触发转换器的类型参数识别。
    /// </summary>
    [Fact]
    public void Preprocess_ForwardedOnlyTypeParam_InsertsTypeOfStatement()
    {
        var src = @"
public abstract class Wrapper{
    public abstract T Deserialize<T>(string json);
}

public class Impl : Wrapper{
    public override T Deserialize<T>(string json){
        return Helper.Async<T>(json);
    }
}

public static class Helper{
    public static T Async<T>(string json){
        return default(T);
    }
}
";
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Impl.Deserialize 的 override 体中 T 仅出现在 Helper.Async<T> 中，
        // 没有被 default(T)/typeof(T)/new T() 使用，因此应插入 __cs2j_typeof_T
        Assert.Contains("__cs2j_typeof_T", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    /// <summary>
    /// 当类型参数已被"已跟踪模式"使用时，不重复插入 typeof 声明。
    /// </summary>
    [Fact]
    public void Preprocess_TypeParamAlreadyTracked_DoesNotInsertTypeOf()
    {
        var src = @"
public class Container{
    public T Convert<T>(){
        var _ = typeof(T);
        return Helper.Forward<T>();
    }
}

public static class Helper{
    public static T Forward<T>(){
        return default(T);
    }
}
";
        var result = Preprocess(src);
        // typeof(T) 已经显式出现，不需要再插入 __cs2j_typeof_T
        Assert.DoesNotContain("__cs2j_typeof_T", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    /// <summary>
    /// 插入的 typeof 声明必须严格在方法体前部、位于其他语句之前。
    /// </summary>
    [Fact]
    public void Preprocess_ForwardedOnlyTypeParam_TypeOfInsertedAtTop()
    {
        var src = @"
public abstract class Base{
    public abstract T Process<T>(string input);
}

public class Derived : Base{
    public override T Process<T>(string input){
        var result = Converter.Convert<T>(input);
        return result;
    }
}

public static class Converter{
    public static T Convert<T>(string input){
        return default(T);
    }
}
";
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("__cs2j_typeof_T", result.OutputCode, StringComparison.Ordinal);
        // 验证插入位置：在派生类的 override 方法体中
        var lines = result.OutputCode.Split('\n');
        var inDerivedMethod = false;
        var foundTypeOf = false;
        foreach (var line in lines)
        {
            if (line.Contains("public override T Process"))
                inDerivedMethod = true;
            if (inDerivedMethod && line.Contains("__cs2j_typeof_T"))
            {
                foundTypeOf = true;
                break;
            }
            // Override 方法内遇到除首部注释/声明之外的其它语句则应已过了 typeof
            if (inDerivedMethod && line.Contains("var result"))
                break;
        }
        Assert.True(foundTypeOf, "typeof 声明应插入到 override 方法体的前部");
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericClassStaticFieldsWithTypeParam()
    {
        // Generic class with static fields that use the type parameter.
        var src = """
        using System;

        public class TypeCache<T>
        {
            private static readonly Type s_type = typeof(T);
            private static readonly T s_default = default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("typeof(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.Equal(1, result.Statistics.TypeOfExpressionsSeen);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericMethodWithLocalFunction()
    {
        // Generic method containing a local function that also uses T.
        var src = """
        using System.Collections.Generic;

        public class Container<T> where T : new()
        {
            public List<T> Populate(int count)
            {
                T MakeDefault() => default(T);
                T MakeNew() => new T();

                var list = new List<T>();
                for (int i = 0; i < count; i++)
                    list.Add(MakeNew());
                return list;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 用户完整场景：System.Text.Json 反序列化（ApiResponse<List<Order>>）
    // ===================================================================

    [Fact]
    public void Preprocess_UserExample_SystemTextJson_DeserializeFullScenario()
    {
        // 用户的完整示例：
        //   using System.Text.Json;
        //   ApiResponse<List<Order>> resp = JsonSerializer.Deserialize<ApiResponse<List<Order>>>(json);
        //   Order first = resp.Data[0];
        //
        // 由于测试环境无法引用 System.Text.Json（单文件编译），我们用自定义的
        // JsonSerializer 模拟相同的泛型语义结构。关键验证：
        //   1. 泛型类 ApiResponse<T> 内的 default(T) 被改写
        //   2. 静态泛型方法 Deserialize<T> 内的 default(T) 被改写
        //   3. 调用点 Deserialize<ApiResponse<List<Order>>> 保持不变
        //   4. 索引访问 resp.Data[0] 保持不变
        var src = """
        using System;
        using System.Collections.Generic;

        public class Order
        {
            public int Id { get; set; }
            public string? Name { get; set; }
        }

        public class ApiResponse<T>
        {
            public T? Data { get; set; }
            public bool Ok { get; set; }

            public ApiResponse()
            {
                Data = default(T);
            }
        }

        public static class JsonSerializer
        {
            public static T Deserialize<T>(string json)
            {
                T result = default(T);
                return result;
            }

            public static string Serialize<T>(T value) => value?.ToString() ?? "";
        }

        public class Program
        {
            public void Main()
            {
                string json = "{ \"Ok\": true, \"Data\": [ { \"Id\": 1, \"Name\": \"x\" } ] }";

                // T 在运行时是具现化的：CLR 真正持有 ApiResponse<List<Order>> 这个类型
                ApiResponse<List<Order>> resp = JsonSerializer.Deserialize<ApiResponse<List<Order>>>(json);
                Order first = resp.Data[0];
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Deserialize<ApiResponse<List<Order>>>", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("resp.Data[0]", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_UserExample_DeserializeWithNewTInRepository()
    {
        // 用户场景延伸：JsonRepository<T> 不仅有 Deserialize<T>，
        // 还有 new T() 和 new T[n]。
        var src = """
        using System.Collections.Generic;

        public class ApiResponse<T>
        {
            public T? Data { get; set; }
            public bool Ok { get; set; }
        }

        public class Order { public int Id { get; set; } }

        public static class JsonSerializer
        {
            public static T Deserialize<T>(string json)
            {
                T result = default(T);
                return result;
            }
        }

        public class JsonRepository<T> where T : new()
        {
            public T Load(string json)
            {
                T item = JsonSerializer.Deserialize<T>(json);
                if (item == null)
                    item = new T();
                return item;
            }

            public T[] LoadAll(string json, int n)
            {
                T[] items = new T[n];
                return items;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 嵌套泛型类型：Outer<T>.Inner<U> 和 Dictionary<string, List<T>>
    // ===================================================================

    [Fact]
    public void Preprocess_OuterInnerNestedGeneric_DefaultT_Rewritten()
    {
        // Outer<T>.Inner<U> — 嵌套泛型类型，prompt 明确提到。
        // 外层类型参数 T 和内层类型参数 U 都应被独立识别。
        var src = """
        public class Outer<T>
        {
            public class Inner<U>
            {
                public T OuterValue = default(T);
                public U InnerValue = default(U);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("default(U)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DictionaryStringListT_DefaultT_Rewritten()
    {
        // Dictionary<string, List<T>> — prompt 提到的"泛型字典的嵌套值"。
        var src = """
        using System.Collections.Generic;

        public class Registry<T>
        {
            private Dictionary<string, List<T>> _lookup = new Dictionary<string, List<T>>();
            private T _default = default(T);

            public void Register(string key, T value)
            {
                if (!_lookup.ContainsKey(key))
                    _lookup[key] = new List<T>();
                _lookup[key].Add(value);
            }

            public T GetDefault() => default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, List<T>>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DictionaryStringListT_NewTArray_Rewritten()
    {
        // Dictionary<string, T[]> as field, plus new T[n] in method body.
        var src = """
        using System.Collections.Generic;

        public class Cache<T>
        {
            private Dictionary<string, T[]> _arrays = new Dictionary<string, T[]>();

            public void AddArray(string key, int n)
            {
                _arrays[key] = new T[n];
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, T[]>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 泛型委托 / Func<T> / Action<T> 场景
    // ===================================================================

    [Fact]
    public void Preprocess_FuncT_DefaultT_Rewritten()
    {
        // Func<T> returning default(T) — common in lazy initialization patterns.
        var src = """
        using System;

        public class Lazy<T> where T : new()
        {
            private Func<T> _factory = () => default(T);
            private T _value = default(T);

            public T Value => _value;
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_ActionT_NewT_Rewritten()
    {
        // Action<T> with new T() inside — common in callback patterns.
        var src = """
        using System;

        public class Producer<T> where T : new()
        {
            public Action<T> OnProduced { get; set; }

            public void Produce()
            {
                T item = new T();
                OnProduced?.Invoke(item);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 泛型接口实现
    // ===================================================================

    [Fact]
    public void Preprocess_GenericInterfaceImplementation_DefaultT_Rewritten()
    {
        // A class implementing a generic interface — type parameter flows.
        var src = """
        using System;
        using System.Collections.Generic;

        public interface IReader<T>
        {
            T Read();
            T[] ReadAll(int count);
        }

        public class DefaultReader<T> : IReader<T> where T : new()
        {
            public T Read() => default(T);
            public T[] ReadAll(int count) => new T[count];
            public T CreateNew() => new T();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_AbstractGenericClass_DefaultT_Rewritten()
    {
        // Abstract generic class with default(T) in concrete method.
        var src = """
        public abstract class BaseRepository<T>
        {
            public abstract T Find(int id);
            public T GetDefault() => default(T);
            public T[] FindAll(int count) => new T[count];
        }

        public class ConcreteRepository<T> : BaseRepository<T> where T : new()
        {
            public override T Find(int id) => new T();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T[", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // new T() 在复杂语句上下文中
    // ===================================================================

    [Fact]
    public void Preprocess_NewTInConditionalExpression_Rewritten()
    {
        // new T() inside a ternary — both branches create T.
        var src = """
        public class Factory<T> where T : new()
        {
            public T Create(bool flag) => flag ? new T() : new T();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.NewTypeParamRewritten);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_NewTInLambdaBody_Rewritten()
    {
        // new T() inside a lambda expression body.
        var src = """
        using System;
        using System.Collections.Generic;

        public class Factory<T> where T : new()
        {
            public List<T> CreateMany(int count)
            {
                var items = new List<T>();
                Action addOne = () => items.Add(new T());
                for (int i = 0; i < count; i++)
                    addOne();
                return items;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // default(T) 在更复杂的表达式上下文中
    // ===================================================================

    [Fact]
    public void Preprocess_DefaultTInTernaryTrueBranch_Rewritten()
    {
        // default(T) in the true branch of a ternary — safe context.
        var src = """
        public class Cache<T>
        {
            public T GetOrDefault(bool found) => found ? default(T) : default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultTInCoalesceExpression_Rewritten()
    {
        // default(T) in a null-coalescing expression — safe context.
        var src = """
        public class Optional<T> where T : class
        {
            private T _value = default(T);
            public T Value => _value ?? default(T);
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultTInSwitchExpression_Rewritten()
    {
        // default(T) in a switch expression arm — safe context.
        var src = """
        public class Dispatcher<T>
        {
            public T Dispatch(int code) => code switch
            {
                0 => default(T),
                _ => default(T),
            };
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_DefaultTInArrayElementAssignment_Rewritten()
    {
        // default(T) assigned to array element — safe context.
        var src = """
        public class Buffer<T>
        {
            public void Reset(T[] arr)
            {
                for (int i = 0; i < arr.Length; i++)
                    arr[i] = default(T);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 泛型方法的泛型参数（与方法所在类的类型参数不同）
    // ===================================================================

    [Fact]
    public void Preprocess_GenericMethodDifferentTypeParam_DefaultU_Rewritten()
    {
        // Class has type param T, method has its own type param U.
        var src = """
        public class Container<T>
        {
            public U Transform<U>() => default(U);
            public U[] TransformArray<U>(int n) => new U[n];
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(U)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new U[", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Array.CreateInstance", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("typeof(U)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericClassAndMethodBothTypeParams_AllRewritten()
    {
        // Class<T> with method<U> — both T and U used in same method.
        var src = """
        using System.Collections.Generic;

        public class Mapper<T> where T : new()
        {
            public List<U> MapAll<U>(int count) where U : new()
            {
                List<U> result = new List<U>();
                for (int i = 0; i < count; i++)
                    result.Add(new U());
                T def = default(T);
                return result;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new U()", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<U>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 泛型结构体和记录
    // ===================================================================

    [Fact]
    public void Preprocess_GenericStruct_DefaultT_Rewritten()
    {
        // Generic struct with default(T) — common in numerical code.
        var src = """
        public struct Wrapper<T>
        {
            public T Value;
            public static Wrapper<T> Default => new Wrapper<T> { Value = default(T) };
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_GenericRecord_DefaultT_Rewritten()
    {
        // Generic record with default(T) — modern C# pattern.
        var src = """
        public record Result<T>
        {
            public T Data { get; init; } = default(T);
            public bool Success { get; init; }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // LINQ 场景中的泛型
    // ===================================================================

    [Fact]
    public void Preprocess_LinqWithGenericClass_NewT_Rewritten()
    {
        // LINQ Select with new T() inside — common pattern.
        var src = """
        using System.Collections.Generic;
        using System.Linq;

        public class Factory<T> where T : new()
        {
            public List<T> CreateRange(int count)
            {
                return Enumerable.Range(0, count)
                    .Select(_ => new T())
                    .ToList();
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 协变/逆变接口中的类型参数
    // ===================================================================

    [Fact]
    public void Preprocess_CovariantInterface_NewT_Rewritten()
    {
        // `out T` covariance — the interface uses T only in output position.
        var src = """
        using System.Collections.Generic;

        public interface IProducer<out T>
        {
            T Produce();
        }

        public class DefaultProducer<T> : IProducer<T> where T : new()
        {
            public T Produce() => new T();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Preprocess_ContravariantInterface_NewT_Rewritten()
    {
        // `in T` contravariance — the interface uses T only in input position.
        var src = """
        public interface IConsumer<in T>
        {
            void Consume(T value);
        }

        public class DefaultConsumer<T> : IConsumer<T> where T : new()
        {
            public void Consume(T value)
            {
                if (value == null)
                    value = new T();
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // 泛型类继承泛型基类
    // ===================================================================

    [Fact]
    public void Preprocess_GenericClassInheritingGenericBase_DefaultT_Rewritten()
    {
        // Generic class inheriting from another generic class.
        var src = """
        public class Base<T>
        {
            public virtual T GetDefault() => default(T);
        }

        public class Derived<T> : Base<T> where T : new()
        {
            public override T GetDefault() => new T();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("default(T)", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new T()", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("Activator.CreateInstance<T>", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // Abstract → Virtual 改写 + Override typeof(T) 同步
    // ===================================================================

    /// <summary>
    /// 当 abstract 泛型方法的 override 使用了 typeof(T) 时，
    /// 预处理器应将 abstract 改为 virtual 并添加 typeof(T) 哨兵语句
    /// 和 throw new NotImplementedException() 方法体。
    /// </summary>
    [Fact]
    public void Preprocess_AbstractGenericMethod_OverrideUsesTypeOf_ConvertedToVirtual()
    {
        var src = """
        using System;

        public abstract class MemberAccessor
        {
            public abstract Func<T> CreateGetter<T>(string name);
        }

        public class ReflectionAccessor : MemberAccessor
        {
            public override Func<T> CreateGetter<T>(string name)
            {
                var t = typeof(T);
                return null;
            }
        }
        """;
        var result = PreprocessWithPrescan(src);
        Assert.True(result.Changed);

        // abstract 方法应被改为 virtual（但类声明仍保持 abstract）
        Assert.Contains("virtual Func<T> CreateGetter", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("abstract Func<T> CreateGetter", result.OutputCode, StringComparison.Ordinal);

        // base 方法体应包含 typeof(T) 哨兵语句
        Assert.Contains("__cs2j_typeof_T", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("typeof(T)", result.OutputCode, StringComparison.Ordinal);

        // 应包含 throw new NotImplementedException()
        Assert.Contains("NotImplementedException", result.OutputCode, StringComparison.Ordinal);

        AssertNoSyntaxErrors(result.OutputCode);
    }

    /// <summary>
    /// 当 override 方法体不使用 typeof(T) 时，abstract 方法不应被改写。
    /// </summary>
    [Fact]
    public void Preprocess_AbstractGenericMethod_NoOverrideTypeOf_NotChanged()
    {
        var src = """
        public abstract class Base
        {
            public abstract T GetValue<T>(int id);
        }

        public class Derived : Base
        {
            public override T GetValue<T>(int id) => default;
        }
        """;
        var result = PreprocessWithPrescan(src);
        // default(T) in GetValue override is rewritten, but abstract is not
        // changed to virtual because no typeof(T) is used in any override.
        Assert.Contains("abstract", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("__cs2j_typeof_T", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("NotImplementedException", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    /// <summary>
    /// 非泛型 abstract 方法不应被改写为 virtual。
    /// </summary>
    [Fact]
    public void Preprocess_AbstractNonGenericMethod_NotChanged()
    {
        var src = """
        public abstract class Base
        {
            public abstract int GetValue(int id);
        }

        public class Derived : Base
        {
            public override int GetValue(int id) => 0;
        }
        """;
        var result = PreprocessWithPrescan(src);
        Assert.False(result.Changed);
        Assert.Contains("abstract", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("NotImplementedException", result.OutputCode, StringComparison.Ordinal);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    /// <summary>
    /// 多个 override 方法中只有一个使用 typeof(T) 时，
    /// 另一个不使用 typeof(T) 的 override 应被注入哨兵语句。
    /// 同时 base abstract 方法也应被改为 virtual。
    /// </summary>
    [Fact]
    public void Preprocess_MultipleOverrides_OneUsesTypeOf_Synced()
    {
        var src = """
        using System;

        public abstract class MemberAccessor
        {
            public abstract Func<T> CreateGetter<T>(string name);
        }

        public class ReflectionAccessor : MemberAccessor
        {
            public override Func<T> CreateGetter<T>(string name)
            {
                var t = typeof(T);
                return null;
            }
        }

        public class EmitAccessor : MemberAccessor
        {
            public override Func<T> CreateGetter<T>(string name)
            {
                return null;
            }
        }
        """;
        var result = PreprocessWithPrescan(src);
        Assert.True(result.Changed);

        // abstract method should be changed to virtual (class stays abstract)
        Assert.Contains("virtual Func<T> CreateGetter", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("abstract Func<T> CreateGetter", result.OutputCode, StringComparison.Ordinal);

        // The EmitAccessor override should have __cs2j_typeof_T injected
        // (it doesn't use typeof(T) but a sibling override does)
        // Count occurrences of __cs2j_typeof_T:
        // 1 in base method body, 1 in EmitAccessor override body
        var typeofCount = System.Text.RegularExpressions.Regex.Count(
            result.OutputCode, "__cs2j_typeof_T");
        Assert.True(typeofCount >= 2,
            $"Expected at least 2 __cs2j_typeof_T injections, found {typeofCount}\n{result.OutputCode}");

        AssertNoSyntaxErrors(result.OutputCode);
    }

    /// <summary>
    /// 当 override 方法体中已有 typeof(T) 时，不应重复注入哨兵语句。
    /// </summary>
    [Fact]
    public void Preprocess_OverrideAlreadyHasTypeOf_NoDuplicateInjection()
    {
        var src = """
        using System;

        public abstract class MemberAccessor
        {
            public abstract Func<T> CreateGetter<T>(string name);
        }

        public class ReflectionAccessor : MemberAccessor
        {
            public override Func<T> CreateGetter<T>(string name)
            {
                var t = typeof(T);
                return null;
            }
        }
        """;
        var result = PreprocessWithPrescan(src);
        Assert.True(result.Changed);

        // The override method should NOT have a duplicate __cs2j_typeof_T
        // (it already has typeof(T) explicitly)
        var lines = result.OutputCode.Split('\n');
        var inOverride = false;
        var typeOfCountInOverride = 0;
        foreach (var line in lines)
        {
            if (line.Contains("override") && line.Contains("CreateGetter"))
                inOverride = true;
            else if (line.Contains("virtual") && line.Contains("CreateGetter"))
                inOverride = false; // base method, not override

            if (inOverride && line.Contains("__cs2j_typeof_T"))
                typeOfCountInOverride++;
        }
        Assert.True(typeOfCountInOverride == 0,
            $"Override already has typeof(T), should not have __cs2j_typeof_T injection. Found {typeOfCountInOverride}");

        AssertNoSyntaxErrors(result.OutputCode);
    }

    /// <summary>
    /// 贴近 SDK 真实场景：MemberAccessor + JsonPropertyInfo&lt;T&gt;
    /// 验证 abstract CreatePropertyGetter&lt;T&gt; 被改为 virtual，
    /// 且调用点能检测到 typeof(T) 用法。
    /// </summary>
    [Fact]
    public void Preprocess_SdkLikeMemberAccessor_AbstractToVirtual()
    {
        var src = """
        using System;
        using System.Reflection;

        public abstract class MemberAccessor
        {
            public abstract Func<T> CreatePropertyGetter<T>(PropertyInfo prop);
            public abstract Action<T> CreatePropertySetter<T>(PropertyInfo prop);
        }

        public class ReflectionEmitMemberAccessor : MemberAccessor
        {
            public override Func<T> CreatePropertyGetter<T>(PropertyInfo prop)
            {
                var tClass = typeof(T);
                return null;
            }

            public override Action<T> CreatePropertySetter<T>(PropertyInfo prop)
            {
                var tClass = typeof(T);
                return null;
            }
        }

        public class JsonPropertyInfo<T>
        {
            public void Initialize(MemberAccessor accessor, PropertyInfo prop)
            {
                var getter = accessor.CreatePropertyGetter<T>(prop);
                var setter = accessor.CreatePropertySetter<T>(prop);
            }
        }
        """;
        var result = PreprocessWithPrescan(src);
        Assert.True(result.Changed);

        // Both abstract methods should be changed to virtual (class stays abstract)
        Assert.DoesNotContain("abstract Func<T> CreatePropertyGetter", result.OutputCode, StringComparison.Ordinal);
        Assert.DoesNotContain("abstract Action<T> CreatePropertySetter", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("virtual Func<T> CreatePropertyGetter", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("virtual Action<T> CreatePropertySetter", result.OutputCode, StringComparison.Ordinal);

        // Both base methods should have __cs2j_typeof_T
        var typeofCount = System.Text.RegularExpressions.Regex.Count(
            result.OutputCode, "__cs2j_typeof_T");
        Assert.True(typeofCount >= 2,
            $"Expected at least 2 __cs2j_typeof_T in base methods, found {typeofCount}\n{result.OutputCode}");

        AssertNoSyntaxErrors(result.OutputCode);
    }

    // ===================================================================
    // Helpers
    // ===================================================================

    private static GenericPreprocessResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("generic-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new GenericPreprocessor();
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
    }

    /// <summary>
    /// 完整编译级预处理：先调用 PrescanOverrideSiblings 构建全局 override 同步映射，
    /// 再逐文件预处理。模拟 ProjectGenericPreprocessor 的实际工作流程。
    /// </summary>
    private static GenericPreprocessResult PreprocessWithPrescan(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("generic-prescan-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var preprocessor = new GenericPreprocessor();
        // 模拟 ProjectGenericPreprocessor 的工作流程：先预扫描，再逐文件处理
        preprocessor.PrescanOverrideSiblings(compilation);
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
    }

    private static void AssertNoSyntaxErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should be valid C#. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }

    /// <summary>
    /// 表达式体 override 也必须拿到哨兵：否则它没有 Class&lt;T&gt; 令牌，而基类声明（抽象方法，
    /// 因同一同步集合被改写为 virtual 并注入哨兵）有令牌 ⇒ 覆写实际不成立、虚派发落到基类
    /// 抛异常体（SDK 实测 35 项失败，栈内均含 MemberAccessor.createField*）。
    /// </summary>
    [Fact]
    public void OverrideSync_InjectsSentinelIntoExpressionBodiedOverride()
    {
        const string source = """
            using System;
            using System.Reflection;

            public abstract class Accessor
            {
                public abstract Func<object, TProperty> CreateFieldGetter<TProperty>(FieldInfo fieldInfo);
                public abstract Func<object, TProperty> CreatePropertyGetter<TProperty>(PropertyInfo propertyInfo);
            }

            public sealed class ReflectionAccessor : Accessor
            {
                // 表达式体且体内没有 typeof ⇒ 旧实现在这里直接返回，永不注入哨兵。
                public override Func<object, TProperty> CreateFieldGetter<TProperty>(FieldInfo fieldInfo)
                    => (obj) => (TProperty)fieldInfo.GetValue(obj);

                // 同名类型参数出现在 typeof(TProperty) 中 ⇒ 进入同步集合。
                public override Func<object, TProperty> CreatePropertyGetter<TProperty>(PropertyInfo propertyInfo)
                    => (obj) => typeof(TProperty) == null ? default : (TProperty)propertyInfo.GetValue(obj);
            }
            """;

        var result = PreprocessWithPrescan(source);
        const string sentinel = "__cs2j_typeof_TProperty = typeof(TProperty)";
        var count = result.OutputCode.Split(sentinel).Length - 1;
        Assert.True(count >= 3,
            $"表达式体 override 也应被注入哨兵（期望 >=3 处，实际 {count} 处）。\n{result.OutputCode}");
        AssertNoSyntaxErrors(result.OutputCode);
    }
}
