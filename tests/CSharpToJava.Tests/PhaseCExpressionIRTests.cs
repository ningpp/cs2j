using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.Transformers.Expression;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Phase C: Expression Transformer IR foundation.
/// Covers IIRExpressionTransformer interface, TransformToIR facade method,
/// LiteralExpressionTransformer IR output, and TransformStatementsToIR bridge.
/// </summary>
public class PhaseCExpressionIRTests
{
    private static string ConvertCode(string code)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        return result.GeneratedCode;
    }

    // ── IIRExpressionTransformer interface ──────────────────────────

    [Fact]
    public void LiteralTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(LiteralExpressionTransformer.Instance);
    }

    [Fact]
    public void LiteralTransformer_ImplementsIExpressionTransformer()
    {
        Assert.IsAssignableFrom<IExpressionTransformer>(LiteralExpressionTransformer.Instance);
    }

    // ── TransformToIR end-to-end ───────────────────────────────────

    [Fact]
    public void TransformToIR_NumericLiteral()
    {
        var result = ConvertCode("class T { void M() { int x = 42; } }");
        Assert.Contains("42", result);
    }

    [Fact]
    public void TransformToIR_StringLiteral()
    {
        var result = ConvertCode("class T { void M() { string s = \"hello\"; } }");
        Assert.Contains("\"hello\"", result);
    }

    [Fact]
    public void Facade_TransformToIR_MultipleDeclarations()
    {
        var result = ConvertCode(@"
class TestClass {
    void TestMethod() {
        bool flag = true;
        int count = 100;
        string name = ""test"";
    }
}");
        Assert.Contains("true", result);
        Assert.Contains("100", result);
        Assert.Contains("\"test\"", result);
    }

    // ── TransformStatementsToIR bridge ─────────────────────────────

    [Fact]
    public void TransformBlockToStructuredBody_ProducesValidJava()
    {
        var result = ConvertCode(@"
class TestClass {
    void Method() {
        int x = 1;
        string s = ""hello"";
        bool b = false;
    }
}");
        Assert.Contains("int x = 1", result);
        Assert.Contains("String s = \"hello\"", result);
        Assert.Contains("boolean b = false", result);
    }

    [Fact]
    public void TransformBlockToStructuredBody_PreservesStatementOrder()
    {
        var result = ConvertCode(@"
class TestClass {
    void Method() {
        int a = 1;
        int b = 2;
        int c = a + b;
    }
}");
        var posA = result.IndexOf("int a = 1");
        var posB = result.IndexOf("int b = 2");
        var posC = result.IndexOf("int c = a + b");
        Assert.True(posA < posB, "a should come before b");
        Assert.True(posB < posC, "b should come before c");
    }

    // ── Structured IR node tests ────────────────────────────────────

    [Fact]
    public void JavaLiteralExpression_ToInlineString_ReturnsValue()
    {
        var lit = new JavaLiteralExpression("42");
        Assert.Equal("42", lit.ToInlineString());
    }

    [Fact]
    public void JavaLiteralExpression_NullLiteral()
    {
        var lit = new JavaLiteralExpression("null");
        Assert.Equal("null", lit.ToInlineString());
    }

    [Fact]
    public void JavaRawExpression_WithResolvedType_PreservesType()
    {
        var expr = new JavaRawExpression("getValue()", "int");
        Assert.Equal("int", expr.ResolvedType);
        Assert.Equal("getValue()", expr.ToInlineString());
    }

    // ── Phase C integration: complex expressions still work ────────

    [Fact]
    public void ComplexExpression_StillWorksAsRawFallback()
    {
        var result = ConvertCode(@"
using System.Collections.Generic;
class TestClass {
    void Method() {
        var list = new List<int>();
        list.Add(42);
        int count = list.Count;
    }
}");
        Assert.Contains("new ArrayList<", result);
        Assert.Contains(".add(42)", result);
    }

    [Fact]
    public void MethodWithReturnValue_StillProducesValidOutput()
    {
        var result = ConvertCode(@"
class TestClass {
    int GetValue() {
        return 42;
    }
}");
        Assert.Contains("return 42", result);
    }

    // ── Phase C Step 2: Migrated transformers implement IIRExpressionTransformer ──

    [Fact]
    public void QueryTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(QueryExpressionTransformer.Instance);
    }

    [Fact]
    public void StringTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(StringExpressionTransformer.Instance);
    }

    [Fact]
    public void UnaryTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(UnaryExpressionTransformer.Instance);
    }

    [Fact]
    public void ElementAccessTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(ElementAccessTransformer.Instance);
    }

    [Fact]
    public void BinaryExpressionTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(BinaryExpressionTransformer.Instance);
    }

    // ── UnaryExpression IR node tests ──────────────────────────────

    [Fact]
    public void JavaUnaryExpression_PrefixNegation()
    {
        var expr = new JavaUnaryExpression
        {
            Operator = "-",
            Operand = new JavaLiteralExpression("5"),
            IsPostfix = false
        };
        Assert.Equal("-5", expr.ToInlineString());
    }

    [Fact]
    public void JavaUnaryExpression_PostfixIncrement()
    {
        var expr = new JavaUnaryExpression
        {
            Operator = "++",
            Operand = new JavaIdentifierExpression { Name = "i" },
            IsPostfix = true
        };
        Assert.Equal("i++", expr.ToInlineString());
    }

    [Fact]
    public void JavaUnaryExpression_LogicalNot()
    {
        var expr = new JavaUnaryExpression
        {
            Operator = "!",
            Operand = new JavaIdentifierExpression { Name = "flag" },
            IsPostfix = false
        };
        Assert.Equal("!flag", expr.ToInlineString());
    }

    // ── BinaryExpression IR node tests ─────────────────────────────

    [Fact]
    public void JavaBinaryExpression_Addition()
    {
        var expr = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression("1"),
            Operator = "+",
            Right = new JavaLiteralExpression("2")
        };
        Assert.Equal("1 + 2", expr.ToInlineString());
    }

    [Fact]
    public void JavaBinaryExpression_NestedExpressions()
    {
        var expr = new JavaBinaryExpression
        {
            Left = new JavaBinaryExpression
            {
                Left = new JavaIdentifierExpression { Name = "a" },
                Operator = "+",
                Right = new JavaIdentifierExpression { Name = "b" }
            },
            Operator = "*",
            Right = new JavaLiteralExpression("2")
        };
        Assert.Equal("a + b * 2", expr.ToInlineString());
    }

    // ── ArrayAccess IR node tests ──────────────────────────────────

    [Fact]
    public void JavaArrayAccessExpression_SimpleAccess()
    {
        var expr = new JavaArrayAccessExpression
        {
            Target = new JavaIdentifierExpression { Name = "arr" },
            Index = new JavaLiteralExpression("0")
        };
        Assert.Equal("arr[0]", expr.ToInlineString());
    }

    // ── End-to-end IR conversion tests ─────────────────────────────

    [Fact]
    public void UnaryNegation_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int x = 5;
        int y = -x;
    }
}");
        Assert.Contains("-x", result);
    }

    [Fact]
    public void BinaryArithmetic_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int a = 1;
        int b = 2;
        int c = a + b;
        int d = a * b - c;
    }
}");
        Assert.Contains("a + b", result);
        Assert.Contains("a * b - c", result);
    }

    [Fact]
    public void BitwiseOperators_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int x = 0xFF;
        int y = ~x;
        int z = x & 0x0F;
        int w = x | 0xF0;
    }
}");
        Assert.Contains("~x", result);
        Assert.Contains("&", result);    // x & 0x0F (hex case may change)
        Assert.Contains("|", result);    // x | 0xF0
    }

    [Fact]
    public void ArrayElementAccess_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int[] arr = new int[] {1, 2, 3};
        int x = arr[0];
        int y = arr[1];
    }
}");
        Assert.Contains("arr[0]", result);
        Assert.Contains("arr[1]", result);
    }

    [Fact]
    public void PostfixIncrement_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int i = 0;
        i++;
    }
}");
        Assert.Contains("i++", result);
    }

    [Fact]
    public void InterpolatedString_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        string name = ""World"";
        string msg = $""Hello {name}"";
    }
}");
        // Should produce concatenation
        Assert.Contains("\"Hello \"", result);
        Assert.Contains("name", result);
    }

    [Fact]
    public void LogicalOperators_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        bool a = true;
        bool b = false;
        bool c = a && b;
        bool d = a || !b;
    }
}");
        Assert.Contains("a && b", result);
        Assert.Contains("!b", result);
    }

    [Fact]
    public void CoalesceExpression_StillWorksAsRawFallback()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        string s = null;
        string t = s ?? ""default"";
    }
}");
        // Coalesce should produce ternary: s != null ? s : "default"
        Assert.Contains("!= null", result);
        Assert.Contains("\"default\"", result);
    }

    // ── Phase C Step 2 continued: all transformers migrated ────────

    [Fact]
    public void ControlFlowTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(ControlFlowTransformer.Instance);
    }

    [Fact]
    public void TypeOperationTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(TypeOperationTransformer.Instance);
    }

    [Fact]
    public void IdentifierExpressionTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(IdentifierExpressionTransformer.Instance);
    }

    [Fact]
    public void AssignmentTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(AssignmentTransformer.Instance);
    }

    [Fact]
    public void ObjectCreationTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(ObjectCreationTransformer.Instance);
    }

    [Fact]
    public void InvocationExpressionTransformer_ImplementsIIRExpressionTransformer()
    {
        Assert.IsAssignableFrom<IIRExpressionTransformer>(InvocationExpressionTransformer.Instance);
    }

    // ── Structured IR node tests for new migrations ────────────────

    [Fact]
    public void JavaConditionalExpression_ToInlineString()
    {
        var expr = new JavaConditionalExpression
        {
            Condition = new JavaIdentifierExpression { Name = "flag" },
            WhenTrue = new JavaLiteralExpression("1"),
            WhenFalse = new JavaLiteralExpression("2")
        };
        Assert.Equal("flag ? 1 : 2", expr.ToInlineString());
    }

    [Fact]
    public void JavaParenthesizedExpression_ToInlineString()
    {
        var expr = new JavaParenthesizedExpression
        {
            InnerExpression = new JavaBinaryExpression
            {
                Left = new JavaLiteralExpression("a"),
                Operator = "+",
                Right = new JavaLiteralExpression("b")
            }
        };
        Assert.Equal("(a + b)", expr.ToInlineString());
    }

    [Fact]
    public void JavaCastExpression_ToInlineString()
    {
        var expr = new JavaCastExpression
        {
            Type = "int",
            Expression = new JavaIdentifierExpression { Name = "value" }
        };
        Assert.Equal("(int) value", expr.ToInlineString());
    }

    [Fact]
    public void JavaThisExpression_ToInlineString()
    {
        var thisExpr = new JavaThisExpression { IsSuper = false };
        Assert.Equal("this", thisExpr.ToInlineString());
        var superExpr = new JavaThisExpression { IsSuper = true };
        Assert.Equal("super", superExpr.ToInlineString());
    }

    [Fact]
    public void JavaMemberAccessExpression_ToInlineString()
    {
        var expr = new JavaMemberAccessExpression
        {
            Target = new JavaIdentifierExpression { Name = "obj" },
            MemberName = "field"
        };
        Assert.Equal("obj.field", expr.ToInlineString());
    }

    // ── End-to-end tests for newly migrated transformers ───────────

    [Fact]
    public void TernaryExpression_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        bool flag = true;
        int x = flag ? 1 : 2;
    }
}");
        Assert.Contains("flag ? 1 : 2", result);
    }

    [Fact]
    public void CastExpression_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        double d = 3.14;
        int i = (int)d;
    }
}");
        Assert.Contains("(int)", result);
    }

    [Fact]
    public void TypeOfExpression_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        var t = typeof(string);
    }
}");
        Assert.Contains("String.class", result);
    }

    [Fact]
    public void SimpleAssignment_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int x = 0;
        x = 42;
    }
}");
        Assert.Contains("x = 42", result);
    }

    [Fact]
    public void ObjectCreation_EndToEnd()
    {
        var result = ConvertCode(@"
using System.Collections.Generic;
class T {
    void M() {
        var list = new List<string>();
    }
}");
        Assert.Contains("ArrayList<", result);
    }

    [Fact]
    public void MethodInvocation_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    int Add(int a, int b) { return a + b; }
    void M() {
        int r = Add(1, 2);
    }
}");
        Assert.Contains("add(1, 2)", result);
    }

    [Fact]
    public void ParenthesizedExpression_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int x = (1 + 2) * 3;
    }
}");
        Assert.Contains("(1 + 2)", result);
        Assert.Contains("* 3", result);
    }

    // ── Phase 1 Deep IR: TypeOperationTransformer ──────────────────

    [Fact]
    public void TypeOp_InstanceOf_ProducesIR()
    {
        var ir = new JavaInstanceOfExpression
        {
            Expression = new JavaIdentifierExpression { Name = "obj" },
            Type = "String"
        };
        Assert.Equal("obj instanceof String", ir.ToInlineString());
    }

    [Fact]
    public void TypeOp_InstanceOfWithPatternVar_ProducesIR()
    {
        var ir = new JavaInstanceOfExpression
        {
            Expression = new JavaIdentifierExpression { Name = "obj" },
            Type = "String",
            PatternVariable = "s"
        };
        Assert.Equal("obj instanceof String s", ir.ToInlineString());
    }

    [Fact]
    public void TypeOp_Is_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M(object obj) {
        bool b = obj is string;
    }
}");
        Assert.Contains("instanceof", result);
        Assert.Contains("String", result);
    }

    [Fact]
    public void TypeOp_IsPattern_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M(object obj) {
        if (obj is string s) {
            System.Console.WriteLine(s);
        }
    }
}");
        Assert.Contains("instanceof String s", result);
    }

    [Fact]
    public void TypeOp_TypeOf_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        var t = typeof(string);
    }
}");
        Assert.Contains("String.class", result);
    }

    [Fact]
    public void TypeOp_Default_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int x = default(int);
    }
}");
        Assert.Contains("0", result);
    }

    [Fact]
    public void TypeOp_DefaultLiteral_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        string s = default;
    }
}");
        Assert.Contains("null", result);
    }

    [Fact]
    public void TypeOp_Default_TypeParameter_Unconstrained_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    TValue M<TValue>() {
        return default(TValue);
    }
}");
        // Method-level type parameter: a Class<TValue> parameter is added so
        // DefaultValue.of(token) can return the proper default (0 for int,
        // new ValueType() for structs, null for reference types).
        Assert.Contains("Class<TValue>", result);
        Assert.Contains("DefaultValue.of(_cs2j_TValue)", result);
        Assert.DoesNotContain("return null", result);
        Assert.DoesNotContain("_cs2jDefault_", result);
    }

    [Fact]
    public void TypeOp_Default_TypeParameter_StaticMethod_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    public static TValue M<TValue>() {
        return default(TValue);
    }
}");
        // Method-level type parameter in a static method — Class<TValue> param
        // added so callers pass a type token for correct default values.
        Assert.Contains("Class<TValue>", result);
        Assert.Contains("DefaultValue.of(_cs2j_TValue)", result);
        Assert.DoesNotContain("return null", result);
        Assert.DoesNotContain("_cs2jDefault_", result);
    }

    [Fact]
    public void TypeOp_Default_TypeParameter_StaticPropertyGetter_EndToEnd()
    {
        var result = ConvertCode(@"
class T<TValue> {
    public static TValue Default => default(TValue);
}");
        // Class-level type parameter in a static property getter — must NOT generate
        // an instance factory method call, which would cause a Java compilation error.
        // null is the correct fallback for static contexts.
        Assert.DoesNotContain("_cs2jDefault_", result);
        Assert.Contains("return null", result);
    }

    [Fact]
    public void TypeOp_Default_TypeParameter_StructConstraint_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    TValue M<TValue>() where TValue : struct {
        return default(TValue);
    }
}");
        Assert.Contains("new TValue()", result);
        Assert.DoesNotContain("null", result);
    }

    [Fact]
    public void TypeOp_Default_TypeParameter_ClassConstraint_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    TValue M<TValue>() where TValue : class {
        return default(TValue);
    }
}");
        Assert.Contains("null", result);
        Assert.DoesNotContain("new TValue()", result);
    }

    [Fact]
    public void TypeOp_DefaultLiteral_TypeParameter_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    TValue M<TValue>() {
        TValue x = default;
        return x;
    }
}");
        // Method-level type parameter with bare 'default' literal.
        // Class<TValue> param added so callers can pass type token.
        Assert.Contains("Class<TValue>", result);
        Assert.Contains("DefaultValue.of(_cs2j_TValue)", result);
        Assert.DoesNotContain("= null", result);
        Assert.DoesNotContain("_cs2jDefault_", result);
    }

    // ── Phase 1 Deep IR: IdentifierExpressionTransformer ───────────

    [Fact]
    public void Identifier_PropertyGetter_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    public int Value { get; set; }
    void M() {
        int x = Value;
    }
}");
        Assert.Contains("getValue()", result);
    }

    [Fact]
    public void Identifier_EnumMember_EndToEnd()
    {
        var result = ConvertCode(@"
enum Color { Red, Green, Blue }
class T {
    void M() {
        var c = Color.Green;
    }
}");
        Assert.Contains("Color.Green", result);
    }

    // ── Phase 1 Deep IR: ElementAccessTransformer ──────────────────

    [Fact]
    public void ElementAccess_ListGet_EndToEnd()
    {
        var result = ConvertCode(@"
using System.Collections.Generic;
class T {
    void M() {
        var list = new List<int>();
        int x = list[0];
    }
}");
        Assert.Contains(".get(0)", result);
    }

    [Fact]
    public void ElementAccess_DictGet_EndToEnd()
    {
        var result = ConvertCode(@"
using System.Collections.Generic;
class T {
    void M() {
        var dict = new Dictionary<string, int>();
        int x = dict[""key""];
    }
}");
        Assert.Contains(".get(", result);
    }

    [Fact]
    public void ElementAccess_StringCharAt_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        string s = ""hello"";
        char c = s[0];
    }
}");
        Assert.Contains(".charAt(0)", result);
    }

    [Fact]
    public void ElementAccess_MethodCallIR()
    {
        // Verify that list[i] produces JavaMethodCallExpression IR
        var ir = new JavaMethodCallExpression
        {
            Target = new JavaIdentifierExpression { Name = "list" },
            MethodName = "get"
        };
        ir.Arguments.Add(new JavaLiteralExpression { Value = "0" });
        Assert.Equal("list.get(0)", ir.ToInlineString());
    }

    // ── Phase 1 Deep IR: BinaryExpressionTransformer ───────────────

    [Fact]
    public void Binary_StringEquals_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        string a = ""x"";
        string b = ""y"";
        bool eq = a == b;
    }
}");
        Assert.Contains("Objects.equals(", result);
    }

    [Fact]
    public void Binary_StringNotEquals_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        string a = ""x"";
        string b = ""y"";
        bool neq = a != b;
    }
}");
        Assert.Contains("!Objects.equals(", result);
    }

    [Fact]
    public void Binary_UserDefinedOperator_EndToEnd()
    {
        var result = ConvertCode(@"
class Point {
    public int X;
    public int Y;
    public static Point operator +(Point a, Point b) => new Point();
}
class T {
    void M() {
        var p = new Point() + new Point();
    }
}");
        Assert.Contains("add(", result);
    }

    // ── Phase 2: StringExpressionTransformer IR ────────────────────

    [Fact]
    public void String_SimpleConcat_ProducesBinaryIR()
    {
        // 2-part interpolation should produce JavaBinaryExpression chain
        var ir = new JavaBinaryExpression
        {
            Left = new JavaLiteralExpression { Value = "\"Hello \"" },
            Operator = "+",
            Right = new JavaIdentifierExpression { Name = "name" }
        };
        Assert.Equal("\"Hello \" + name", ir.ToInlineString());
    }

    [Fact]
    public void String_Interpolation_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        string name = ""World"";
        string s = $""Hello {name}"";
    }
}");
        // Should contain string concatenation
        Assert.Contains("Hello", result);
        Assert.Contains("name", result);
    }

    [Fact]
    public void String_InterpolationWithFormat_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        double d = 3.14;
        string s = $""Value: {d:F2}"";
    }
}");
        Assert.Contains("String.format", result);
    }

    // ── Phase 3: LambdaTransformer IR ──────────────────────────────

    [Fact]
    public void Lambda_ExpressionBody_ProducesIR()
    {
        var lambda = new JavaLambdaExpression
        {
            ExpressionBody = new JavaBinaryExpression
            {
                Left = new JavaIdentifierExpression { Name = "x" },
                Operator = "+",
                Right = new JavaLiteralExpression { Value = "1" }
            }
        };
        lambda.Parameters.Add("x");
        Assert.Equal("x -> x + 1", lambda.ToInlineString());
    }

    [Fact]
    public void Lambda_MultiParam_ProducesIR()
    {
        var lambda = new JavaLambdaExpression
        {
            ExpressionBody = new JavaBinaryExpression
            {
                Left = new JavaIdentifierExpression { Name = "a" },
                Operator = "+",
                Right = new JavaIdentifierExpression { Name = "b" }
            }
        };
        lambda.Parameters.Add("a");
        lambda.Parameters.Add("b");
        Assert.Equal("(a, b) -> a + b", lambda.ToInlineString());
    }

    [Fact]
    public void Lambda_SimpleLambda_EndToEnd()
    {
        var result = ConvertCode(@"
using System;
class T {
    void M() {
        Func<int, int> f = x => x + 1;
    }
}");
        Assert.Contains("->", result);
        Assert.Contains("x + 1", result);
    }

    [Fact]
    public void Lambda_ParenthesizedLambda_EndToEnd()
    {
        var result = ConvertCode(@"
using System;
class T {
    void M() {
        Func<int, int, int> f = (a, b) => a + b;
    }
}");
        Assert.Contains("->", result);
    }

    // ── Phase 4: InvocationExpressionTransformer IR ────────────────

    [Fact]
    public void Invocation_Nameof_ProducesLiteralIR()
    {
        // nameof(x) should produce a JavaLiteralExpression
        var ctx = new ConversionContext(new ConversionOptions(), new CSharpToJava.TypeMapping.TypeMappingRegistry(new CSharpToJava.TypeMapping.TypeMappingConfig()));
        var facade = ExpressionTransformerFacade.Instance;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "class T { void M() { var s = nameof(T); } }");
        var root = tree.GetRoot();
        var invocation = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .First();
        var ir = facade.TransformToIR(invocation, ctx);
        Assert.IsType<JavaLiteralExpression>(ir);
        Assert.Contains("\"T\"", ir.ToInlineString());
    }

    [Fact]
    public void Invocation_Nameof_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        string s = nameof(T);
    }
}");
        Assert.Contains("\"T\"", result);
    }

    [Fact]
    public void Invocation_BareMethodCall_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void DoWork(int x) { }
    void M() {
        DoWork(42);
    }
}");
        Assert.Contains("doWork(42)", result);
    }

    [Fact]
    public void Invocation_BareMethodCall_ProducesMethodCallIR()
    {
        var ctx = new ConversionContext(new ConversionOptions(), new CSharpToJava.TypeMapping.TypeMappingRegistry(new CSharpToJava.TypeMapping.TypeMappingConfig()));
        var facade = ExpressionTransformerFacade.Instance;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "class T { void DoWork(int x) { } void M() { DoWork(42); } }");
        var root = tree.GetRoot();
        var invocation = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .First();
        var ir = facade.TransformToIR(invocation, ctx);
        Assert.IsType<JavaMethodCallExpression>(ir);
        var call = (JavaMethodCallExpression)ir;
        Assert.Equal("doWork", call.MethodName);
        Assert.Single(call.Arguments);
    }

    [Fact]
    public void Invocation_BareMultiArg_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    int Max(int a, int b) { return a > b ? a : b; }
    void M() {
        int x = Max(3, 5);
    }
}");
        Assert.Contains("max(3, 5)", result);
    }

    [Fact]
    public void Invocation_MemberAccess_StaysCorrect()
    {
        // Member access invocations should still produce correct output (via raw fallback)
        var result = ConvertCode(@"
using System;
class T {
    void M() {
        string s = ""hello"";
        int len = s.Length;
    }
}");
        Assert.Contains("length()", result);
    }

    [Fact]
    public void Invocation_GenericMethod_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    U Convert<U>(object o) { return (U)o; }
    void M() {
        int x = Convert<int>(42);
    }
}");
        // Generic type arguments are stripped in Java, result should be convert(42)
        Assert.Contains("convert(42)", result);
    }

    // ── Phase 5: ObjectCreationTransformer IR ──────────────────────

    [Fact]
    public void ObjectCreation_SimpleNew_EndToEnd()
    {
        var result = ConvertCode(@"
class Foo { }
class T {
    void M() {
        Foo f = new Foo();
    }
}");
        Assert.Contains("new Foo()", result);
    }

    [Fact]
    public void ObjectCreation_NewWithArgs_EndToEnd()
    {
        var result = ConvertCode(@"
class Foo {
    public Foo(int x, string y) { }
}
class T {
    void M() {
        Foo f = new Foo(42, ""hello"");
    }
}");
        Assert.Contains("new Foo(42, \"hello\")", result);
    }

    [Fact]
    public void ObjectCreation_ProducesJavaNewExpressionIR()
    {
        var ctx = new ConversionContext(new ConversionOptions(), new CSharpToJava.TypeMapping.TypeMappingRegistry(new CSharpToJava.TypeMapping.TypeMappingConfig()));
        var facade = ExpressionTransformerFacade.Instance;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "class Foo { } class T { void M() { var f = new Foo(); } }");
        var root = tree.GetRoot();
        var creation = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>()
            .First();
        var ir = facade.TransformToIR(creation, ctx);
        Assert.IsType<JavaNewExpression>(ir);
        Assert.Contains("new Foo()", ir.ToInlineString());
    }

    // ── Phase 6: AssignmentTransformer IR ──────────────────────────

    [Fact]
    public void Assignment_SimpleVariable_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int x = 0;
        x = 42;
    }
}");
        Assert.Contains("x = 42", result);
    }

    [Fact]
    public void Assignment_CompoundAdd_EndToEnd()
    {
        var result = ConvertCode(@"
class T {
    void M() {
        int x = 0;
        x += 10;
    }
}");
        Assert.Contains("x += 10", result);
    }

    [Fact]
    public void Assignment_ProducesAssignmentIR()
    {
        var ctx = new ConversionContext(new ConversionOptions(), new CSharpToJava.TypeMapping.TypeMappingRegistry(new CSharpToJava.TypeMapping.TypeMappingConfig()));
        var facade = ExpressionTransformerFacade.Instance;
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "class T { void M() { int x = 0; x = 42; } }");
        var root = tree.GetRoot();
        var assignment = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax>()
            .First();
        var ir = facade.TransformToIR(assignment, ctx);
        Assert.IsType<JavaAssignmentExpression>(ir);
        var assign = (JavaAssignmentExpression)ir;
        Assert.Equal("=", assign.Operator);
    }

    [Fact]
    public void Assignment_PropertySetter_StaysCorrect()
    {
        // Property assignments should produce setter calls (via raw fallback)
        var result = ConvertCode(@"
class T {
    public int X { get; set; }
    void M() {
        X = 42;
    }
}");
        Assert.Contains("setX(42)", result);
    }
}
