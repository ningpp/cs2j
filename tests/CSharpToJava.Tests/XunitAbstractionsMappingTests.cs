using System.Reflection;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

public class XunitAbstractionsMappingTests
{

    [Fact]
    public async Task ResolvedXunitCoreInterface_Implemented_MappedType_AddsImport()
    {
        var source = """
using Xunit.Sdk;

public class XmlInlineDataDiscoverer : IDataDiscoverer
{
    public System.Collections.Generic.IEnumerable<object[]> GetData(Xunit.Abstractions.IAttributeInfo dataAttribute, Xunit.Abstractions.IMethodInfo testMethod)
    {
        return null;
    }

    public bool SupportsDiscoveryEnumeration(Xunit.Abstractions.IAttributeInfo dataAttribute, Xunit.Abstractions.IMethodInfo testMethod)
    {
        return true;
    }
}
""";
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "XmlInlineDataDiscoverer.cs");
        var xunitCoreAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "xunit.core")
            ?? Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(typeof(Assembly).Assembly.Location)!, "..", "..", "xunit.core.dll"));
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IEnumerable<>).Assembly.Location),
            MetadataReference.CreateFromFile(xunitCoreAssembly.Location),
        };
        var compilation = CSharpCompilation.Create("TempAssembly", new[] { syntaxTree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(compilation);
        var result = Assert.Single(results);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("implements IDataDiscoverer", code, StringComparison.Ordinal);
        Assert.Contains("import csharp.xunit.Sdk.IDataDiscoverer;", code, StringComparison.Ordinal);
    }
}
