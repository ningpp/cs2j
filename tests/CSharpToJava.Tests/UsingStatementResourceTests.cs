using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

public class UsingStatementResourceTests
{
    private readonly ITestOutputHelper _out;
    public UsingStatementResourceTests(ITestOutputHelper output) { _out = output; }

    private static ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void UsingExistingLocal_EmitsResourceReference()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int Read(Stream stream) {
        using (stream) {
            return stream.ReadByte();
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (stream)", code);
        Assert.DoesNotContain("try ()", code);
    }

    [Fact]
    public void UsingFactoryExpression_IntroducesResourceVariable()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int Read(string fileName) {
        using (File.OpenRead(fileName)) {
            return 1;
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (InputStream _usingResource", code);
        Assert.Contains("= FileHelper.openRead(fileName))", code);
        Assert.DoesNotContain("try ()", code);
    }

    [Fact]
    public void UsingDeclaration_WrapsRemainderOfScope()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int Read(string fileName) {
        using var stream = File.OpenRead(fileName);
        return stream.ReadByte();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (InputStream stream = FileHelper.openRead(fileName))", code);
        Assert.Contains("return stream.readByte();", code);
        Assert.DoesNotContain("var stream = FileHelper.openRead(fileName);", code);
    }

    [Fact]
    public void UsingDeclarationInsideNestedBlock_DoesNotWrapOuterStatements()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int Read(string fileName) {
        {
            using var stream = File.OpenRead(fileName);
            return stream.ReadByte();
        }
        return 42;
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (InputStream stream = FileHelper.openRead(fileName))", code);
        Assert.Contains("return stream.readByte();", code);
        Assert.Contains("return 42;", code);
        Assert.DoesNotContain("return stream.readByte();\n        }\n        return 42;", code);
    }

    [Fact]
    public void UsingDeclarationInsideIfBlock_WrapsOnlyIfBlockRemainder()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int Read(string fileName, bool enabled) {
        if (enabled) {
            using var stream = File.OpenRead(fileName);
            return stream.ReadByte();
        }
        return -1;
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("if (enabled)", code);
        Assert.Contains("try (InputStream stream = FileHelper.openRead(fileName))", code);
        Assert.Contains("return stream.readByte();", code);
        Assert.Contains("return -1;", code);
        Assert.DoesNotContain("return stream.readByte();\n        }\n        return -1;", code);
    }

    [Fact]
    public void SingleUsingDeclarationWithMultipleVariables_EmitsAllResources()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int ReadBoth(string left, string right) {
        using Stream first = File.OpenRead(left), second = File.OpenRead(right);
        return first.ReadByte() + second.ReadByte();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (StreamWrapper first = StreamWrapper.of(FileHelper.openRead(left)); StreamWrapper second = StreamWrapper.of(FileHelper.openRead(right)))", code);
        Assert.Contains("return first.readByte() + second.readByte();", code);
        Assert.DoesNotContain("StreamWrapper first = StreamWrapper.of(FileHelper.openRead(left));\n", code);
        Assert.DoesNotContain("StreamWrapper second = StreamWrapper.of(FileHelper.openRead(right));\n", code);
    }

    [Fact]
    public void ConsecutiveUsingDeclarations_ShareTryResourceList()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int ReadBoth(string left, string right) {
        using var first = File.OpenRead(left);
        using var second = File.OpenRead(right);
        return first.ReadByte() + second.ReadByte();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (InputStream first = FileHelper.openRead(left); InputStream second = FileHelper.openRead(right))", code);
        Assert.Contains("return first.readByte() + second.readByte();", code);
        Assert.DoesNotContain("var second = FileHelper.openRead(right);", code);
    }

    [Fact]
    public void LaterUsingDeclarationAfterNormalStatement_NestsInsideExistingUsingScope()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int ReadBoth(string left, string right) {
        using var first = File.OpenRead(left);
        int prefix = first.ReadByte();
        using var second = File.OpenRead(right);
        return prefix + second.ReadByte();
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (InputStream first = FileHelper.openRead(left))", code);
        Assert.Contains("int prefix = first.readByte();", code);
        Assert.Contains("try (InputStream second = FileHelper.openRead(right))", code);
        Assert.Contains("return prefix + second.readByte();", code);
        Assert.DoesNotContain("var second = FileHelper.openRead(right);", code);
    }

    [Fact]
    public void StackedUsingFactoryExpressions_UseDistinctSyntheticResourceNames()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int ReadBoth(string left, string right) {
        using (File.OpenRead(left))
        using (File.OpenRead(right)) {
            return 1;
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (InputStream _usingResource", code);
        Assert.Contains("; InputStream _usingResource", code);
        Assert.DoesNotContain("try ()", code);
        Assert.DoesNotContain("InputStream _usingResource = FileHelper.openRead(left); InputStream _usingResource = FileHelper.openRead(right)", code);
    }

    [Fact]
    public void UsingObjectCreationExpression_IntroducesTypedSyntheticResource()
    {
        var result = Convert("""
using System.IO;
class Sample {
    static int Read() {
        using (new MemoryStream()) {
            return 7;
        }
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("try (MemoryStream _usingResource", code);
        Assert.Contains("= new MemoryStream())", code);
        Assert.Contains("return 7;", code);
        Assert.DoesNotContain("try ()", code);
    }
}
