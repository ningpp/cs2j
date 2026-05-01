using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Lowering;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Tests.Java2;

public class LoweringPassTests
{
    private static ConversionContext MakeContext() =>
        new(new ConversionOptions(), new TypeMappingRegistry(new TypeMappingConfig()));

    [Fact]
    public void LowerProperty_ConvertsGetAccess()
    {
        var unit = new IrCompilationUnit();
        var cls = new IrClassDeclaration { Name = "Foo" };
        cls.Methods.Add(new IrMethodDeclaration
        {
            Name = "test", ReturnType = "String",
            Body = new IrBlockStatement
            {
                Statements =
                {
                    new IrReturnStatement
                    {
                        Expression = new IrCSharpPropertyAccessExpression
                        {
                            Target = new IrIdentifierExpression { Name = "obj" },
                            PropertyName = "Name",
                        }
                    }
                }
            },
        });
        unit.TypeDeclarations.Add(cls);

        var pass = new LowerProperty();
        var result = pass.Apply(unit, MakeContext());

        var methodBody = ((IrClassDeclaration)result.TypeDeclarations[0]).Methods[0].Body!;
        var retStmt = (IrReturnStatement)methodBody.Statements[0];
        var inv = Assert.IsType<IrInvocationExpression>(retStmt.Expression);
        Assert.Equal("getName", inv.MethodName);
    }

    [Fact]
    public void LowerUsing_ConvertsToTryFinally()
    {
        var unit = new IrCompilationUnit();
        var cls = new IrClassDeclaration { Name = "Foo" };
        cls.Methods.Add(new IrMethodDeclaration
        {
            Name = "test", ReturnType = "void",
            Body = new IrBlockStatement
            {
                Statements =
                {
                    new IrCSharpUsingStatement
                    {
                        Resource = new IrVariableDeclarationStatement { Type = "Stream", Name = "s" },
                        Body = new IrExpressionStatement { Expression = new IrIdentifierExpression { Name = "doWork" } },
                    }
                }
            },
        });
        unit.TypeDeclarations.Add(cls);

        var pass = new LowerUsing();
        var result = pass.Apply(unit, MakeContext());

        var methodBody = ((IrClassDeclaration)result.TypeDeclarations[0]).Methods[0].Body!;
        var tc = Assert.IsType<IrTryCatchStatement>(methodBody.Statements[0]);
        Assert.NotNull(tc.FinallyBody);
    }
}
