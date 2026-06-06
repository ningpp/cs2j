using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class FileScopedNamespaceTests
{
    /// <summary>
    /// 验证文件范围命名空间 (namespace Foo;) 能正确转换为 Java package 声明
    /// </summary>
    [Fact]
    public void FileScopedNamespace_GeneratesPackageDeclaration()
    {
        var result = Convert(@"
namespace Demo;

class Program
{
    public static void Main()
    {
        System.Console.WriteLine(""Hello"");
    }
}");

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("package Demo;", result.GeneratedCode);
        Assert.Contains("public class Program", result.GeneratedCode);
        Assert.Contains("System.out.println", result.GeneratedCode);
    }

    /// <summary>
    /// 验证文件范围命名空间内的类成员（字段、方法）被正确转换
    /// </summary>
    [Fact]
    public void FileScopedNamespace_ClassMembersConverted()
    {
        var result = Convert(@"
namespace MyApp;

class Service
{
    private int _count;

    public int GetCount() => _count;
}");

        Assert.True(result.Success);
        Assert.Contains("package MyApp;", result.GeneratedCode);
        Assert.Contains("public class Service", result.GeneratedCode);
        Assert.Contains("private int _count", result.GeneratedCode);
        Assert.Contains("public int getCount()", result.GeneratedCode);
    }

    /// <summary>
    /// 验证文件范围命名空间 + unsafe 方法的组合转换
    /// </summary>
    [Fact]
    public void FileScopedNamespace_WithUnsafeMethod_PointersMappedToMemorySegment()
    {
        var result = Convert(@"
namespace Demo;

class Program
{
    public static unsafe int GetBytes(char* pStr, int count, byte* bytes, int start)
    {
        return System.Text.Encoding.UTF8.GetBytes(pStr + start, count, bytes, 160);
    }
}");

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("package Demo;", result.GeneratedCode);
        Assert.Contains("MemorySegment pStr", result.GeneratedCode);
        Assert.Contains("MemorySegment bytes", result.GeneratedCode);
        Assert.DoesNotContain("unsafe", result.GeneratedCode);
    }

    /// <summary>
    /// 验证文件范围命名空间内的常量字段转换
    /// </summary>
    [Fact]
    public void FileScopedNamespace_ConstFieldsConverted()
    {
        var result = Convert(@"
namespace Demo;

class Config
{
    private const short MaxRetries = 40;
    private const short MaxBytes = 4;
}");

        Assert.True(result.Success);
        Assert.Contains("package Demo;", result.GeneratedCode);
        Assert.Contains("private static final short MaxRetries", result.GeneratedCode);
        Assert.Contains("private static final short MaxBytes", result.GeneratedCode);
    }

    /// <summary>
    /// 验证文件范围命名空间内 using 指令的转换
    /// </summary>
    [Fact]
    public void FileScopedNamespace_WithUsingDirective()
    {
        var result = Convert(@"
namespace Demo;

using System.Text;

class Encoder
{
    public string Encode() => Encoding.UTF8.GetString(new byte[0]);
}");

        Assert.True(result.Success);
        Assert.Contains("package Demo;", result.GeneratedCode);
        Assert.Contains("public class Encoder", result.GeneratedCode);
    }

    /// <summary>
    /// 验证文件范围命名空间内枚举的转换
    /// </summary>
    [Fact]
    public void FileScopedNamespace_EnumConverted()
    {
        var result = Convert(@"
namespace Demo;

enum Color
{
    Red,
    Green,
    Blue
}");

        Assert.True(result.Success);
        Assert.Contains("package Demo;", result.GeneratedCode);
        Assert.Contains("enum Color", result.GeneratedCode);
    }

    /// <summary>
    /// 验证块作用域命名空间仍然正常工作（回归测试）
    /// </summary>
    [Fact]
    public void BlockScopedNamespace_StillWorks()
    {
        var result = Convert(@"
namespace Demo
{
    class Program
    {
        public static void Main()
        {
            System.Console.WriteLine(""Hello"");
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("package Demo;", result.GeneratedCode);
        Assert.Contains("public class Program", result.GeneratedCode);
    }

    /// <summary>
    /// 验证文件范围命名空间内嵌套命名空间的转换
    /// </summary>
    [Fact]
    public void FileScopedNamespace_WithNestedNamespace()
    {
        var result = Convert(@"
namespace Outer.Inner;

class Helper
{
    public void DoWork() { }
}");

        Assert.True(result.Success);
        Assert.Contains("package Outer.Inner;", result.GeneratedCode);
        Assert.Contains("public class Helper", result.GeneratedCode);
    }

    /// <summary>
    /// 验证完整的 test_input.cs 场景（文件范围命名空间 + unsafe + 指针 + 常量）
    /// </summary>
    [Fact]
    public void FileScopedNamespace_FullUnsafeScenario()
    {
        var result = Convert(@"
namespace Demo;

using System.Text;

class Program
{
    private const short c_MaxAsciiCharsReallocate = 40;
    private const short c_MaxUnicodeCharsReallocate = 40;
    private const short c_MaxUTF_8BytesPerUnicodeChar = 4;

    public static unsafe int GetBytes(char* pStr, int count, byte* bytes, int start)
    {
        int i = start;
        return Encoding.UTF8.GetBytes(pStr + i, count, bytes,
            c_MaxUnicodeCharsReallocate * c_MaxUTF_8BytesPerUnicodeChar);
    }

    public static void Main()
    {
        string str = ""Hello, World!"";
        System.Console.WriteLine(str);
    }
}");

        Assert.True(result.Success, $"Conversion failed: {string.Join(", ", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("package Demo;", result.GeneratedCode);
        Assert.Contains("public class Program", result.GeneratedCode);
        Assert.Contains("MemorySegment pStr", result.GeneratedCode);
        Assert.Contains("MemorySegment bytes", result.GeneratedCode);
        Assert.Contains("private static final short", result.GeneratedCode);
        Assert.Contains("System.out.println", result.GeneratedCode);
        Assert.DoesNotContain("unsafe", result.GeneratedCode);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
