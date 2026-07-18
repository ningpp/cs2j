using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CSharpToJava.Tests;

public class FunctionImportDisambiguationTests
{
    private static ConversionOptions CreateProjectOptions()
        => new()
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };

    [Fact]
    public async Task ProjectWithFuncAndProjectFunction_UsesExplicitJdkFunctionImport()
    {
        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Function.cs",
                Content = """
                    namespace MS.Internal.Xml.XPath
                    {
                        public class Function { }
                    }
                    """,
            },
            new SourceFile
            {
                FilePath = "Helper.cs",
                Content = """
                    using System.Threading.Tasks;
                    using MS.Internal.Xml.XPath;

                    namespace dotnet.xml
                    {
                        public static class Helper
                        {
                            public static Task CallAsync<TArg>(this Task task, System.Func<TArg, Task> func, TArg arg)
                            {
                                return func(arg);
                            }
                        }
                    }
                    """,
            },
        });

        var helper = Assert.Single(results, r => r.FileName == "Helper.java");
        Assert.True(helper.Success, string.Join("\n", helper.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import java.util.function.Function;", helper.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("import java.util.function.*;", helper.GeneratedCode, StringComparison.Ordinal);
    }
}
