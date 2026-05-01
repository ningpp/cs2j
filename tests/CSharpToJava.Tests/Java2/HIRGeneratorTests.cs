using Xunit;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.HIR;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Java2.CodeGen;
using CSharpToJava.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests.Java2;

public class HIRGeneratorTests
{
    private static (CSharpToJavaHIRGenerator, ConversionContext, CompilationUnitSyntax) Setup(string code)
    {
        var options = new ConversionOptions();
        var typeMappings = new TypeMappingRegistry(new TypeMappingConfig());
        var context = new ConversionContext(options, typeMappings);
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("Test", new[] { tree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
            });
        context.SemanticModel = compilation.GetSemanticModel(tree);
        context.ProjectCompilation = compilation;
        var root = (CompilationUnitSyntax)tree.GetRoot();
        return (new CSharpToJavaHIRGenerator(), context, root);
    }

    [Fact]
    public void EmptyClass_GeneratesHIR()
    {
        var (generator, context, root) = Setup("class Empty { }");
        var unit = generator.Generate(root, context);

        Assert.NotNull(unit);
        Assert.Single(unit.TypeDeclarations);
        var cls = Assert.IsType<IrClassDeclaration>(unit.TypeDeclarations[0]);
        Assert.Equal("Empty", cls.Name);
    }

    [Fact]
    public void ClassWithIntField_GeneratesHIRWithStructuredField()
    {
        var (generator, context, root) = Setup("class Point { int x = 5; }");
        var unit = generator.Generate(root, context);
        var cls = (IrClassDeclaration)unit.TypeDeclarations[0];

        Assert.Single(cls.Fields);
        Assert.Equal("int", cls.Fields[0].Type);
        Assert.Equal("x", cls.Fields[0].Name);
        Assert.Equal("5", cls.Fields[0].Initializer);
    }

    [Fact]
    public void MethodWithBinaryExpression_GeneratesStructuredTree()
    {
        var (generator, context, root) = Setup("class Calc { int Add(int a, int b) { return a + b; } }");
        var unit = generator.Generate(root, context);
        var cls = (IrClassDeclaration)unit.TypeDeclarations[0];
        var method = cls.Methods[0];

        Assert.Single(method.Body!.Statements);
        var retStmt = Assert.IsType<IrReturnStatement>(method.Body.Statements[0]);
        var binExpr = Assert.IsType<IrBinaryExpression>(retStmt.Expression);
        Assert.Equal(IrBinaryOp.Add, binExpr.Operator);
        Assert.IsType<IrIdentifierExpression>(binExpr.Left);
        Assert.IsType<IrIdentifierExpression>(binExpr.Right);
    }

    [Fact]
    public void SimpleClass_RoundtripsThroughCodeGen()
    {
        var code = @"
class Calculator {
    int Add(int a, int b) {
        return a + b;
    }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var codeGen = new JavaCodeGenerator();
        var java = codeGen.Generate(unit);

        Assert.Contains("class Calculator", java);
        Assert.Contains("int Add", java);
        Assert.Contains("int a", java);
        Assert.Contains("int b", java);
        Assert.Contains("return a + b;", java);
    }
}
