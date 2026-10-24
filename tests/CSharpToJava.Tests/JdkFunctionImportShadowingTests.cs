using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Tests;

/// <summary>
/// java.util.function.* 标准单类型导入对同包源内同名类型的遮蔽回归：
/// 真实链 dotnet-xml 的 MS.Internal.Xml.XPath 包声明 Function 类（含嵌套枚举
/// FunctionType），包内其他文件（BooleanFunctions 等）以限定名
/// Function.FunctionType 引用它。AddStandardJdkImports 无条件种下
/// import java.util.function.Function 后，JLS 名称解析里单类型导入优先于
/// 同包其他编译单元声明的类型，Function 被劫持到 JDK 接口（mvn：找不到符号
/// 类 FunctionType 位置: 接口 java.util.function.Function，11 处）。
/// 修复：组装导入时若当前命名空间声明了同简名顶级类型则丢弃该 JDK 导入。
/// </summary>
public class JdkFunctionImportShadowingTests
{
    private const string Source = """
namespace MS.Internal.Xml.XPath
{
    public class Function
    {
        public enum FunctionType { FuncLast, FuncPosition }
        public FunctionType TypeOfFunction() => FunctionType.FuncLast;
    }
    public class BooleanFunctions
    {
        private Function.FunctionType _funcType = Function.FunctionType.FuncLast;
    }
}
""";

    private static (TypeMappingService Mapper, Microsoft.CodeAnalysis.CSharp.CSharpCompilation Compilation) CreateService(string currentNamespace)
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(Source);
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "jdk-function-shadowing",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        var service = new TypeMappingService(
            new ConversionOptions(),
            new TypeMappingRegistry(new TypeMappingConfig()),
            new DiagnosticCollector(),
            new HashSet<string>(),
            () => currentNamespace,
            () => compilation.GlobalNamespace,
            _ => false);

        return (service, compilation);
    }

    [Fact]
    public void CurrentNamespaceDeclaresType_TrueForSameNamespaceFunction()
    {
        var (mapper, _) = CreateService("MS.Internal.Xml.XPath");

        Assert.True(mapper.CurrentNamespaceDeclaresType("Function"));
        // 不存在的简名 / 无关简名不得误判
        Assert.False(mapper.CurrentNamespaceDeclaresType("Consumer"));
        Assert.False(mapper.CurrentNamespaceDeclaresType("NotDeclared"));
    }

    [Fact]
    public void CurrentNamespaceDeclaresType_FalseInOtherNamespace()
    {
        var (mapper, _) = CreateService("MS.Internal.Xml.SomeOther");

        Assert.False(mapper.CurrentNamespaceDeclaresType("Function"));
    }

    private static JavaCompilationUnit CreateUnitWithStandardFunctionImports()
    {
        var unit = new JavaCompilationUnit("MS.Internal.Xml.XPath");
        unit.TypeDeclarations.Add(new JavaClassDeclaration { Name = "BooleanFunctions" });
        unit.Imports.Add(new JavaImport("java.util.function.Function"));
        unit.Imports.Add(new JavaImport("java.util.function.BiFunction"));
        unit.Imports.Add(new JavaImport("java.util.function.Consumer"));
        unit.Imports.Add(new JavaImport("java.util.function.Predicate"));
        return unit;
    }

    [Fact]
    public void Drop_ShadowedFunctionImport_RemovedFromCollidingNamespace()
    {
        var (mapper, _) = CreateService("MS.Internal.Xml.XPath");
        var unit = CreateUnitWithStandardFunctionImports();

        TypeGroupResolver.DropJdkFunctionImportsShadowingSourceTypes(unit, mapper);

        var emitted = unit.ToString("");
        Assert.DoesNotContain("import java.util.function.Function;", emitted, StringComparison.Ordinal);
        // 无冲突的其余标准导入保持不变
        Assert.Contains("import java.util.function.Consumer;", emitted, StringComparison.Ordinal);
        Assert.Contains("import java.util.function.Predicate;", emitted, StringComparison.Ordinal);
    }

    [Fact]
    public void Drop_NoCollision_AllStandardImportsKept()
    {
        var (mapper, _) = CreateService("MS.Internal.Xml.SomeOther");
        var unit = CreateUnitWithStandardFunctionImports();

        TypeGroupResolver.DropJdkFunctionImportsShadowingSourceTypes(unit, mapper);

        var emitted = unit.ToString("");
        Assert.Contains("import java.util.function.Function;", emitted, StringComparison.Ordinal);
        Assert.Contains("import java.util.function.Consumer;", emitted, StringComparison.Ordinal);
    }
}
