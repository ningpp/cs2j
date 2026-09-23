using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Fully-qualified type references across namespaces (e.g. Dot2Graph.Parser.Parse(...)
/// or ArgsParser.ArgsParser as a field type) must register a Java import for the
/// target package, even when the C# file has no corresponding using directive.
/// </summary>
public class QualifiedCrossNamespaceReferenceTests
{

    [Fact]
    public async Task FullyQualifiedFieldTypeFromOtherNamespace_AddsImport()
    {
        var results = await ConvertAsync(
            new SourceFile
            {
                FilePath = "ArgsParser.cs",
                Content = """
namespace ArgsParser {
    public class ArgsParser {
        public ArgsParser(string[] args) { }
    }
}
""",
            },
            new SourceFile
            {
                FilePath = "Main.cs",
                Content = """
namespace Agl {
    public class Main2 {
        readonly ArgsParser.ArgsParser argsParser;
        public Main2(string[] args) {
            argsParser = new ArgsParser.ArgsParser(args);
        }
    }
}
""",
            });

        Assert.All(results, r => Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var main = results.Single(r => r.FileName == "Main2.java");
        Assert.Contains("import ArgsParser.ArgsParser;", main.GeneratedCode);
        Assert.Contains("private ArgsParser argsParser", main.GeneratedCode);
    }

    [Fact]
    public async Task FullyQualifiedGenericTypeFromOtherNamespace_AddsImport()
    {
        var results = await ConvertAsync(
            new SourceFile
            {
                FilePath = "Box.cs",
                Content = """
namespace Toolbox {
    public class Box<T> {
        public T Value { get; set; }
    }
}
""",
            },
            new SourceFile
            {
                FilePath = "Main.cs",
                Content = """
namespace Agl {
    public class Main2 {
        private Toolbox.Box<int> box;
    }
}
""",
            });

        Assert.All(results, r => Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var main = results.Single(r => r.FileName == "Main2.java");
        Assert.Contains("import Toolbox.Box;", main.GeneratedCode);
        Assert.Contains("Box<Integer> box", main.GeneratedCode);
    }

    [Fact]
    public async Task MicrosoftNamespaceCompatTypeReference_AddsImport()
    {
        var results = await ConvertAsync(
            new SourceFile
            {
                FilePath = "UnitTestAssertException.cs",
                Content = """
namespace Microsoft.VisualStudio.TestTools.UnitTesting {
    public class UnitTestAssertException : System.Exception { }
}
""",
            },
            new SourceFile
            {
                FilePath = "Validate.cs",
                Content = """
namespace Microsoft.Msagl.UnitTests {
    public class Validate {
        public void M() {
            throw new Microsoft.VisualStudio.TestTools.UnitTesting.UnitTestAssertException();
        }
    }
}
""",
            });

        Assert.All(results, r => Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var main = results.Single(r => r.FileName == "Validate.java");
        Assert.Contains("import Microsoft.VisualStudio.TestTools.UnitTesting.UnitTestAssertException;", main.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("new UnitTestAssertException()", main.GeneratedCode, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<ConversionResult>> ConvertAsync(params SourceFile[] sourceFiles)
    {
        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
        var pipeline = new ProjectConversionPipeline(options);
        return await pipeline.ConvertProjectAsync(sourceFiles);
    }
}
