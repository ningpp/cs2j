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
    public void Enum_Member_Initializer_Rewritten()
    {
        var src = """
        enum E {
            A = 1,
            B = 2,
            AB = A | B
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        // In enum member initializers, references are unqualified (A, not E.A)
        Assert.Contains("(int)A", result.OutputCode);
        Assert.Contains("(int)B", result.OutputCode);
        Assert.Contains("(E)", result.OutputCode);
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
}
