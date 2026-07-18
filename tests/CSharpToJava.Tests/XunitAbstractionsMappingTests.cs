using System.Reflection;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

public class XunitAbstractionsMappingTests
{
    [Fact]
    public void MappedBaseClass_AddsImport()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
using System.Collections;

public class Sample : ArrayList
{
}
""",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });

        var code = result.GeneratedCode ?? "";

        Assert.Contains("extends CSharpArrayList", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.CSharpArrayList;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ImplementedInterface_MappedType_AddsImport()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
using Xunit.Abstractions;
using Xunit.Sdk;

public class XmlInlineDataDiscoverer : IDataDiscoverer
{
    public System.Collections.Generic.IEnumerable<object[]> GetData(IAttributeInfo dataAttribute, IMethodInfo testMethod)
    {
        return null;
    }

    public bool SupportsDiscoveryEnumeration(IAttributeInfo dataAttribute, IMethodInfo testMethod)
    {
        return true;
    }
}
""",
            FileName = "XmlInlineDataDiscoverer.cs",
            Options = new ConversionOptions(),
        });

        var code = result.GeneratedCode ?? "";

        Assert.Contains("implements IDataDiscoverer", code, StringComparison.Ordinal);
        Assert.Contains("import csharp.xunit.Abstractions.IDataDiscoverer;", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectMode_ImplementedInterface_MappedType_AddsImport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = Path.Combine(tempDir, "XmlInlineDataDiscoverer.cs");
            await File.WriteAllTextAsync(sourcePath, """
using Xunit.Abstractions;

public class XmlInlineDataDiscoverer : IDataDiscoverer
{
    public System.Collections.Generic.IEnumerable<object[]> GetData(IAttributeInfo dataAttribute, IMethodInfo testMethod)
    {
        return null;
    }

    public bool SupportsDiscoveryEnumeration(IAttributeInfo dataAttribute, IMethodInfo testMethod)
    {
        return true;
    }
}
""");

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, new ConversionOptions());
            var result = Assert.Single(results);
            var code = result.GeneratedCode ?? "";

            Assert.Contains("implements IDataDiscoverer", code, StringComparison.Ordinal);
            Assert.Contains("import csharp.xunit.Abstractions.IDataDiscoverer;", code, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ProjectMode_TwoClassesInFile_Implemented_MappedType_AddsImport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var sourcePath = Path.Combine(tempDir, "XunitRunner.cs");
            await File.WriteAllTextAsync(sourcePath, """
using Xunit.Abstractions;
using Xunit.Sdk;

namespace OLEDB.Test.ModuleCore
{
    public class XmlInlineDataDiscoverer : IDataDiscoverer
    {
        public System.Collections.Generic.IEnumerable<object[]> GetData(IAttributeInfo dataAttribute, IMethodInfo testMethod)
        {
            return null;
        }

        public bool SupportsDiscoveryEnumeration(IAttributeInfo dataAttribute, IMethodInfo testMethod)
        {
            return true;
        }
    }

    public sealed class XmlTestsAttribute : DataAttribute
    {
        public XmlTestsAttribute(string methodName) { }

        public override System.Collections.Generic.IEnumerable<object[]> GetData(System.Reflection.MethodInfo testMethod)
        {
            return null;
        }
    }
}
""");

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, new ConversionOptions());
            var discoverer = results.Single(r => r.FileName == "XmlInlineDataDiscoverer.java");
            var code = discoverer.GeneratedCode ?? "";

            Assert.Contains("implements IDataDiscoverer", code, StringComparison.Ordinal);
            Assert.Contains("import csharp.xunit.Abstractions.IDataDiscoverer;", code, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResolvedInterfaceInNamespaceWithTwoClasses_Implemented_MappedType_AddsImport()
    {
        var source = """
using Xunit.Abstractions;
using Xunit.Sdk;

namespace OLEDB.Test.ModuleCore
{
    public class XmlInlineDataDiscoverer : IDataDiscoverer
    {
        public System.Collections.Generic.IEnumerable<object[]> GetData(IAttributeInfo dataAttribute, IMethodInfo testMethod)
        {
            return null;
        }

        public bool SupportsDiscoveryEnumeration(IAttributeInfo dataAttribute, IMethodInfo testMethod)
        {
            return true;
        }
    }

    public sealed class XmlTestsAttribute : DataAttribute
    {
        public XmlTestsAttribute(string methodName) { }

        public override System.Collections.Generic.IEnumerable<object[]> GetData(System.Reflection.MethodInfo testMethod)
        {
            return null;
        }
    }
}
""";
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "XunitRunner.cs");
        var xunitAbstractionsAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "xunit.abstractions")
            ?? Assembly.Load("xunit.abstractions");
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IEnumerable<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Reflection.MethodInfo).Assembly.Location),
            MetadataReference.CreateFromFile(xunitAbstractionsAssembly.Location),
        };
        var compilation = CSharpCompilation.Create("TempAssembly", new[] { syntaxTree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(compilation);
        var discoverer = results.Single(r => r.FileName == "XmlInlineDataDiscoverer.java");
        var code = discoverer.GeneratedCode ?? "";

        Assert.Contains("implements IDataDiscoverer", code, StringComparison.Ordinal);
        Assert.Contains("import csharp.xunit.Abstractions.IDataDiscoverer;", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvedInterfaceInNamespace_Implemented_MappedType_AddsImport()
    {
        var source = """
using Xunit.Abstractions;

namespace OLEDB.Test.ModuleCore
{
    public class XmlInlineDataDiscoverer : IDataDiscoverer
    {
        public System.Collections.Generic.IEnumerable<object[]> GetData(IAttributeInfo dataAttribute, IMethodInfo testMethod)
        {
            return null;
        }

        public bool SupportsDiscoveryEnumeration(IAttributeInfo dataAttribute, IMethodInfo testMethod)
        {
            return true;
        }
    }
}
""";
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "XmlInlineDataDiscoverer.cs");
        var xunitAbstractionsAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "xunit.abstractions")
            ?? Assembly.Load("xunit.abstractions");
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IEnumerable<>).Assembly.Location),
            MetadataReference.CreateFromFile(xunitAbstractionsAssembly.Location),
        };
        var compilation = CSharpCompilation.Create("TempAssembly", new[] { syntaxTree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(compilation);
        var result = Assert.Single(results);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("implements IDataDiscoverer", code, StringComparison.Ordinal);
        Assert.Contains("import csharp.xunit.Abstractions.IDataDiscoverer;", code, StringComparison.Ordinal);
    }

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

    [Fact]
    public async Task UnresolvedInterface_Implemented_MappedType_AddsImport()
    {
        var source = """
using Xunit.Abstractions;

public class XmlInlineDataDiscoverer : IDataDiscoverer
{
    public System.Collections.Generic.IEnumerable<object[]> GetData(IAttributeInfo dataAttribute, IMethodInfo testMethod)
    {
        return null;
    }

    public bool SupportsDiscoveryEnumeration(IAttributeInfo dataAttribute, IMethodInfo testMethod)
    {
        return true;
    }
}
""";
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "XmlInlineDataDiscoverer.cs");
        // Deliberately omit xunit.abstractions reference so IDataDiscoverer is unresolved.
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IEnumerable<>).Assembly.Location),
        };
        var compilation = CSharpCompilation.Create("TempAssembly", new[] { syntaxTree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(compilation);
        var result = Assert.Single(results);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("implements IDataDiscoverer", code, StringComparison.Ordinal);
        Assert.Contains("import csharp.xunit.Abstractions.IDataDiscoverer;", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvedInterface_Implemented_MappedType_AddsImport()
    {
        var source = """
using Xunit.Abstractions;

public class XmlInlineDataDiscoverer : IDataDiscoverer
{
    public System.Collections.Generic.IEnumerable<object[]> GetData(IAttributeInfo dataAttribute, IMethodInfo testMethod)
    {
        return null;
    }

    public bool SupportsDiscoveryEnumeration(IAttributeInfo dataAttribute, IMethodInfo testMethod)
    {
        return true;
    }
}
""";
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "XmlInlineDataDiscoverer.cs");
        var xunitAbstractionsAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "xunit.abstractions")
            ?? Assembly.Load("xunit.abstractions");
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.IEnumerable<>).Assembly.Location),
            MetadataReference.CreateFromFile(xunitAbstractionsAssembly.Location),
        };
        var compilation = CSharpCompilation.Create("TempAssembly", new[] { syntaxTree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = await pipeline.ConvertProjectAsync(compilation);
        var result = Assert.Single(results);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("implements IDataDiscoverer", code, StringComparison.Ordinal);
        Assert.Contains("import csharp.xunit.Abstractions.IDataDiscoverer;", code, StringComparison.Ordinal);
    }
}
