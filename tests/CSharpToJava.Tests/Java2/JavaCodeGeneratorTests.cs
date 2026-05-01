// tests/CSharpToJava.Tests/Java2/JavaCodeGeneratorTests.cs
using Xunit;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Java2.CodeGen;

namespace CSharpToJava.Tests.Java2;

public class JavaCodeGeneratorTests
{
    [Fact]
    public void EmptyClass_GeneratesCorrectly()
    {
        var unit = new IrCompilationUnit
        {
            Package = "com.example",
            TypeDeclarations =
            {
                new IrClassDeclaration
                {
                    Modifiers = IrModifiers.Public,
                    Name = "Empty",
                }
            }
        };
        var gen = new JavaCodeGenerator();
        var code = gen.Generate(unit);
        Assert.Contains("package com.example;", code);
        Assert.Contains("public class Empty {", code);
        Assert.Contains("}", code);
    }

    [Fact]
    public void ClassWithField_GeneratesFieldDeclaration()
    {
        var unit = new IrCompilationUnit
        {
            TypeDeclarations =
            {
                new IrClassDeclaration
                {
                    Modifiers = IrModifiers.Public,
                    Name = "Point",
                    Fields =
                    {
                        new IrFieldDeclaration
                        {
                            Modifiers = IrModifiers.Private,
                            Type = "int",
                            Name = "x",
                        }
                    }
                }
            }
        };
        var gen = new JavaCodeGenerator();
        var code = gen.Generate(unit);
        Assert.Contains("private int x;", code);
    }
}
