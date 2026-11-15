using CSharpToJava.Core.LocalFunctionLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// 顶层语句（top-level statements）里的局部函数：没有语法宿主类型，
/// 提升方法统一放进文件末尾合成的 <c>internal static class __Cs2jLocalFunctions</c>。
/// 顶层语句需要 <see cref="OutputKind.ConsoleApplication"/> 才能无 CS8805 地编译。
/// </summary>
public class LocalFunctionLiftTopLevelTests
{
    [Fact]
    public void TopLevel_LocalFunction_IsLiftedIntoGeneratedClass()
    {
        var src = """
        using System;

        Console.WriteLine(Local(2));
        int Local(int x) => x * 2;
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("internal static class __Cs2jLocalFunctions", result.OutputCode);
        Assert.Contains("internal static int local(int x)", result.OutputCode);
        Assert.Contains("__Cs2jLocalFunctions.local(2)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void TopLevel_LocalFunction_WithCapture_UsesParameters()
    {
        var src = """
        using System;

        int factor = 3;
        Console.WriteLine(Scale(2));
        int Scale(int x) => x * factor;
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains(
            "internal static int scale(int _cs2j_cap_factor, int x)",
            result.OutputCode);
        Assert.Contains("__Cs2jLocalFunctions.scale(factor, 2)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void TopLevel_LocalFunction_WriteCapture_UsesRef()
    {
        var src = """
        using System;

        int total = 0;
        Add(2);
        Console.WriteLine(total);
        void Add(int x) { total += x; }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains(
            "internal static void add(ref int _cs2j_cap_total, int x)",
            result.OutputCode);
        Assert.Contains("__Cs2jLocalFunctions.add(ref total, 2)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void TopLevel_AnonymousMethodBody_LocalFunction_StillLifted()
    {
        var src = """
        using System;

        Func<int> f = () => { return Local(); int Local() => 3; };
        Console.WriteLine(f());
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("internal static int local()", result.OutputCode);
        Assert.Contains("__Cs2jLocalFunctions.local()", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void TopLevel_NameCollision_GeneratedClassNameSuffixed()
    {
        var src = """
        using System;

        Console.WriteLine(Local());
        int Local() => 1;

        class __Cs2jLocalFunctions { }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("internal static class __Cs2jLocalFunctions2", result.OutputCode);
        Assert.Contains("__Cs2jLocalFunctions2.local()", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void TopLevel_NameOf_LocalFunction_KeepsOriginalName()
    {
        var src = """
        using System;

        Console.WriteLine(nameof(Add));
        int Add(int a, int b) => a + b;
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("\"Add\"", result.OutputCode);
        Assert.DoesNotContain("nameof(add)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void TopLevel_ConditionalDeclared_LocalFunction_IsSkippedWithDiagnostic()
    {
        // DEBUG 启用时该局部函数是活动代码，但声明点位于非空 directive 栈：
        // 合成类方法无法安全落进同一分支，保守跳过并给出精确诊断。
        var src = """
        using System;

        #if DEBUG
        Console.WriteLine(Local());
        int Local() => 1;
        #endif
        Console.WriteLine(2);
        """;
        var result = Preprocess(src, "DEBUG");

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(1, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.ConditionalTopLevelDeclaration);
        Assert.Contains("int Local() => 1;", result.OutputCode);
    }

    [Fact]
    public void TopLevel_ConditionalDeclared_SkipDoesNotRollBackFile()
    {
        var src = """
        using System;

        Console.WriteLine(Unconditional());
        #if DEBUG
        int Conditional() => 1;
        #endif
        Console.WriteLine(2);
        int Unconditional() => 3;
        """;
        var result = Preprocess(src, "DEBUG");

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(1, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(0, result.Statistics.FilesRolledBackAtomicity);
        Assert.Contains("__Cs2jLocalFunctions.unconditional()", result.OutputCode);
        Assert.Contains("int Conditional() => 1;", result.OutputCode);
        Assert.True(
            result.OutputCode.IndexOf("#endif", StringComparison.Ordinal)
            < result.OutputCode.IndexOf("class __Cs2jLocalFunctions", StringComparison.Ordinal),
            "合成的静态类必须位于文件尾部 #endif 之外");
        AssertCompiles(result.OutputCode, "DEBUG");
    }

    private static LocalFunctionLiftResult Preprocess(string source, params string[] symbols)
    {
        var parseOptions = new CSharpParseOptions(preprocessorSymbols: symbols);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "local-fn-toplevel",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true));
        return new LocalFunctionLiftPreprocessor().Preprocess(
            source, compilation.GetSemanticModel(tree));
    }

    private static void AssertNoLocalFunctions(string code, params string[] symbols)
    {
        var root = CSharpSyntaxTree.ParseText(
                code, new CSharpParseOptions(preprocessorSymbols: symbols))
            .GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>());
    }

    private static void AssertCompiles(string code, params string[] symbols)
    {
        var tree = CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(preprocessorSymbols: symbols));
        var compilation = CSharpCompilation.Create(
            "local-fn-toplevel-output",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should compile without errors. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
    };
}
