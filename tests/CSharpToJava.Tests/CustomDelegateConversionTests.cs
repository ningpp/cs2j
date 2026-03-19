using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for custom delegate declaration and invocation.
///
/// Bug fixed: DelegateTransformer generated @FunctionalInterface with method name "invoke",
/// but InvocationExpressionTransformer used the fallback "apply" for custom delegate calls.
/// Both sides now use "apply" so they are consistent.
/// </summary>
public class CustomDelegateConversionTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── 声明侧：SAM 方法名必须是 apply ───────────────────────────────────────

    [Fact]
    public void CustomDelegate_DeclarationGeneratesApplyMethod()
    {
        const string code = """
            class Outer
            {
                delegate double Mapper(int x);
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("double apply(int", result.GeneratedCode);
        Assert.DoesNotContain("double invoke(", result.GeneratedCode);
    }

    [Fact]
    public void CustomVoidDelegate_DeclarationGeneratesApplyMethod()
    {
        const string code = """
            class Outer
            {
                delegate void Worker(int x);
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("void apply(int", result.GeneratedCode);
        Assert.DoesNotContain("void invoke(", result.GeneratedCode);
    }

    [Fact]
    public void CustomNoArgDelegate_DeclarationGeneratesApplyMethod()
    {
        const string code = """
            class Outer
            {
                delegate void Run();
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("void apply()", result.GeneratedCode);
        Assert.DoesNotContain("void invoke()", result.GeneratedCode);
    }

    // ── 调用侧：自定义委托字段/参数调用生成 apply ───────────────────────────

    [Fact]
    public void CustomDelegate_FieldInvocation_EmitsApply()
    {
        const string code = """
            class Outer
            {
                delegate double Mapper(int x);
                Mapper mapFn;

                double Run(int m)
                {
                    return mapFn(m);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("mapFn.apply(m)", result.GeneratedCode);
        Assert.DoesNotContain("mapFn.invoke(", result.GeneratedCode);
    }

    [Fact]
    public void CustomDelegate_ParameterInvocation_EmitsApply()
    {
        const string code = """
            class Outer
            {
                delegate void Worker(string s);

                void Execute(Worker fn, string value)
                {
                    fn(value);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("fn.apply(value)", result.GeneratedCode);
        Assert.DoesNotContain("fn.invoke(", result.GeneratedCode);
    }

    [Fact]
    public void CustomDelegate_MemberAccessInvocation_EmitsApply()
    {
        const string code = """
            class Outer
            {
                delegate int Selector(string s);
                Selector selector;

                int Run(string s)
                {
                    return this.selector(s);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("apply(s)", result.GeneratedCode);
        Assert.DoesNotContain("invoke(s)", result.GeneratedCode);
    }

    // ── 泛型委托 ─────────────────────────────────────────────────────────────

    [Fact]
    public void CustomGenericDelegate_DeclarationPreservesTypeParams()
    {
        const string code = """
            class Outer
            {
                delegate TResult Transform<TInput, TResult>(TInput input);
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("@FunctionalInterface", result.GeneratedCode);
        Assert.Contains("apply(", result.GeneratedCode);
        Assert.DoesNotContain("invoke(", result.GeneratedCode);
    }
}
