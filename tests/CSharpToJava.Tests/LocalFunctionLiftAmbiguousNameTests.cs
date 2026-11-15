using CSharpToJava.Core.LocalFunctionLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// C05 局部函数提升：同一宿主类型内重名局部函数 + 调用点符号绑定失败。
/// 旧策略是整组保守跳过；C# 的局部函数遵循词法作用域，不同方法/语句容器里的
/// 同名声明彼此不可见，调用点只会看到其最内层语句容器里的那一个（同容器则是
/// 重载）。因此未绑定调用点可以按词法作用域 + 重载消歧唯一解析，全部提升。
/// 真实 SDK：json 的 JsonSerializerOptions 在两个方法里都声明局部函数 Add
/// （Dictionary / ConcurrentDictionary 捕获）；整树编译把 regular-expressions
/// 工程的 global using System.ComponentModel 泄漏进 json 文件，使
/// new BooleanConverter() 二义（CS0104）、Add(...) 绑定失败。
/// </summary>
public class LocalFunctionLiftAmbiguousNameTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    private const string MainSource = """
        using System.Collections.Concurrent;
        using System.Collections.Generic;
        using Demo.Converters;

        namespace Demo
        {
            public class Options
            {
                public static Dictionary<System.Type, object> M()
                {
                    var simple = new Dictionary<System.Type, object>();
                    void Add(object converter) => simple.Add(converter.GetType(), converter);

                    Add(new BooleanConverter());
                    return simple;
                }

                public static ConcurrentDictionary<System.Type, object> N()
                {
                    var concurrent = new ConcurrentDictionary<System.Type, object>();
                    void Add(object converter) => concurrent[converter.GetType()] = converter;

                    Add(new BooleanConverter());
                    return concurrent;
                }
            }
        }

        namespace Demo.Converters
        {
            public class BooleanConverter { }
        }
        """;

    private const string SameBlockOverloadSource = """
        using System.Collections.Generic;
        using Demo.Converters;

        namespace Demo
        {
            public class Overloads
            {
                public static Dictionary<System.Type, object> M()
                {
                    var map = new Dictionary<System.Type, object>();
                    void Add(object converter) => map[converter.GetType()] = converter;
                    void Add(object key, object value) => map[key.GetType()] = value;

                    Add(new BooleanConverter());
                    Add(new BooleanConverter(), 42);
                    return map;
                }
            }
        }

        namespace Demo.Converters
        {
            public class BooleanConverter { }
        }
        """;

    private const string SameBlockTypedOverloadSource = """
        using System.Collections.Generic;
        using Demo.Converters;

        namespace Demo
        {
            public class TypedOverloads
            {
                public static List<object> M()
                {
                    var map = new List<object>();
                    void Add(object key, object value) => map.Add(key);
                    void Add(object key, string value) => map.Add(value);

                    Add(new BooleanConverter(), "text");
                    return map;
                }
            }
        }

        namespace Demo.Converters
        {
            public class BooleanConverter { }
        }
        """;

    private const string SameBlockAllErrorArgumentsSource = """
        using System.Collections.Generic;
        using Demo.Converters;

        namespace Demo
        {
            public class UnknownOverloads
            {
                public static List<object> M()
                {
                    var map = new List<object>();
                    void Add(object converter) => map.Add(converter);
                    void Add(string converter) => map.Add(converter);

                    Add(new BooleanConverter());
                    return map;
                }
            }
        }

        namespace Demo.Converters
        {
            public class BooleanConverter { }
        }
        """;

    private const string RecursiveSource = """
        using System.Collections.Generic;
        using Demo.Converters;

        namespace Demo
        {
            public class Recursive
            {
                public static void M()
                {
                    var seen = new List<object>();
                    void Add(object converter)
                    {
                        seen.Add(converter);
                        Add(new BooleanConverter());
                    }

                    Add(new BooleanConverter());
                }
            }
        }

        namespace Demo.Converters
        {
            public class BooleanConverter { }
        }
        """;

    private const string MethodGroupSource = """
        using System;
        using System.Collections.Generic;
        using Demo.Converters;

        namespace Demo
        {
            public class MethodGroups
            {
                public static Action<object> M()
                {
                    var first = new List<object>();
                    void Add(object converter) => first.Add(converter);
                    Action<object> action = Add;
                    Add(new BooleanConverter());
                    return action;
                }

                public static Action<object> N()
                {
                    var second = new List<object>();
                    void Add(object converter) => second.Add(converter);
                    Action<object> action = Add;
                    Add(new BooleanConverter());
                    return action;
                }
            }
        }

        namespace Demo.Converters
        {
            public class BooleanConverter { }
        }
        """;

    private static LocalFunctionLiftResult Lift(string? leakSource)
        => Lift(MainSource, leakSource);

    private static LocalFunctionLiftResult Lift(string source, string? leakSource)
    {
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(source, ParseOptions, path: "main.cs"),
        };
        if (leakSource != null)
        {
            trees.Add(CSharpSyntaxTree.ParseText(leakSource, ParseOptions, path: "other.cs"));
        }

        var compilation = CSharpCompilation.Create(
            "local-fn-ambiguous",
            trees,
            CSharpToJava.CLI.ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var mainTree = trees[0];

        return new LocalFunctionLiftPreprocessor().Preprocess(
            source, compilation.GetSemanticModel(mainTree));
    }

    [Fact]
    public void Bound_Call_Sites_Lift_Distinct_Duplicate_Names()
    {
        var result = Lift(null);

        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("add(simple, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
        Assert.Contains("add2(concurrent, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Unbound_Duplicate_Name_Call_Sites_Lift_By_Lexical_Scope()
    {
        // 另一个工程的 global using 泄漏进同一整树编译 → BooleanConverter 二义。
        var result = Lift("global using System.ComponentModel;");

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("add(simple, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
        Assert.Contains("add2(concurrent, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void Unbound_SameBlockOverloads_Resolve_By_Arity()
    {
        var result = Lift(SameBlockOverloadSource, "global using System.ComponentModel;");

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("add(map, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
        Assert.Contains("add2(map, new BooleanConverter(), 42)", result.OutputCode,
            StringComparison.Ordinal);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void Unbound_SameBlockOverloads_Resolve_By_ArgumentTypes()
    {
        // 唯一的联合实参类型错误（BooleanConverter 二义），但第二个实参 "text" 的
        // 类型已知：C# 的重载择优会选中 (object, string)。解析器必须用实参类型
        // 补足这个证据，而不是因为第一个实参类型不可用就整组跳过。
        var result = Lift(SameBlockTypedOverloadSource, "global using System.ComponentModel;");

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("add2(map, new BooleanConverter(), \"text\")", result.OutputCode,
            StringComparison.Ordinal);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void Unbound_SameBlockOverloads_AllErrorArguments_FallBackWithoutResidual()
    {
        // 所有实参类型都不可用（唯一的实参由二义类型构造）：解析器没有类型证据，
        // 只能回到 Roslyn 的候选（它就是要求「不新增残留」的既有行为）。
        // 关键契约：不因为类型证据缺失而跳过，residual 仍为 0。
        var result = Lift(SameBlockAllErrorArgumentsSource, "global using System.ComponentModel;");

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void Unbound_RecursiveCall_Resolves_To_Self()
    {
        var result = Lift(RecursiveSource, "global using System.ComponentModel;");

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("add(seen, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
        Assert.Contains("add(_cs2j_cap_seen, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void Unbound_MethodGroupReference_DuplicateNames_Resolves_By_Scope()
    {
        var result = Lift(MethodGroupSource, "global using System.ComponentModel;");

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("add(first, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
        Assert.Contains("add2(second, new BooleanConverter())", result.OutputCode,
            StringComparison.Ordinal);
        // 方法组引用带捕获 → 合成 lambda，调用目标按词法作用域选对 plan。
        Assert.Contains("add(first, __cs2jArg0)", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("add2(second, __cs2jArg0)", result.OutputCode, StringComparison.Ordinal);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void Unbound_Duplicate_Names_Residual_Is_Zero()
    {
        var result = Lift("global using System.ComponentModel;");

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        var root = CSharpSyntaxTree.ParseText(
            result.OutputCode, new CSharpParseOptions(LanguageVersion.Latest)).GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>());
    }

    private static void AssertNoLocalFunctions(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>());
    }

    private static void AssertCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "local-fn-ambiguous-output",
            new[] { tree },
            CompileReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Output should compile without errors. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }

    private static readonly MetadataReference[] CompileReferences =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(
            typeof(System.Collections.Concurrent.ConcurrentDictionary<,>).Assembly.Location),
        MetadataReference.CreateFromFile(
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
    };
}
