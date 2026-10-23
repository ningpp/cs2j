using CSharpToJava.Core.EnumPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

public class EnumPreprocessorUnitTests
{
    private static EnumPreprocessorResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("enum-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new EnumPreprocessor().Preprocess(source, model);
    }

    [Fact]
    public void Clean_File_Is_Byte_Level_Preserved()
    {
        var src = """
        class C {
            void M() {
                int a = 1;
                System.Console.WriteLine(a);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void Dirty_But_No_Enum_Bitwise_Is_Byte_Preserved()
    {
        var src = """
        class C {
            void M() {
                int a = 1 | 2;
                int b = a & 0xFF;
                int c = ~a;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.True(result.Statistics.FilesDirtyNoRewrite > 0);
    }

    [Fact]
    public void Simple_Enum_Or_Is_Rewritten()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("(int)E.B", result.OutputCode);
        Assert.Contains("|", result.OutputCode);
    }

    [Fact]
    public void Simple_Enum_And_Is_Rewritten()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A & E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("&", result.OutputCode);
    }

    [Fact]
    public void Simple_Enum_Xor_Is_Rewritten()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A ^ E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("^", result.OutputCode);
    }

    [Fact]
    public void Enum_Or_With_Zero_Literal_No_Cast_On_Zero()
    {
        var src = """
        enum E { A = 1 }
        class C {
            void M() {
                E x = E.A | 0;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.DoesNotContain("(int)0", result.OutputCode);
    }

    [Fact]
    public void Enum_Remove_Flag_Pattern_X_And_Not_A()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                E y = x & ~E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)x", result.OutputCode);
        // ~E.A is rewritten with ~(int)E.A pattern (may have extra parens)
        Assert.Contains("~", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
    }

    [Fact]
    public void Nested_Bitwise_Parenthesized()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2, C = 4 }
        class C {
            void M(E x, E y, E z) {
                E r = (x | y) & ~z;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // Print the output for debugging
        Assert.Contains("(int)x", result.OutputCode);
    }

    [Fact]
    public void Multiple_Flags_Chained_Or()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2, C = 4 }
        class C {
            void M() {
                E x = E.A | E.B | E.C;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("(int)E.B", result.OutputCode);
        Assert.Contains("(int)E.C", result.OutputCode);
    }

    [Fact]
    public void Parent_Already_Enum_Cast_No_Double_Cast()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = (E)(E.A | E.B);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("(E)((E)", result.OutputCode);
        // Parent is already (E) cast, no double cast produced
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("(int)E.B", result.OutputCode);
    }

    [Fact]
    public void Byte_Enum_Not_Has_Unchecked()
    {
        var src = """
        [System.Flags]
        enum FB : byte { A = 1, B = 2 }
        class C {
            void M(FB x) {
                FB y = x & ~FB.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(byte)", result.OutputCode);
        Assert.Contains("unchecked", result.OutputCode);
    }

    [Fact]
    public void Int_Enum_Not_Has_No_Unchecked()
    {
        var src = """
        [System.Flags]
        enum E { A = 1 }
        class C {
            void M(E x) {
                E y = x & ~E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("unchecked", result.OutputCode);
    }

    [Fact]
    public void Uint_Enum_Uses_Uint_Cast()
    {
        var src = """
        [System.Flags]
        enum E : uint { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(uint)E.A", result.OutputCode);
        Assert.Contains("(uint)E.B", result.OutputCode);
    }

    [Fact]
    public void Long_Enum_Uses_Long_Cast()
    {
        var src = """
        [System.Flags]
        enum E : long { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(long)E.A", result.OutputCode);
    }

    [Fact]
    public void NonEnum_Custom_Struct_Or_Not_Rewritten()
    {
        var src = """
        struct S {
            public static S operator |(S a, S b) => a;
            void M() {
                S a = new S();
                S b = new S();
                S c = a | b;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

        [Fact]
    public void Compound_Or_Assignment_Local_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M() {
                E x = E.A;
                x |= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)((int)x | (int)E.B)", result.OutputCode);
    }

    [Fact]
    public void Compound_And_Assignment_Parameter_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                x &= E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)((int)x & (int)E.A)", result.OutputCode);
    }

    [Fact]
    public void Compound_Xor_Assignment_Parameter_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                x ^= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)((int)x ^ (int)E.B)", result.OutputCode);
    }

    [Fact]
    public void Compound_Assignment_Property_Not_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            public E Prop { get; set; }
            void M() {
                Prop |= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Compound_Assignment_This_Field_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            E _f;
            void M() {
                _f |= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(E)((int)_f | (int)E.B)", result.OutputCode);
    }

    [Fact]
    public void HasFlag_Rewritten_To_Mask_Equality()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                bool b = x.HasFlag(E.A);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(int)x", result.OutputCode);
        Assert.Contains("(int)E.A", result.OutputCode);
        Assert.Contains("==", result.OutputCode);
        Assert.DoesNotContain("HasFlag", result.OutputCode);
    }

    [Fact]
    public void HasFlag_Cross_Type_Not_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1 }
        [System.Flags]
        enum F { A = 1 }
        class C {
            void M(E x) {
                bool b = x.HasFlag(F.A);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("HasFlag", result.OutputCode);
    }

    [Fact]
    public void Nullable_Enum_Or_Lifted_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E? x) {
                E? y = x | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("HasValue", result.OutputCode);
        Assert.Contains("(E?)", result.OutputCode);
    }

    [Fact]
    public void Nullable_Enum_Tilde_Lifted_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E : byte { A = 1 }
        class C {
            void M(E? x) {
                E? y = ~x;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("HasValue", result.OutputCode);
        Assert.Contains("unchecked", result.OutputCode);
        Assert.Contains("(byte)", result.OutputCode);
    }

    [Fact]
    public void Nullable_Enum_Property_Operand_Not_Rewritten()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            public E? Prop { get; set; }
            void M() {
                E? y = Prop | E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Enum_Member_Initializer_Is_Preserved()
    {
        // Enum member initializers bind member references as the enum's
        // underlying integer type, so A | B is integer arithmetic by spec.
        // Rewriting it produces invalid nested E | int code; it must be
        // preserved byte-for-byte instead.
        var src = """
        enum E {
            A = 1,
            B = 2,
            AB = A | B
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }

    [Fact]
    public void Residual_Count_Is_Zero_After_Rewrite()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2, C = 4 }
        class C {
            void M(E x) {
                E y = x | E.B;
                E z = y & ~E.A;
                bool b = x.HasFlag(E.C);
                x ^= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.ResidualCount);
    }

    [Fact]
    public void Statistics_Track_Bitwise_Counts()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                E a = x | E.A;
                E b = x & E.A;
                E c = x ^ E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.True(result.Statistics.BitwiseOrRewritten >= 1);
        Assert.True(result.Statistics.BitwiseAndRewritten >= 1);
        Assert.True(result.Statistics.BitwiseXorRewritten >= 1);
    }

    [Fact]
    public void Statistics_Track_HasFlag_And_Compound()
    {
        var src = """
        [System.Flags]
        enum E { A = 1, B = 2 }
        class C {
            void M(E x) {
                bool b = x.HasFlag(E.A);
                x |= E.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.True(result.Statistics.HasFlagRewritten >= 1);
        Assert.True(result.Statistics.CompoundAssignRewritten >= 1);
    }

    // ── CS2J enum residual gate root-cause regressions ──

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    private static string CompileErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("enum-output-test",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return string.Join("\n", compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Enum_Member_Initializer_Bitwise_Binds_As_Underlying_And_Is_Preserved()
    {
        // Inside an enum declaration member initializers bind to the
        // underlying integer operators; rewriting them nests (E) casts back
        // under bitwise operators and yields invalid E | int code (CS0019).
        var src = """
        [System.Flags]
        enum E
        {
            None = 0,
            A = 1,
            B = 2,
            C = A | B,
            D = A & ~B
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Enum_Member_Initializer_And_Method_Code_Mix_Only_Method_Code_Rewrites()
    {
        var src = """
        [System.Flags]
        enum E
        {
            None = 0,
            A = 1,
            B = 2,
            C = A | B,
            D = A & ~B,
            All = A | B | C
        }
        class C
        {
            E M() => E.A | E.B;
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);

        // Enum initializer expressions survive verbatim.
        Assert.Contains("C = A | B", code);
        Assert.Contains("D = A & ~B", code);
        Assert.Contains("All = A | B | C", code);
        // Method code is lowered as before.
        Assert.Contains("((E)((int)E.A | (int)E.B))", code);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Compound_Assignment_To_Property_With_Deterministic_Receiver_Is_Expanded()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class Settings { public E Flags { get; set; } }
        class C
        {
            void M(Settings settings, E other)
            {
                settings.Flags |= other;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains(
            "settings.Flags = ((E)((int)settings.Flags | (int)other))", code);
        Assert.DoesNotContain("|=", code);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Compound_Assignment_To_Array_Element_With_Pure_Index_Is_Expanded()
    {
        // Mirrors s_charTypeTable[c] |= CurrentCharType in the BCL corpus.
        var src = """
        enum E { A = 1, B = 2 }
        class C
        {
            private static readonly E[] s_table = new E[4];
            void M(int c)
            {
                s_table[c] |= E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains(
            "s_table[c] = ((E)((int)(s_table[c]) | (int)E.A))", code);
        Assert.DoesNotContain("|=", code);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Compound_Assignment_To_Property_With_Method_Receiver_Stays_Skipped()
    {
        // Duplicating a method-call receiver would re-invoke it; correct
        // lowering requires statement temp lifting, so keep the warning
        // instead of silently emitting an expansion.
        var src = """
        enum E { A = 1, B = 2 }
        class Holder { public E P { get; set; } }
        class C
        {
            static Holder Make() => new Holder();
            void M()
            {
                Make().P |= E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(
            result.Diagnostics,
            d => d.Severity == EnumPreprocessorSeverity.Warning
                 && d.Message.Contains("not a stable reference"));
    }

    [Fact]
    public void Compound_Assignment_To_Array_Element_With_SideEffecting_Index_Stays_Skipped()
    {
        var src = """
        enum E { A = 1, B = 2 }
        class C
        {
            private static readonly E[] s_table = new E[4];
            private static int Next() => 0;
            void M()
            {
                s_table[Next()] |= E.A;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(
            result.Diagnostics,
            d => d.Severity == EnumPreprocessorSeverity.Warning
                 && d.Message.Contains("not a stable reference"));
    }

    [Fact]
    public void Enum_Bitwise_Inside_Invocation_Receiver_Chain_Is_Rewritten()
    {
        // Mirrors new Regex(pattern, options | compiled).IsMatch(input):
        // the non-HasFlag invocation path used to drop the receiver chain
        // entirely, leaving the enum bitwise node residual.
        var src = """
        enum E { A = 1, B = 2 }
        class Box
        {
            public Box(E e) { }
            public bool Run() => true;
        }
        class C
        {
            bool M(E a, E b) => new Box(a | b).Run();
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        var code = Squish(result.OutputCode);
        Assert.Contains("new Box(((E)((int)a | (int)b)))", code);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Nested_Enum_Bitwise_Cross_Namespace_Cast_Is_Enclosing_Qualified()
    {
        // Mirrors XPathDocument.LoadFlags (nested in System.Xml.XPath)
        // consumed from MS.Internal.Xml.Cache: the inserted (E) cast used to
        // carry only the metadata name "LoadFlags", which fails to bind at
        // the sibling use site (CS0246) and poisons the downstream
        // enum-declaration semantic model so the cast is never lowered.
        var src = """
        namespace N1
        {
            internal class Outer
            {
                [System.Flags]
                internal enum LoadFlags { None = 0, A = 1, B = 2, C = 4 }
            }
        }
        namespace N2
        {
            using N1;
            internal class User
            {
                bool M(Outer.LoadFlags flags)
                {
                    bool a = (flags & Outer.LoadFlags.A) != 0;
                    Outer.LoadFlags b = flags | Outer.LoadFlags.B;
                    Outer.LoadFlags c = flags & ~Outer.LoadFlags.A;
                    flags |= Outer.LoadFlags.C;
                    return a;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(Outer.LoadFlags)", result.OutputCode);
        Assert.DoesNotContain("(LoadFlags)", result.OutputCode);
        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Nested_Nullable_Enum_Lifted_Bitwise_Cross_Namespace_Cast_Is_Qualified()
    {
        // The nullable-lifted ternary emits two (E?) casts; they must carry
        // the enclosing type chain as well.
        var src = """
        namespace N1
        {
            internal class Outer
            {
                [System.Flags]
                internal enum LoadFlags { None = 0, A = 1, B = 2 }
            }
        }
        namespace N2
        {
            using N1;
            internal class User
            {
                Outer.LoadFlags? M(Outer.LoadFlags? x)
                    => x | Outer.LoadFlags.B;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("(Outer.LoadFlags?)", result.OutputCode);
        Assert.DoesNotContain("(LoadFlags?)", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    // ── 普通真 enum 的关系/算术站点（Java enum 只支持 ==/!=，其余二元运算必须
    //    在预处理期降为底层 int 形态；回归 xml-tiny Operator.InvertOperator、
    //    XPathNavigatorReader.get_IsReading、XPathNavigator 的 XPathNodeType 差值）──

    private const string PlainEnumSource = """
        enum Op { EQ, NE, LT, LE, GT, GE }
        class C
        {
            bool Check(Op op) => Op.EQ <= op && op <= Op.GE;
            bool Reading(Op state) => state > Op.EQ && state < Op.GE;
        }
        """;

    [Fact]
    public void Plain_Enum_Relational_In_NonBitwise_File_Is_Lowered_To_Int_Casts()
    {
        var result = Preprocess(PlainEnumSource);

        Assert.True(result.Changed);
        Assert.Contains("(int)Op.EQ <= (int)op", result.OutputCode);
        Assert.Contains("(int)op <= (int)Op.GE", result.OutputCode);
        Assert.Contains("(int)state > (int)Op.EQ", result.OutputCode);
        Assert.Contains("(int)state < (int)Op.GE", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    [Fact]
    public void Plain_Enum_Subtraction_Is_Lowered_To_Int_Casts()
    {
        var src = """
        enum NodeType { Document, Attribute, Text, Whitespace }
        class C
        {
            bool InRange(NodeType type)
                => (uint)(type - NodeType.Text) <= (uint)(NodeType.Whitespace - NodeType.Text);
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("(uint)((int)type - (int)NodeType.Text)", result.OutputCode);
        Assert.Contains("(uint)((int)NodeType.Whitespace - (int)NodeType.Text)", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    // 同 enum == / != 是合法 Java 直译（引用相等），保持原样不改写。
    [Fact]
    public void Plain_Enum_Same_Type_Equality_Stays_Untouched()
    {
        var src = """
        enum Op { EQ, NE }
        class C
        {
            bool Check(Op op) => op == Op.EQ && op != Op.NE;
        }
        """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Contains("op == Op.EQ", result.OutputCode);
        Assert.Contains("op != Op.NE", result.OutputCode);
    }

    // 关系站点沿用本 pass 既有语义（枚举操作数一律降为底层 int，(int) 直译面对
    // 对齐/非对齐 enum 都成立），脏检查扩宽后非 bitwise 文件同样生效。
    [Fact]
    public void NonOrdinal_Enum_Relational_Is_Lowered_To_Int_Casts_Too()
    {
        var src = """
        enum Weird { A = 5, B = 9 }
        class C
        {
            bool Check(Weird w) => w <= Weird.B;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("(int)w <= (int)Weird.B", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }

    // 算术站点只降 plain（底层 int 且 0..n-1 对齐）enum：值形态 enum 的 +/- 站点
    // 由 enum-declaration 的 Deferred 判定交给 numeric-boundary 族，本 pass 不碰。
    [Fact]
    public void NonOrdinal_Enum_Subtraction_Is_Not_Lowered_By_This_Pass()
    {
        var src = """
        enum Weird { A = 5, B = 9 }
        class C
        {
            int Diff(Weird w) => w - Weird.A;
        }
        """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Contains("w - Weird.A", result.OutputCode);
    }

    // 可空 enum vs null 字面量是 HasValue 判空（Java 引用判空直译），绝不能降为
    // (U)x.Value != null——那既不合法也改变语义。回归 regular-expressions
    // RegexParserTests.ParseTree 的 `error != null`（脏检查扩宽后暴露的既有缺口）。
    [Fact]
    public void Nullable_Enum_Vs_Null_Literal_Stays_Untouched()
    {
        var src = """
        enum RegexParseError { InsufficientOpeningParentheses }
        class C
        {
            bool Has(RegexParseError? error) => error != null;
            bool Missing(RegexParseError? error) => error == null;
        }
        """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Contains("error != null", result.OutputCode);
        Assert.Contains("error == null", result.OutputCode);
        Assert.DoesNotContain("error.Value", result.OutputCode);
    }

    // 可空 enum 参与关系比较（非 null 字面量）沿用既有提升行为：读 .Value 后降为
    // (int) 比较。
    [Fact]
    public void Nullable_Enum_Relational_Still_Lifts_Value()
    {
        var src = """
        enum Op { EQ, GE }
        class C
        {
            bool Check(Op? op) => op >= Op.GE;
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("(int)op.Value >= (int)Op.GE", result.OutputCode);
        Assert.Empty(CompileErrors(result.OutputCode));
    }
}
