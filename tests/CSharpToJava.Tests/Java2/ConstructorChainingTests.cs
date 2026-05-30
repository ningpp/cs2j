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

public class ConstructorChainingTests
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
    public void ConstructorWithThisInitializer_GeneratesThisCall()
    {
        var code = @"
class ClusterDef {
    private static int nextClusterId = 0;
    internal ClusterDef() : this(0.0, 0.0) { }
    internal ClusterDef(double minSizeX, double minSizeY) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var cls = Assert.IsType<IrClassDeclaration>(unit.TypeDeclarations[0]);

        var parameterlessCtor = cls.Constructors[0];
        Assert.NotNull(parameterlessCtor.Initializer);
        Assert.True(parameterlessCtor.Initializer.IsThisCall);
        Assert.Equal(2, parameterlessCtor.Initializer.Arguments.Count);
    }

    [Fact]
    public void ConstructorWithThisInitializer_RoundtripsThroughCodeGen()
    {
        var code = @"
class ClusterDef {
    internal ClusterDef() : this(0.0, 0.0) { }
    internal ClusterDef(double minSizeX, double minSizeY) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var codeGen = new JavaCodeGenerator();
        var java = codeGen.Generate(unit);

        Assert.Contains("this(0.0, 0.0);", java);
    }

    [Fact]
    public void ConstructorWithBaseInitializer_GeneratesSuperCall()
    {
        var code = @"
class Base {
    public Base(int x) { }
}
class Derived : Base {
    public Derived() : base(42) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var cls = (IrClassDeclaration)unit.TypeDeclarations[1];

        var derivedCtor = cls.Constructors[0];
        Assert.NotNull(derivedCtor.Initializer);
        Assert.False(derivedCtor.Initializer.IsThisCall);
        Assert.Single(derivedCtor.Initializer.Arguments);
    }

    [Fact]
    public void ConstructorWithBaseInitializer_RoundtripsThroughCodeGen()
    {
        var code = @"
class Base {
    public Base(int x) { }
}
class Derived : Base {
    public Derived() : base(42) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var codeGen = new JavaCodeGenerator();
        var java = codeGen.Generate(unit);

        Assert.Contains("super(42);", java);
    }

    [Fact]
    public void ConstructorWithoutInitializer_HasNullInitializer()
    {
        var code = @"
class Foo {
    public Foo(int x) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var cls = (IrClassDeclaration)unit.TypeDeclarations[0];

        Assert.Null(cls.Constructors[0].Initializer);
    }

    [Fact]
    public void ConstructorWithThisAndArguments_GeneratesThisWithArgs()
    {
        var code = @"
class Rect {
    public Rect() : this(10, 20) { }
    public Rect(int w, int h) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var cls = (IrClassDeclaration)unit.TypeDeclarations[0];

        var init = cls.Constructors[0].Initializer;
        Assert.NotNull(init);
        Assert.True(init.IsThisCall);
        Assert.Equal(2, init.Arguments.Count);
    }

    [Fact]
    public void ConstructorWithThisAndArguments_RoundtripsThroughCodeGen()
    {
        var code = @"
class Rect {
    public Rect() : this(10, 20) { }
    public Rect(int w, int h) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var codeGen = new JavaCodeGenerator();
        var java = codeGen.Generate(unit);

        Assert.Contains("this(10, 20);", java);
    }

    [Fact]
    public void ConstructorWithBaseAndVariableArgs_GeneratesSuperWithArgs()
    {
        var code = @"
class Base {
    public Base(string name) { }
}
class Child : Base {
    public Child(string n) : base(n) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var cls = (IrClassDeclaration)unit.TypeDeclarations[1];

        var init = cls.Constructors[0].Initializer;
        Assert.NotNull(init);
        Assert.False(init.IsThisCall);
        Assert.Single(init.Arguments);
    }

    [Fact]
    public void ConstructorWithBaseAndVariableArgs_RoundtripsThroughCodeGen()
    {
        var code = @"
class Base {
    public Base(string name) { }
}
class Child : Base {
    public Child(string n) : base(n) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var codeGen = new JavaCodeGenerator();
        var java = codeGen.Generate(unit);

        Assert.Contains("super(n);", java);
    }

    [Fact]
    public void ConstructorWithThisAndBody_BothEmitted()
    {
        var code = @"
class Foo {
    private int field;
    public Foo() : this(5) { field = 99; }
    public Foo(int x) { field = x; }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var codeGen = new JavaCodeGenerator();
        var java = codeGen.Generate(unit);

        Assert.Contains("this(5);", java);
    }

    [Fact]
    public void ConstructorWithThisChaining_MultipleLevels()
    {
        var code = @"
class Multi {
    public Multi() : this(1) { }
    public Multi(int a) : this(a, 2) { }
    public Multi(int a, int b) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var cls = (IrClassDeclaration)unit.TypeDeclarations[0];

        Assert.NotNull(cls.Constructors[0].Initializer);
        Assert.True(cls.Constructors[0].Initializer.IsThisCall);
        Assert.Equal(1, cls.Constructors[0].Initializer.Arguments.Count);

        Assert.NotNull(cls.Constructors[1].Initializer);
        Assert.True(cls.Constructors[1].Initializer.IsThisCall);
        Assert.Equal(2, cls.Constructors[1].Initializer.Arguments.Count);

        Assert.Null(cls.Constructors[2].Initializer);
    }

    [Fact]
    public void ConstructorWithThisChaining_MultipleLevels_RoundtripsThroughCodeGen()
    {
        var code = @"
class Multi {
    public Multi() : this(1) { }
    public Multi(int a) : this(a, 2) { }
    public Multi(int a, int b) { }
}";
        var (generator, context, root) = Setup(code);
        var unit = generator.Generate(root, context);
        var codeGen = new JavaCodeGenerator();
        var java = codeGen.Generate(unit);

        Assert.Contains("this(1);", java);
        Assert.Contains("this(a, 2);", java);
    }

    [Fact]
    public void IrConstructorInitializer_StoresStructuredData()
    {
        var init = new IrConstructorInitializer
        {
            IsThisCall = true,
        };
        init.Arguments.Add(new IrLiteralExpression { Value = "0" });
        init.Arguments.Add(new IrLiteralExpression { Value = "0" });

        var ctor = new IrConstructorDeclaration
        {
            TypeName = "Foo",
            Modifiers = IrModifiers.Public,
            Initializer = init,
        };

        Assert.NotNull(ctor.Initializer);
        Assert.True(ctor.Initializer.IsThisCall);
        Assert.Equal(2, ctor.Initializer.Arguments.Count);
    }

    [Fact]
    public void MemberWriter_WriteConstructor_WithThisInitializer()
    {
        var init = new IrConstructorInitializer { IsThisCall = true };
        init.Arguments.Add(new IrLiteralExpression { Value = "0" });
        init.Arguments.Add(new IrLiteralExpression { Value = "0" });

        var ctor = new IrConstructorDeclaration
        {
            TypeName = "Foo",
            Modifiers = IrModifiers.Public,
            Initializer = init,
        };
        var w = new IndentedWriter();
        var memberWriter = new MemberWriter();
        memberWriter.WriteConstructor(ctor, w);
        var output = w.ToString();

        Assert.Contains("this(0, 0);", output);
    }

    [Fact]
    public void MemberWriter_WriteConstructor_WithSuperInitializer()
    {
        var init = new IrConstructorInitializer { IsThisCall = false };
        init.Arguments.Add(new IrLiteralExpression { Value = "42" });

        var ctor = new IrConstructorDeclaration
        {
            TypeName = "Foo",
            Modifiers = IrModifiers.Public,
            Initializer = init,
        };
        var w = new IndentedWriter();
        var memberWriter = new MemberWriter();
        memberWriter.WriteConstructor(ctor, w);
        var output = w.ToString();

        Assert.Contains("super(42);", output);
    }

    [Fact]
    public void MemberWriter_WriteConstructor_WithoutInitializer()
    {
        var ctor = new IrConstructorDeclaration
        {
            TypeName = "Foo",
            Modifiers = IrModifiers.Public,
        };
        var w = new IndentedWriter();
        var memberWriter = new MemberWriter();
        memberWriter.WriteConstructor(ctor, w);
        var output = w.ToString();

        Assert.DoesNotContain("this(", output);
        Assert.DoesNotContain("super(", output);
    }

    [Fact]
    public void MemberWriter_WriteConstructor_WithVariableArgInitializer()
    {
        var init = new IrConstructorInitializer { IsThisCall = false };
        init.Arguments.Add(new IrIdentifierExpression { Name = "n" });

        var ctor = new IrConstructorDeclaration
        {
            TypeName = "Child",
            Modifiers = IrModifiers.Public,
            Initializer = init,
        };
        var w = new IndentedWriter();
        var memberWriter = new MemberWriter();
        memberWriter.WriteConstructor(ctor, w);
        var output = w.ToString();

        Assert.Contains("super(n);", output);
    }
}
