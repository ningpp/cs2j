using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.LocalFunctionLift;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CoreLocalFunctionLiftOptions = CSharpToJava.Core.LocalFunctionLift.LocalFunctionLiftOptions;

namespace CSharpToJava.Tests;

public class LocalFunctionLiftTests
{
    // -----------------------------------------------------------------------
    // Baseline tests (Task 9)
    // -----------------------------------------------------------------------

    [Fact]
    public void NoLocalFunctions_ReturnsUnchanged()
    {
        var src = "class C { public int M() => 1; }";
        var result = Preprocess(src);
        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void SimpleLocalFunction_LiftedToPrivateMethod()
    {
        var src = """
        class C {
            int M(int x) {
                return Square(x);
                int Square(int v) => v * v;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private int square(int v)", result.OutputCode);
        Assert.Contains("square(x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void StaticLocalFunction_LiftedAsPrivateStatic()
    {
        var src = """
        class C {
            int M(int x) {
                return Add(x, 1);
                static int Add(int a, int b) => a + b;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("private static int add(int a, int b)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Naming_CamelCase()
    {
        var src = """
        class C {
            void M() {
                ParseOffset(1);
                void ParseOffset(int x) { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private void parseOffset(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Naming_MemberConflict_AppendsSuffix()
    {
        var src = """
        class C {
            int square;
            void M() {
                Square(1);
                void Square(int x) { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private void square2(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Modifiers_AsyncPreserved()
    {
        var src = """
        class C {
            async System.Threading.Tasks.Task M() {
                await DoAsync();
                async System.Threading.Tasks.Task DoAsync() => System.Threading.Tasks.Task.CompletedTask;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private async", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Attributes_AreCopiedToHoistedMethod()
    {
        var src = """
        using System;

        [AttributeUsage(AttributeTargets.Method)]
        class MarkerAttribute : Attribute { }

        class C {
            void M() {
                Help();
                [Marker] void Help() { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("[Marker]", result.OutputCode);
        Assert.Contains("private void help()", result.OutputCode);

        var hoisted = ParseMethod(result.OutputCode, "help");
        var attributeList = Assert.Single(hoisted.AttributeLists);
        Assert.Contains("Marker", attributeList.ToString());

        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ReadonlyModifier_IsPreservedForStructLocalFunction()
    {
        var src = """
        struct S {
            int x;
            int M() {
                return Read();
                readonly int Read() => x;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private readonly int read()", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void ReadonlyModifier_NotAddedWhenAbsent()
    {
        var src = """
        struct S {
            int x;
            int M() {
                return Read();
                int Read() => x;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private int read()", result.OutputCode);
        Assert.DoesNotContain("private readonly", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void Body_ExpressionBody_NonVoid_ConvertedToReturnBlock()
    {
        var src = """
        class C {
            int M() {
                return GetValue();
                int GetValue() => 42;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private int getValue()", result.OutputCode);
        Assert.Contains("return 42", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Body_ExpressionBody_Void_ConvertedToExpressionStatement()
    {
        var src = """
        class C {
            void M() {
                DoWork();
                void DoWork() => System.Console.WriteLine("hi");
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("private void doWork()", result.OutputCode);
        Assert.Contains("Console.WriteLine(\"hi\")", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // Capture and call site tests (Task 10)
    // -----------------------------------------------------------------------

    [Fact]
    public void Capture_ReadOnly_BecomesValueParameter()
    {
        var src = """
        class C {
            int M() {
                int x = 10;
                return GetX();
                int GetX() => x;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("_cs2j_cap_x", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void Capture_Written_BecomesRefParameter()
    {
        var src = """
        class C {
            int M() {
                int best = -1;
                Search(3);
                return best;
                void Search(int v) {
                    if (v > best) best = v;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("ref int _cs2j_cap_best", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    /// <summary>
    /// C# 11 <c>scoped</c> 形参被捕获时，提升出的捕获形参必须保留 scoped：
    /// 把 scoped 变量传给非 scoped 形参会被判定为可能逃逸（CS8352）。
    /// </summary>
    [Fact]
    public void ScopedParameterCapture_PreservesScoped()
    {
        var src = """
        class C {
            void M(scoped System.Span<int> s) {
                Use();
                void Use() { s[0] = 1; }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains(
            "private void use(scoped global::System.Span<int> _cs2j_cap_s)",
            result.OutputCode);
        Assert.Contains("use(s)", result.OutputCode);
        Assert.DoesNotContain("scoped ref", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    /// <summary>
    /// <c>scoped</c> 局部变量被捕获时同样保留修饰符。
    /// </summary>
    [Fact]
    public void ScopedLocalCapture_PreservesScoped()
    {
        var src = """
        class C {
            void M() {
                scoped System.Span<int> local = stackalloc int[4];
                Use();
                void Use() { local[0] = 1; }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains(
            "private void use(scoped global::System.Span<int> _cs2j_cap_local)",
            result.OutputCode);
        Assert.DoesNotContain("scoped ref", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    /// <summary>
    /// 非 scoped 捕获不得凭空多出 scoped（防回归）。
    /// </summary>
    [Fact]
    public void UnscopedCapture_DoesNotGainScoped()
    {
        var src = """
        class C {
            int M() {
                int x = 10;
                return GetX();
                int GetX() => x;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Contains("int _cs2j_cap_x", result.OutputCode);
        Assert.DoesNotContain("scoped", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    /// <summary>
    /// scoped 捕获是写入型、需要 ref 才能回写：`scoped ref` 形参不可表达，
    /// 保守跳过并诊断，不产出非法/不安全签名。
    /// </summary>
    [Fact]
    public void ScopedWriteCapture_IsSkipped()
    {
        var src = """
        class C {
            void M(scoped System.Span<int> s) {
                Use();
                void Use() { s = stackalloc int[2]; }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(1, result.Statistics.LocalFunctionsSkipped);
        var diagnostic = Assert.Single(result.Diagnostics,
            d => d.Reason == LocalFunctionSkipReason.RefCaptureUnavailable);
        Assert.Contains("scoped", diagnostic.Message);
        Assert.Contains("void Use()", result.OutputCode);
        Assert.DoesNotContain("scoped ref", result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void CallSite_Recursive()
    {
        var src = """
        class C {
            int M(int n) {
                return Fib(n);
                int Fib(int k) => k <= 1 ? k : Fib(k - 1) + Fib(k - 2);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("fib(n)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void CallSite_NameOf_RenamedNoArgs()
    {
        var src = """
        class C {
            string M() {
                return nameof(Helper);
                void Helper() { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Contains("nameof(helper)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    /// <summary>
    /// nameof(被捕获变量) 的实参只是一个标识符，字符串值必须保持原名字。
    /// 提升后捕获形参改名为 _cs2j_cap_*，若按符号改名会把 nameof 的运行时
    /// 字符串一起改掉（cs-text p05: NumberFormatInfo.ThrowInvalid 的
    /// ArgumentException.ParamName 从 "style" 变成 "_cs2j_cap_style"）。
    /// </summary>
    [Fact]
    public void Capture_NameOf_UsesOriginalIdentifierString()
    {
        var src = """
        class C {
            void M(int style) {
                ThrowInvalid(style);
                void ThrowInvalid(int value) {
                    throw new System.ArgumentException("bad", nameof(style));
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("nameof(_cs2j_cap_style)", result.OutputCode);
        Assert.Contains("\"style\"", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    /// <summary>
    /// 外层 const 局部是编译期常量，不是捕获。`v is Limit` 必须内联成 `v is 42`：
    /// 改写成 `_cs2j_cap_Limit` 形参在常量模式里是 CS0150（应输入常量值）。
    /// </summary>
    [Fact]
    public void ConstCapture_ConstantPattern_IsInlined()
    {
        var src = """
        class C {
            bool M(object o) {
                const int Limit = 42;
                return IsIt(o);
                bool IsIt(object v) => v is Limit;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("_cs2j_cap_Limit", result.OutputCode);
        Assert.Contains("is 42", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    /// <summary>
    /// 形参默认值是常量语境：提升后的方法签名若仍写 `= Limit`，常量在类级
    /// 作用域不可见（CS0103），必须内联成 `= 42`。
    /// </summary>
    [Fact]
    public void ConstCapture_ParameterDefault_IsInlined()
    {
        var src = """
        class C {
            int M() {
                const int Limit = 42;
                return WithDefault();
                int WithDefault(int x = Limit) => x;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("_cs2j_cap_Limit", result.OutputCode);
        Assert.DoesNotContain("x = Limit", result.OutputCode);
        Assert.Contains("= 42", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    /// <summary>字符串常量同样按值内联为字面量。</summary>
    [Fact]
    public void ConstCapture_StringConst()
    {
        var src = """
        class C {
            void M() {
                const string S = "abc";
                Write();
                void Write() => System.Console.WriteLine(S);
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain("_cs2j_cap_S", result.OutputCode);
        Assert.Contains("\"abc\"", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    /// <summary>const 内联、普通变量仍是捕获形参，两者互不影响。</summary>
    [Fact]
    public void ConstCapture_MixedWithNormalCapture()
    {
        var src = """
        class C {
            int M() {
                int offset = 3;
                const int Limit = 42;
                return Sum();
                int Sum() => offset + Limit;
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("_cs2j_cap_offset", result.OutputCode);
        Assert.DoesNotContain("_cs2j_cap_Limit", result.OutputCode);
        Assert.Contains("+ 42", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    /// <summary>
    /// Iterator local functions are lifted, not skipped: the hoisted method keeps the
    /// yield statements and is a valid iterator method at class level (see the
    /// deliberately disabled iterator gate in LocalFunctionLiftAnalyzer).
    /// </summary>
    [Fact]
    public void Iterator_LiftedToClassLevelIteratorMethod()
    {
        var src = """
        class C {
            System.Collections.Generic.IEnumerable<int> M() {
                return GetSeq();
                System.Collections.Generic.IEnumerable<int> GetSeq() {
                    yield return 1;
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.DoesNotContain(result.Diagnostics, d => d.Reason == LocalFunctionSkipReason.Iterator);
        // The hoisted method is declared at class level and keeps the yield statement.
        Assert.Contains("private System.Collections.Generic.IEnumerable<int> getSeq()", result.OutputCode);
        Assert.Contains("yield return 1;", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void Statistics_CorrectCounts()
    {
        var src = """
        class C {
            void M() {
                A();
                B();
                void A() { }
                void B() { }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(1, result.Statistics.FilesTransformed);
    }

    // -----------------------------------------------------------------------
    // V-09: local functions declared directly inside a switch section
    // (the rt10 JsonElement.DeepEquals shape — switch statement, section
    // statement list, NOT a nested block). Before the fix the class-level
    // method was generated and every call site was renamed while the
    // original declaration survived: a half-rewritten tree that only the
    // strict residual count caught.
    // -----------------------------------------------------------------------

    [Fact]
    public void SwitchSectionLocalFunction_DeclarationRemovedAfterLift()
    {
        var src = """
        class C {
            bool DeepEquals(int kind, int a, int b) {
                switch (kind) {
                    case 1:
                        return NameEquals(a, b);
                    default:
                        return NameEquals(a, b);
                        static bool NameEquals(int l, int r) { return l == r; }
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private static bool nameEquals(int l, int r)", result.OutputCode);
        Assert.Contains("nameEquals(a, b)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void SwitchSectionLocalFunction_InCaseLabel_Removed()
    {
        var src = """
        class C {
            int M(int kind) {
                switch (kind) {
                    case 0:
                        return Compute(1);
                        static int Compute(int x) { return x * 2; }
                    default:
                        return 0;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("private static int compute(int x)", result.OutputCode);
        Assert.Contains("compute(1)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void SwitchSectionLocalFunctions_MultipleSections_AllRemoved()
    {
        var src = """
        class C {
            int M(int kind) {
                switch (kind) {
                    case 0:
                        return Twice(1);
                        static int Twice(int x) { return x * 2; }
                    case 1:
                        return Triple(1);
                        static int Triple(int x) { return x * 3; }
                    default:
                        return 0;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private static int twice(int x)", result.OutputCode);
        Assert.Contains("private static int triple(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void SwitchSectionLocalFunction_InsideNestedBlock_StillLifted()
    {
        // Regression guard: a local function inside a block nested in a
        // switch section always went through the VisitBlock channel.
        var src = """
        class C {
            int M(int kind) {
                switch (kind) {
                    case 0:
                        {
                            return Compute(1);
                            static int Compute(int x) { return x * 2; }
                        }
                    default:
                        return 0;
                }
            }
        }
        """;
        var result = Preprocess(src);
        Assert.True(result.Changed);
        Assert.Contains("private static int compute(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // Capture propagation naming: a callee's capture parameter name must be
    // made unique in the caller's lifted signature. Two different symbols can
    // share the same original name (e.g. the caller's own local `node` and a
    // callee's parameter `node`), and each plan assigns `_cs2j_cap_node`
    // independently. Merging the callee captures verbatim produced two
    // `_cs2j_cap_node` parameters (CS0100).
    // -----------------------------------------------------------------------

    [Fact]
    public void CapturePropagation_SameNameAsExistingCapture_DeduplicatesParameterNames()
    {
        var src = """
        class C {
            void M() {
                int node = 1;
                Run(2);
                void Run(int node) {
                    Write();
                    Helper();
                    void Write() => System.Console.WriteLine(node);
                }
                void Helper() {
                    System.Console.WriteLine(node);
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        AssertNoLocalFunctions(result.OutputCode);

        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(result.OutputCode).GetRoot();
        var lifted = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "run");
        var parameterNames = lifted.ParameterList.Parameters
            .Select(p => p.Identifier.Text)
            .ToList();
        Assert.Equal(parameterNames.Count, parameterNames.Distinct(StringComparer.Ordinal).Count());

        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // CS1676: 带 out/ref 形参的局部函数被当作方法组转换时，合成 lambda 的
    // 形参必须保留 ref/out/in 修饰符，否则与目标委托签名不匹配。
    // -----------------------------------------------------------------------

    [Fact]
    public void MethodGroup_WithOutParameterAndCapture_PreservesOutModifier()
    {
        var src = """
        class C {
            delegate void D(string s, out int x);
            void M() {
                string prefix = "p";
                D d = Run;
                int value;
                d("x", out value);
                void Run(string s, out int x) {
                    x = (prefix + s).Length;
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        AssertNoLocalFunctions(result.OutputCode);
        Assert.Contains("(string __cs2jArg0, out int __cs2jArg1)", result.OutputCode);
        Assert.Contains("out __cs2jArg1", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // CS0123: 提升方法携带外层方法的类型参数时必须显式书写类型实参。方法组转换
    // 无法携带显式类型实参，所以要把引用点改写成等价 lambda，并在调用上补
    // `<T>`；局部函数自己的类型参数在调用点无法命名，只能保守跳过。
    // -----------------------------------------------------------------------

    [Fact]
    public void MethodGroup_WithEnclosingTypeParameterCapture_SynthesizesGenericLambda()
    {
        var src = """
        using System;
        class C {
            void M<T>(T seed) {
                Func<T, T> f = Gen;
                Console.WriteLine(f(seed));
                T Gen(T input) => seed;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("gen<T>(", result.OutputCode);
        Assert.DoesNotContain("= gen;", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void MethodGroup_OwnTypeParameterWithEnclosingCapture_IsSkipped()
    {
        var src = """
        using System;
        class C {
            void M<T>(T seed) {
                Func<T, T> f = Gen;
                Console.WriteLine(f(seed));
                T Gen<U>(U input) => seed;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(1, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(0, result.Statistics.LocalFunctionsLifted);
        Assert.Contains(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.EnclosingTypeParameterConflict
            && d.Message.Contains(
                "method group conversion cannot supply", StringComparison.Ordinal));
        Assert.Contains("Gen<U>(U input)", result.OutputCode);
        Assert.Contains("= Gen;", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // 显式类型实参的方法组引用（Gen<int>）：此前 VisitIdentifierName 把
    // GenericName 的父节点直接交给「VisitInvocationExpression 处理」，但那只
    // 覆盖调用表达式；方法组 / 委托转换场景原样保留 Gen<int>，提升后 CS0103。
    // -----------------------------------------------------------------------

    [Fact]
    public void GenericMethodGroup_WithCapture_SynthesizesLambda()
    {
        var src = """
        using System;
        class C {
            void M(int cap) {
                Func<int, int> f = Gen<int>;
                Console.WriteLine(f(1));
                int Gen<T>(T input) => cap + 1;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.DoesNotContain("= Gen<int>", result.OutputCode);
        // 合成 lambda 的调用必须保留显式类型实参：gen<int>(cap, __cs2jArg0)
        Assert.Contains("gen<int>(", result.OutputCode);
        Assert.Contains("_cs2j_cap_cap", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void GenericMethodGroup_NoCapture_IsRenamed()
    {
        var src = """
        class C {
            void M() {
                System.Func<int, int> f = Gen<int>;
                System.Console.WriteLine(f(1));
                int Gen<T>(T input) => 1;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("gen<int>", result.OutputCode);
        Assert.DoesNotContain("= Gen<int>", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void GenericMethodGroup_InNameOf_FoldsToLiteral()
    {
        var src = """
        class C {
            string M() {
                string name = nameof(Gen<int>);
                return name;
                int Gen<T>(T input) => 1;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Contains("\"Gen\"", result.OutputCode);
        Assert.DoesNotContain("nameof(Gen<int>)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // 捕获传播 / holder 判定 / 跳过级联此前只扫 IdentifierNameSyntax，漏掉
    // GenericNameSyntax 形式的方法组引用（`Other<int>` 非调用）。重写器会把它
    // 合成为 `(args) => other<int>(caps, args)`，若 caller 未传播这些捕获，
    // lambda 里就会引用 caller 作用域中不存在的名字（CS0103 / CS1628）。
    // -----------------------------------------------------------------------

    [Fact]
    public void NestedGenericMethodGroup_CapturesPropagateToCaller()
    {
        var src = """
        class C {
            void M() {
                int x = 5;
                Outer();
                void Outer() {
                    System.Func<int, int> f = Inner<int>;
                    System.Console.WriteLine(f(1));
                }
                int Inner<T>(T input) => x + 1;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);

        // Outer 自己的方法体里没有 x 的语法引用，只有 Inner<int> 方法组引用；
        // x 必须经由捕获传播进入 Outer 的提升方法签名。
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(result.OutputCode).GetRoot();
        var lifted = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "outer");
        Assert.Contains(lifted.ParameterList.Parameters,
            p => p.Identifier.Text == "_cs2j_cap_x");
        Assert.Contains("outer(x)", result.OutputCode);
        AssertCompiles(result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void NestedGenericMethodGroup_WriteCaptureGetsHolderWhenNeeded()
    {
        // Inner 写入 x，且 Inner<int> 的方法组引用位于 Outer 体内的 lambda 中：
        // 重写器合成的 lambda 会以 ref 形式携带 x（CS1628），必须改走 holder。
        var src = """
        class C {
            void M() {
                int x = 0;
                Outer();
                void Outer() {
                    System.Action a = () => {
                        System.Func<int, int> f = Inner<int>;
                        System.Console.WriteLine(f(1));
                    };
                    a();
                }
                int Inner<T>(T input) { x++; return 1; }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.RefCaptureInNestedLambda);
        Assert.Contains("StrongBox<int> x", result.OutputCode);
        Assert.Contains("StrongBox<int> _cs2j_cap_x", result.OutputCode);
        Assert.DoesNotContain("ref _cs2j_cap_", result.OutputCode);
        AssertCompiles(result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void GenericMethodGroupReferenceToSkippedFunction_CascadesSkip()
    {
        // Inner 因「own 类型参数 + 携带外层类型参数」被方法组转换禁用而跳过；
        // Outer 以 Inner<int> 方法组引用它，也必须一起跳过（residual 保留原样）。
        var src = """
        using System;
        class C {
            void M<T>(T seed) {
                Outer(seed);
                void Outer(T value) {
                    Func<int, T> f = Inner<int>;
                    Console.WriteLine(f(1));
                }
                T Inner<U>(U input) => seed;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(2, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(0, result.Statistics.LocalFunctionsLifted);
        Assert.Contains(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.EnclosingTypeParameterConflict);
        Assert.Contains(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.ReferencesSkippedLocalFunction
            && d.Message.Contains("'Inner'", StringComparison.Ordinal));
        Assert.Contains("Inner<int>", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // CS1628: a write capture would become a `ref` parameter, and C# forbids
    // using ref parameters inside lambdas/anonymous methods. Such captures are
    // now wrapped in a StrongBox<T> holder instead of skipping the function:
    // the declaring local becomes the holder and the lifted method receives it
    // by value, so the synthesized lambda never uses `ref`.
    // -----------------------------------------------------------------------

    [Fact]
    public void WriteCapture_MethodGroupReferenceInBody_LiftsWithStrongBoxHolder()
    {
        // Mirrors RegexCompiler.EmitNode: the body contains a method group
        // reference to the local function itself, which the rewriter turns into
        // `() => inc(x)` after the capture became a StrongBox holder.
        var src = """
        class C {
            void M() {
                int x = 0;
                Inc();
                Other();
                void Inc() {
                    System.Action a = Inc;
                    x++;
                    a();
                }
                void Other() { }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.RefCaptureInNestedLambda);
        Assert.Contains("StrongBox<int> x", result.OutputCode);
        Assert.Contains("StrongBox<int> _cs2j_cap_x", result.OutputCode);
        Assert.DoesNotContain("ref _cs2j_cap_", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void WriteCapture_ReferencedInsideNestedLambda_LiftsWithStrongBoxHolder()
    {
        var src = """
        class C {
            void M() {
                int x = 0;
                Inc();
                Other();
                void Inc() {
                    System.Action a = () => x++;
                    a();
                }
                void Other() { }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.RefCaptureInNestedLambda);
        Assert.Contains("StrongBox<int> x", result.OutputCode);
        Assert.Contains("_cs2j_cap_x.Value++", result.OutputCode);
        Assert.DoesNotContain("ref _cs2j_cap_", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void WriteCapture_SharedByMultipleLocalFunctions_DeclaresHolderOnce()
    {
        // One local function is converted to a delegate (forcing the holder),
        // the other only reads the same variable. Both must observe the same
        // holder instance and the declaration must be rewritten exactly once.
        var src = """
        class C {
            void M() {
                int x = 1;
                Inc();
                Print();
                void Inc() {
                    System.Action a = Inc;
                    x++;
                    a();
                }
                void Print() { System.Console.WriteLine(x); }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(1, CountOccurrences(result.OutputCode, "StrongBox<int> x"));
        Assert.Equal(2, CountOccurrences(result.OutputCode, "StrongBox<int> _cs2j_cap_x"));
        Assert.DoesNotContain("ref _cs2j_cap_", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // CS1623 / CS1988: iterator and async local functions cannot declare
    // ref/in/out parameters, so a write capture cannot become a `ref`
    // parameter there. Such captures now use the same StrongBox<T> holder as
    // the lambda case instead of emitting uncompilable code (iterator) or
    // skipping the function (async).
    // -----------------------------------------------------------------------

    [Fact]
    public void IteratorWithWriteCapture_UsesStrongBoxHolder()
    {
        var src = """
        class C {
            System.Collections.Generic.IEnumerable<int> M() {
                int sum = 0;
                return Gen();
                System.Collections.Generic.IEnumerable<int> Gen() {
                    while (sum < 3) {
                        sum++;
                        yield return sum;
                    }
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("StrongBox<int> sum", result.OutputCode);
        Assert.Contains("StrongBox<int> _cs2j_cap_sum", result.OutputCode);
        Assert.DoesNotContain("ref _cs2j_cap_", result.OutputCode);
        Assert.DoesNotContain("ref int", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void IteratorWithWriteAndReadCaptures_ReadCaptureStaysByValue()
    {
        var src = """
        class C {
            System.Collections.Generic.IEnumerable<int> M() {
                int sum = 0;
                int start = 5;
                return Gen();
                System.Collections.Generic.IEnumerable<int> Gen() {
                    sum++;
                    yield return sum + start;
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("StrongBox<int> sum", result.OutputCode);
        Assert.Contains("StrongBox<int> _cs2j_cap_sum", result.OutputCode);
        Assert.Contains("int _cs2j_cap_start", result.OutputCode);
        Assert.DoesNotContain("StrongBox<int> _cs2j_cap_start", result.OutputCode);
        Assert.DoesNotContain("ref _cs2j_cap_", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void AsyncWithWriteCapture_UsesStrongBoxHolder()
    {
        var src = """
        class C {
            async System.Threading.Tasks.Task<int> M() {
                int sum = 0;
                await AddAsync();
                return sum;
                async System.Threading.Tasks.Task AddAsync() {
                    sum++;
                    await System.Threading.Tasks.Task.CompletedTask;
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("StrongBox<int> sum", result.OutputCode);
        Assert.Contains("StrongBox<int> _cs2j_cap_sum", result.OutputCode);
        Assert.DoesNotContain("ref _cs2j_cap_", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void IteratorWithRefStructWriteCapture_IsSkippedWithDiagnostic()
    {
        // Span<int> cannot be a StrongBox type argument, and an iterator method
        // cannot take a ref parameter: the local function must stay local so the
        // output never contains the illegal `ref` iterator signature (CS1623).
        var src = """
        class C {
            System.Collections.Generic.IEnumerable<int> M() {
                System.Span<int> span = stackalloc int[1];
                return Gen();
                System.Collections.Generic.IEnumerable<int> Gen() {
                    span = stackalloc int[2];
                    yield return span[0];
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(1, result.Statistics.LocalFunctionsSkipped);
        var diagnostic = Assert.Single(result.Diagnostics,
            d => d.Reason == LocalFunctionSkipReason.RefCaptureUnavailable);
        Assert.Contains("iterator", diagnostic.Message);
        Assert.Contains("CS1623", diagnostic.Message);
        // The local function stays local (unchanged), and no illegal holder/ref
        // signature was emitted.
        Assert.Contains("System.Collections.Generic.IEnumerable<int> Gen()", result.OutputCode);
        Assert.DoesNotContain("StrongBox", result.OutputCode);
        AssertNoSyntaxErrors(result.OutputCode);
    }

    [Fact]
    public void HolderSharedByPlainAndIteratorLocalFunctions_UsesStrongBoxEverywhere()
    {
        // The holder decision is per symbol: one written capture forces every
        // lifted function (the plain one that would otherwise use `ref` and the
        // iterator one that cannot) to receive the same StrongBox by value.
        var src = """
        class C {
            System.Collections.Generic.IEnumerable<int> M() {
                int x = 0;
                Inc();
                return Gen();
                void Inc() { x++; }
                System.Collections.Generic.IEnumerable<int> Gen() {
                    x++;
                    yield return x;
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(2, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(1, CountOccurrences(result.OutputCode, "StrongBox<int> x"));
        Assert.Equal(2, CountOccurrences(result.OutputCode, "StrongBox<int> _cs2j_cap_x"));
        Assert.DoesNotContain("ref _cs2j_cap_", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void NestedLocalFunction_CapturesCallerParameter_DoesNotBecomeCallerCapture()
    {
        // A nested local function capturing the caller's own parameter must not
        // be propagated back to the caller: the hoisted caller still has that
        // parameter, so propagating it produced a duplicate parameter / renamed
        // the caller's parameter references to the capture parameter.
        var src = """
        class C {
            void M() {
                Inc(1);
                Other();
                void Inc(int x) {
                    Nested();
                    void Nested() { System.Console.WriteLine(x); }
                }
                void Other() { }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(3, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private void inc(int x)", result.OutputCode);
        Assert.DoesNotContain("_cs2j_cap_x", result.OutputCode.Split("private void inc")[1]
            .Split("private void nested")[0]);
        AssertCompiles(result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    [Fact]
    public void ReadOnlyCapture_ReferencedInsideNestedLambda_StillLifts()
    {
        // Read-only captures are passed by value, so the lambda may use them.
        var src = """
        class C {
            int M() {
                int x = 1;
                return Inc();
                int Inc() {
                    System.Func<int> f = () => x + 1;
                    return f();
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private int inc(int _cs2j_cap_x)", result.OutputCode);
        Assert.Contains("() => _cs2j_cap_x + 1", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // ErrorType capture with a declarable type syntax: the semantic model could
    // not bind the captured variable's type (e.g. a missing reference), but the
    // declaration's type syntax is enough to spell the parameter. The lifted
    // method re-binds the same syntax in the real project, where the reference
    // exists.
    // -----------------------------------------------------------------------

    [Fact]
    public void UnresolvedCaptureType_WithDeclarableSyntax_IsLifted()
    {
        var src = """
        class C {
            void M() {
                var queue = new MissingType();
                Trim();
                void Trim() { queue.DoWork(); }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("MissingType _cs2j_cap_queue", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // #if 条件编译区：提升方法的插入点必须与声明点/调用点的可见域一致。
    // 旧逻辑固定插在“最后一个字段之后”；若该字段位于 #if 区域内，方法被
    // 关进区域，区域外的调用点看不到它（CS0103）。
    // -----------------------------------------------------------------------

    [Fact]
    public void ConditionalDirective_LiftInsertedOutsideIfRegion_Compiles()
    {
        var src = """
        class C {
            int _x;
        #if !DEBUG
            int conditionalField;
        #endif
            int M(int v) {
                return Helper(v);
                static int Helper(int x) => x + 1;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        AssertNoLocalFunctions(result.OutputCode);

        var helperIndex = result.OutputCode.IndexOf("private static int helper", StringComparison.Ordinal);
        var ifIndex = result.OutputCode.IndexOf("#if !DEBUG", StringComparison.Ordinal);
        Assert.True(helperIndex >= 0 && ifIndex > helperIndex,
            "Lifted method must be inserted outside the conditional region.\n" + result.OutputCode);

        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // 嵌套局部函数的捕获传播：callee 捕获的符号可能就是 caller 自己的局部
    // 变量。它不能变成 caller 的形参，否则 caller 的外部调用点会被追加一个
    // 作用域外的实参（CS0103）。
    // -----------------------------------------------------------------------

    [Fact]
    public void NestedLocalFunction_CaptureOfEnclosingLocal_NotPropagatedToOuterCallSite()
    {
        var src = """
        class C {
            int M() {
                int seed = 1;
                return Run(seed);
                int Run(int value) {
                    int local = value + seed;
                    return Inner();
                    int Inner() => local + seed;
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // 合并全树编译里别的工程的源类型会“泄漏”进本文件的语义模型：
    // System.Private.CoreLib 的 dotnet.System.Text.StringBuilder 正是本文件命名
    // 空间 dotnet.System.Text.RegularExpressions 的祖先，按简单名查找先命中它，
    // 遮蔽了 using System.Text 引入的 BCL StringBuilder。FullyQualifiedFormat 于是
    // 生成真实工程（只引用 BCL）里不存在的 global::dotnet.System.Text.StringBuilder
    // 形参 —— CS0234。提升后的形参必须按同一位置的 using/命名空间规则重新拼写。
    // -----------------------------------------------------------------------

    [Fact]
    public void CaptureType_ShadowedByOtherMergedProjectType_DisplaysTypeResolvableAfterLift()
    {
        var shadowingSource = """
        namespace dotnet.System.Text
        {
            public sealed class StringBuilder
            {
                public StringBuilder Append(char c) => this;
            }
        }
        """;

        var source = """
        using System.Text;

        namespace dotnet.System.Text.RegularExpressions
        {
            class RegexCharClass
            {
                string Describe(string set)
                {
                    StringBuilder desc = new StringBuilder().Append('[');
                    RenderRanges();
                    return desc.ToString();

                    void RenderRanges()
                    {
                        desc.Append(']');
                    }
                }
            }
        }
        """;

        var shadowingTree = CSharpSyntaxTree.ParseText(
            shadowingSource, path: "System/Text/StringBuilder.cs");
        var tree = CSharpSyntaxTree.ParseText(
            source, path: "System/Text/RegularExpressions/RegexCharClass.cs");
        var compilation = CSharpCompilation.Create("local-fn-merged",
            new[] { shadowingTree, tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var preprocessor = new LocalFunctionLiftPreprocessor();
        var result = preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));

        Assert.True(result.Changed);
        AssertNoLocalFunctions(result.OutputCode);
        Assert.DoesNotContain("dotnet.System.Text.StringBuilder", result.OutputCode);

        // 真实工程里没有 Prelude 的 dotnet.System.Text 命名空间：
        // 形参类型必须重新解析到 BCL System.Text.StringBuilder。
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // 写入型捕获 + ref 形参的前提：调用点实参必须确定赋值。局部声明时不初始化、
    // 只在局部函数体内赋值时，声明处补 `= default(T)`（cs-text p05 的 ch1/ch2），
    // 提升后调用点的 ref 实参即满足确定赋值，且读取前必经赋值，语义不变。
    // -----------------------------------------------------------------------

    [Fact]
    public void WriteCapture_LocalWithoutInitializer_SeedsDefaultAndLifts()
    {
        var src = """
        class C {
            string M() {
                char ch1;
                char ch2;
                string result = Render();
                return result;
                string Render() {
                    ch1 = 'a';
                    ch2 = ch1;
                    return ch2.ToString();
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.DoesNotContain(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.RefCaptureNotDefinitelyAssigned);
        Assert.Contains("char ch1 = default(char);", result.OutputCode);
        Assert.Contains("char ch2 = default(char);", result.OutputCode);
        Assert.Contains("ref ch1", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void WriteCapture_LocalWithoutInitializer_NeedsHolder_SeedsDefaultInStrongBox()
    {
        // 无初始化器的捕获同时需要在 lambda 内引用（holder）：声明改写为
        // StrongBox<T> 时其构造实参用 default(T)，提升后的方法按值接收 holder。
        var src = """
        class C {
            string M() {
                char ch;
                string result = Render();
                return result;
                string Render() {
                    System.Func<char> read = () => ch;
                    ch = 'a';
                    return read().ToString();
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("StrongBox<char> ch = new", result.OutputCode);
        Assert.Contains("StrongBox<char>(default(char))", result.OutputCode);
        Assert.Contains("StrongBox<char> _cs2j_cap_ch", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // 局部函数名在条件编译区（本次分析编译未启用该符号）里被引用：提升方法保留
    // 原名，使启用/禁用两种符号配置下的引用都解析到同一个方法（cs-text p05 的
    // #if DEBUG nameof(TryFindRawFixedSets)）。
    // -----------------------------------------------------------------------

    [Fact]
    public void NameReferencedInDisabledText_LiftsWithOriginalName()
    {
        var src = """
        class C {
            string M() {
                string Helper() => "x";
        #if DEBUG
                return nameof(Helper);
        #else
                return Helper();
        #endif
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("private string Helper()", result.OutputCode);
        Assert.Contains("#if DEBUG", result.OutputCode);
        Assert.Contains("nameof(Helper)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);

        // DEBUG 未定义：走 #else 分支的调用点；DEBUG 定义：走 #if 分支的 nameof。
        AssertCompilesWithSymbols(result.OutputCode);
        AssertCompilesWithSymbols(result.OutputCode, "DEBUG");
    }

    // -----------------------------------------------------------------------
    // 原名与宿主类型成员冲突时，禁用区里的引用做精确标识符替换后仍可改名提升；
    // 两种符号配置都必须编译通过（原实现直接跳过，禁用区留着旧名 → CS0103）。
    // -----------------------------------------------------------------------

    [Fact]
    public void NameReferencedInDisabledText_NameConflict_RewritesDisabledTextPrecisely()
    {
        var src = """
        class C {
            private static bool TryFindRawFixedSets(int a, int b) => a < b;

            bool M() {
                bool ok = TryFindRawFixedSets(1);
        #if DEBUG
                string s = nameof(TryFindRawFixedSets);
                System.Console.WriteLine(s);
        #endif
                return ok;
                static bool TryFindRawFixedSets(int v) => v > 0;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("private static bool tryFindRawFixedSets(int v)", result.OutputCode);
        Assert.Contains("nameof(tryFindRawFixedSets)", result.OutputCode);
        Assert.DoesNotContain("nameof(TryFindRawFixedSets)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);

        AssertCompilesWithSymbols(result.OutputCode);
        AssertCompilesWithSymbols(result.OutputCode, "DEBUG");
    }

    // -----------------------------------------------------------------------
    // async Task 局部函数的表达式体可以是 void 表达式（被调用方返回 void）：
    // 提升后不能写成 `return <void 表达式>`（CS1997），必须保留为表达式语句。
    // -----------------------------------------------------------------------

    [Fact]
    public void Body_ExpressionBody_AsyncTaskWithVoidExpression_BecomesStatement()
    {
        var src = """
        using System.Threading.Tasks;

        class C {
            void M(Task<string> t) {
                Validate(t);
                static async Task Validate(Task<string> t) =>
                    System.Console.WriteLine(await t.ConfigureAwait(false));
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        AssertNoLocalFunctions(result.OutputCode);
        Assert.DoesNotContain("return System.Console.WriteLine", result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // 捕获变量类型在分析编译里是 ErrorType（合并编译的 global using 泄漏使源码
    // 类型与元数据类型同名二义）时，用声明处的类型语法兜底；提升方法插入同一
    // 文件后按原 using 规则重新绑定，真实编译里类型与变量声明一致。
    // -----------------------------------------------------------------------

    [Fact]
    public void Capture_ErrorTypeFromAmbiguousType_VarObjectCreation_UsesDeclaredSyntax()
    {
        var result = PreprocessAmbiguous("""
        using Dotnet.Collections;

        class C {
            void M() {
                var queue = new PriorityQueue<int, int>();
                queue.TrimExcess();
                TrimAndEnsure();
                void TrimAndEnsure() {
                    queue.TrimExcess();
                }
            }
        }
        """);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        AssertNoLocalFunctions(result.OutputCode);

        var method = ParseMethod(result.OutputCode, "trimAndEnsure");
        var parameter = Assert.Single(method.ParameterList.Parameters);
        Assert.Equal("PriorityQueue<int, int>", parameter.Type!.ToString());

        // 真实工程没有泄漏的 global using：输出必须能按原 using 解析出源码类型。
        AssertCompilesWithSourceStub(result.OutputCode);
    }

    [Fact]
    public void Capture_ErrorTypeFromAmbiguousType_ExplicitType_UsesDeclaredSyntax()
    {
        var result = PreprocessAmbiguous("""
        using Dotnet.Collections;

        class C {
            void M() {
                PriorityQueue<int, int> queue = new PriorityQueue<int, int>();
                queue.TrimExcess();
                TrimAndEnsure();
                void TrimAndEnsure() {
                    queue.TrimExcess();
                }
            }
        }
        """);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);

        var method = ParseMethod(result.OutputCode, "trimAndEnsure");
        var parameter = Assert.Single(method.ParameterList.Parameters);
        Assert.Equal("PriorityQueue<int, int>", parameter.Type!.ToString());

        AssertCompilesWithSourceStub(result.OutputCode);
    }

    [Fact]
    public void Capture_ErrorTypeWithoutDeclaredSyntax_StillSkipped()
    {
        var result = Preprocess("""
        class C {
            void M() {
                var queue = Create();
                TrimAndEnsure();
                void TrimAndEnsure() {
                    queue.TrimExcess();
                }
            }
        }
        """);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.LocalFunctionsLifted);
        Assert.Contains(result.Diagnostics, d =>
            d.Reason == LocalFunctionSkipReason.UnsupportedCaptureType
            && d.Message.Contains("unresolved types", StringComparison.Ordinal));
        Assert.Contains("void TrimAndEnsure()", result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // checked / unchecked 上下文：声明点位于 checked/unchecked 区域时不再跳过，
    // 提升方法体整体包一层同名块以复现算术溢出检查语义。
    // -----------------------------------------------------------------------

    [Fact]
    public void CheckedContext_LiftsWithCheckedBody()
    {
        const string src = """
        class C {
            int M() {
                int total = 1;
                checked {
                    Add(2);
                    void Add(int v) { total += v * 3; }
                }
                return total;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);

        var method = ParseMethod(result.OutputCode, "add");
        var checkedStatement = Assert.Single(
            method.DescendantNodes().OfType<CheckedStatementSyntax>());
        Assert.True(checkedStatement.IsKind(SyntaxKind.CheckedStatement));
        // 整个方法体就是这一层 checked 块。
        Assert.Same(checkedStatement, method.Body!.Statements[0]);

        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void UncheckedContext_LiftsWithUncheckedBody()
    {
        const string src = """
        class C {
            int M() {
                int total = 1;
                unchecked {
                    Add(2);
                    void Add(int v) { total += v * 3; }
                }
                return total;
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);

        var method = ParseMethod(result.OutputCode, "add");
        var uncheckedStatement = Assert.Single(
            method.DescendantNodes().OfType<CheckedStatementSyntax>());
        Assert.True(uncheckedStatement.IsKind(SyntaxKind.UncheckedStatement));

        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void CheckedExpression_ContextDetected()
    {
        // 局部函数声明不能直接出现在表达式里，合法形态是声明在 checked(expr)
        // 内的 lambda 体中：声明点的最近 checked 祖先是表达式形式。
        const string src = """
        class C {
            int M() {
                return checked(((System.Func<int>)(() => {
                    int Twice(int v) => v + v;
                    return Twice(2);
                }))());
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);

        var method = ParseMethod(result.OutputCode, "twice");
        var checkedStatement = Assert.Single(
            method.DescendantNodes().OfType<CheckedStatementSyntax>());
        Assert.True(checkedStatement.IsKind(SyntaxKind.CheckedStatement));

        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void CheckedContext_Iterator_StillLifts()
    {
        const string src = """
        using System.Collections.Generic;

        class C {
            IEnumerable<int> M(int n) {
                checked {
                    foreach (var v in Values(n)) yield return v;
                    IEnumerable<int> Values(int count) {
                        for (int i = 0; i < count; i++) yield return i * 2;
                    }
                }
            }
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);

        var method = ParseMethod(result.OutputCode, "values");
        Assert.Contains(method.DescendantNodes().OfType<CheckedStatementSyntax>(),
            s => s.IsKind(SyntaxKind.CheckedStatement));
        Assert.Contains(method.DescendantNodes().OfType<YieldStatementSyntax>(),
            s => s.IsKind(SyntaxKind.YieldReturnStatement));

        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // 字段/属性初始化器里的 lambda 局部函数：C# 不允许初始化器直接声明局部函数，
    // 唯一合法形态是「初始化器 → lambda → 局部函数」。宿主类型存在，调用点在
    // lambda 体内（VisitBlock 的移除通道可用），捕获（含 lambda 形参/局部）按
    // 既有机制变成提升方法形参，因此可以提升。初始化器的值没有 this 上下文
    // （CS0236），提升方法必须 static；属性表达式体是 getter，仍按属性修饰符判定。
    // -----------------------------------------------------------------------

    [Fact]
    public void FieldInitializerLambda_LocalFunctionIsLifted()
    {
        var src = """
        using System;

        class C {
            private readonly Func<int> _f = () => {
                return Local(21);
                int Local(int x) => x * 2;
            };
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("local(21)", result.OutputCode);
        Assert.DoesNotContain("Local(21)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void PropertyInitializerLambda_CapturesLambdaParameter()
    {
        var src = """
        using System;

        class C {
            private Func<int, int> F { get; } = v => {
                return Local();
                int Local() => v + 1;
            };
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);

        var method = ParseMethod(result.OutputCode, "local");
        var parameter = Assert.Single(method.ParameterList.Parameters);
        Assert.Equal("_cs2j_cap_v", parameter.Identifier.Text);
        Assert.Contains("local(v)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void StaticFieldInitializerLambda_HoistsStaticMethod()
    {
        var src = """
        using System;

        class C {
            private static readonly Func<int> _f = () => {
                return Local(21);
                int Local(int x) => x * 2;
            };
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private static int local(int x)", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void FieldInitializerLambda_WriteCapture_UsesHolderOrRef()
    {
        // lambda 体内的局部变量被局部函数写入：ref 实参（lambda 内局部可作 ref
        // 实参）或 StrongBox holder 都可表达，两者都不得产出坏码。
        var src = """
        using System;

        class C {
            private readonly Func<int> _f = () => {
                int x = 0;
                return Local();
                int Local() {
                    x++;
                    return x;
                }
            };
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(0, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains("_cs2j_cap_x", result.OutputCode);
        Assert.True(
            result.OutputCode.Contains("ref x", StringComparison.Ordinal)
            || result.OutputCode.Contains("StrongBox<int> x", StringComparison.Ordinal),
            result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void PropertyExpressionBodyLambda_HoistsInstanceMethod()
    {
        // 属性表达式体是 getter：this 可用，实例属性的局部函数提升为实例方法。
        var src = """
        using System;

        class C {
            private int _v = 3;

            private Func<int> F => () => {
                return Local();
                int Local() => _v;
            };
        }
        """;
        var result = Preprocess(src);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.LocalFunctionsLifted);
        Assert.Contains("private int local()", result.OutputCode);
        Assert.DoesNotContain("private static int local()", result.OutputCode);
        AssertNoLocalFunctions(result.OutputCode);
        AssertCompiles(result.OutputCode);
    }

    [Fact]
    public void TopLevelStatement_LocalFunction_StillSkipped()
    {
        // 顶层语句（CompilationUnitSyntax 直接宿主）没有宿主类型，仍按 NoHostType 跳过。
        var src = """
        using System;

        Console.WriteLine(Local(2));
        int Local(int x) => x * 2;
        """;
        var result = Preprocess(src);

        Assert.False(result.Changed);
        Assert.Equal(0, result.Statistics.LocalFunctionsLifted);
        Assert.Equal(1, result.Statistics.LocalFunctionsSkipped);
        Assert.Contains(result.Diagnostics, d => d.Reason == LocalFunctionSkipReason.NoHostType);
        Assert.Contains("int Local(int x)", result.OutputCode);
    }

    // -----------------------------------------------------------------------
    // Residual gate: every surviving local function is a residual, no
    // "intentionally retained" exemption. Strict (default) must fail.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ResidualGate_Counts_Every_Unlifted_LocalFunction_StrictErrors_NonStrictWarns()
    {
        // 捕获变量类型无法解析（ErrorType 且声明处拼不出类型语法）的局部函数
        // 提升后会产生未声明名字（CS0103），只能保留为 residual 并如实上报。
        const string source = """
            class C {
                void M() {
                    var queue = Create();
                    Use();
                    void Use() { queue.TrimExcess(); }
                }
            }
            """;

        var strict = await RunAdapterAsync(source, strict: true);
        Assert.Equal(1, strict.Statistics.LocalFunctionsResidual);
        var strictResidual = Assert.Single(strict.Diagnostics,
            d => d.Message.Contains("residual local function", StringComparison.Ordinal));
        Assert.Equal(LocalFunctionLiftSeverity.Error, strictResidual.Severity);
        Assert.True(strict.HasErrors);

        var nonStrict = await RunAdapterAsync(source, strict: false);
        Assert.Equal(1, nonStrict.Statistics.LocalFunctionsResidual);
        var nonStrictResidual = Assert.Single(nonStrict.Diagnostics,
            d => d.Message.Contains("residual local function", StringComparison.Ordinal));
        Assert.Equal(LocalFunctionLiftSeverity.Warning, nonStrictResidual.Severity);
        Assert.False(nonStrict.HasErrors);
    }

    // -----------------------------------------------------------------------
    // Multi-configuration lift: a project tree is compiled with several
    // preprocessor-symbol sets (for example generator/test projects define
    // DEBUG unconditionally), so lifting only under the default set leaves
    // local functions inside those disabled branches behind.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ProjectTree_LiftsLocalFunctionsInDebugOnlyBranch()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "cs2j-lfl-symbol-config-" + Guid.NewGuid().ToString("N"));
        var outputRoot = Path.Combine(root, "out");
        Directory.CreateDirectory(root);
        try
        {
            var libDir = Path.Combine(root, "Lib");
            var genDir = Path.Combine(root, "Gen");
            Directory.CreateDirectory(libDir);
            Directory.CreateDirectory(genDir);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(genDir, "Gen.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <DefineConstants>$(DefineConstants);DEBUG</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);
            const string libCode = """
                public class Lib
                {
                    public int Outer(int x)
                    {
                        return Square(x);
                        int Square(int v) => v * v;
                    }
                }
                """;
            const string genCode = """
                public class Gen
                {
                #if DEBUG
                    public string Describe(int x)
                    {
                        return Format(x);
                        static string Format(int v) => "d" + v;
                    }
                #endif
                }
                """;
            File.WriteAllText(Path.Combine(libDir, "Code.cs"), libCode);
            File.WriteAllText(Path.Combine(genDir, "Code.cs"), genCode);

            var solution = Path.Combine(root, "Tree.sln");
            File.WriteAllLines(solution, new[]
            {
                "Microsoft Visual Studio Solution File, Format Version 12.00",
                "# Visual Studio Version 17",
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"Lib\", \"Lib\\Lib.csproj\", \"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}\"",
                "EndProject",
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"Gen\", \"Gen\\Gen.csproj\", \"{{{Guid.NewGuid().ToString().ToUpperInvariant()}}}\"",
                "EndProject",
            });

            var adapter = new LocalFunctionLiftPassAdapter(
                new CoreLocalFunctionLiftOptions(), verbose: false,
                filterUnsupportedProjects: false, compatNamespacePrefixes: null,
                verifyBuild: false, verifyTests: false, strict: true);

            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = root,
                DestinationRoot = outputRoot,
                Force = true,
            });

            Assert.True(run.Success, string.Join("\n", adapter.BuildChainDiagnosticLines(true)));
            Assert.False(adapter.HasErrors);
            Assert.Equal(0, adapter.Statistics.LocalFunctionsResidual);
            Assert.Equal(2, adapter.Statistics.LocalFunctionsLifted);

            var libOutput = File.ReadAllText(
                Path.Combine(run.IntermediateRoot, "Lib", "Code.cs"));
            var genOutput = File.ReadAllText(
                Path.Combine(run.IntermediateRoot, "Gen", "Code.cs"));

            AssertNoLocalFunctions(libOutput);
            AssertNoLocalFunctions(genOutput);
            AssertNoLocalFunctions(genOutput, "DEBUG", "NETCOREAPP");

            Assert.Contains("square", libOutput);
            Assert.Contains("format", genOutput);

            // The lifted declaration must stay inside the condition that guards
            // its local function, otherwise Release builds (which do not compile
            // the DEBUG branch) would see a call to a missing member.
            var debugStart = genOutput.IndexOf("#if DEBUG", StringComparison.Ordinal);
            var methodIndex = genOutput.IndexOf("format", StringComparison.Ordinal);
            var debugEnd = genOutput.IndexOf("#endif", StringComparison.Ordinal);
            Assert.True(debugStart >= 0 && methodIndex > debugStart && methodIndex < debugEnd,
                genOutput);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SymbolConfigurations_DeriveFromProjectDefines_WithAppendSemantics()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "cs2j-lfl-config-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup><DefineConstants>ROOT</DefineConstants></PropertyGroup>
                </Project>
                """);
            var projectA = Path.Combine(root, "A", "A.csproj");
            var projectB = Path.Combine(root, "B", "B.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(projectA)!);
            Directory.CreateDirectory(Path.GetDirectoryName(projectB)!);
            File.WriteAllText(projectA, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <DefineConstants>$(DefineConstants);FEATURE_A</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(projectB, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <DefineConstants>$(DefineConstants);DEBUG</DefineConstants>
                  </PropertyGroup>
                </Project>
                """);

            var layout = new ProjectPreprocessSourceLayout(
                root, root, new[] { projectA, projectB });
            var configs = ConditionalSymbolConfigResolver.Resolve(
                layout, LocalFunctionLiftPassAdapter.DefaultPreprocessorSymbols,
                out var warnings);

            Assert.Empty(warnings);
            Assert.Equal("default", configs[0].Name);
            Assert.Equal(new[] { "NETCOREAPP" }, configs[0].Symbols);

            var union = Assert.Single(configs, config => config.Name == "union");
            Assert.Contains("ROOT", union.Symbols);
            Assert.Contains("FEATURE_A", union.Symbols);
            Assert.Contains("DEBUG", union.Symbols);
            Assert.Contains("NETCOREAPP", union.Symbols);

            var configA = Assert.Single(configs,
                config => config.Name == "project:A/A.csproj");
            Assert.Contains("ROOT", configA.Symbols);
            Assert.Contains("FEATURE_A", configA.Symbols);
            Assert.DoesNotContain("DEBUG", configA.Symbols);

            var configB = Assert.Single(configs,
                config => config.Name == "project:B/B.csproj");
            Assert.Contains("ROOT", configB.Symbols);
            Assert.Contains("DEBUG", configB.Symbols);
            Assert.DoesNotContain("FEATURE_A", configB.Symbols);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SymbolConfigurations_ReplaceSemantics_AndCapWithWarning()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "cs2j-lfl-config-cap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
                <Project>
                  <PropertyGroup><DefineConstants>ROOT</DefineConstants></PropertyGroup>
                </Project>
                """);
            var projects = new List<string>();
            for (var index = 0; index < 12; index++)
            {
                var directory = Path.Combine(root, "P" + index);
                Directory.CreateDirectory(directory);
                var project = Path.Combine(directory, "P" + index + ".csproj");
                // The first project replaces the inherited value; the rest append.
                var define = index == 0
                    ? "ONLY_P0"
                    : $"$(DefineConstants);SYMBOL_{index}";
                File.WriteAllText(project, $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><DefineConstants>{define}</DefineConstants></PropertyGroup>
                    </Project>
                    """);
                projects.Add(project);
            }

            var layout = new ProjectPreprocessSourceLayout(root, root, projects);
            var configs = ConditionalSymbolConfigResolver.Resolve(
                layout, LocalFunctionLiftPassAdapter.DefaultPreprocessorSymbols,
                out var warnings);

            Assert.Equal(
                ConditionalSymbolConfigResolver.MaxConfigurations, configs.Count);
            var warning = Assert.Single(warnings);
            Assert.Contains("limit", warning, StringComparison.OrdinalIgnoreCase);

            var replaced = Assert.Single(configs,
                config => config.Name == "project:P0/P0.csproj");
            Assert.Contains("ONLY_P0", replaced.Symbols);
            Assert.DoesNotContain("ROOT", replaced.Symbols);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertNoLocalFunctions(string code, params string[] symbols)
    {
        var root = CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(preprocessorSymbols: symbols)).GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>());
    }

    private static async Task<LocalFunctionLiftPassAdapter> RunAdapterAsync(
        string source, bool strict)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "cs2j-lfl-residual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "C.cs");
        await File.WriteAllTextAsync(sourcePath, source);
        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: sourcePath);
            var compilation = CSharpCompilation.Create(
                "local-fn-residual",
                new[] { syntaxTree },
                References,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var adapter = new LocalFunctionLiftPassAdapter(
                new CoreLocalFunctionLiftOptions(), verbose: false,
                filterUnsupportedProjects: false, compatNamespacePrefixes: null,
                verifyBuild: false, verifyTests: false, strict: strict);

            await adapter.RewriteFileAsync(new PassFileContext
            {
                SourceCode = source,
                SourceFilePath = sourcePath,
                RelativePath = "C.cs",
                DestinationFilePath = sourcePath,
                SemanticModel = compilation.GetSemanticModel(syntaxTree),
            });

            await adapter.AfterFilesAsync(new PassTreeContext
            {
                Layout = new ProjectPreprocessSourceLayout(
                    root, root, Array.Empty<string>()),
                SourceRoot = root,
                IntermediateRoot = root,
                DestinationRoot = root,
                ProjectGraph = null,
                Exclusion = new SupportedProjectFiltering.ExclusionInfo(),
                SourceFiles = new[] { sourcePath },
                CsFiles = new[] { sourcePath },
                ExternalSharedCompileFiles = Array.Empty<string>(),
                InPlace = true,
                Snapshot = new InPlaceSourceSnapshot(false),
            });

            return adapter;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// 构造与全树分析编译同形的二义场景：源码里的 <c>Dotnet.Collections.PriorityQueue</c>
    /// 与 BCL 的 <c>System.Collections.Generic.PriorityQueue</c> 同时可见（后者由另一个
    /// 工程的 global using 泄漏进来），使 <c>var queue</c> 的类型退化成 ErrorType。
    /// </summary>
    private static LocalFunctionLiftResult PreprocessAmbiguous(string source)
    {
        var stubTree = CSharpSyntaxTree.ParseText(SourceStub, path: "Stub.cs");
        var leakTree = CSharpSyntaxTree.ParseText(
            "global using System.Collections.Generic;", path: "Leak.cs");
        var tree = CSharpSyntaxTree.ParseText(source, path: "Target.cs");
        var compilation = CSharpCompilation.Create("local-fn-ambiguous",
            new[] { stubTree, leakTree, tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var preprocessor = new LocalFunctionLiftPreprocessor();
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
    }

    private const string SourceStub = """
    namespace Dotnet.Collections {
        public class PriorityQueue<TElement, TPriority> {
            public void TrimExcess() { }
        }
    }
    """;

    private static MethodDeclarationSyntax ParseMethod(string code, string name)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name);
    }

    private static void AssertCompilesWithSourceStub(string code)
    {
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(SourceStub, path: "Stub.cs"),
            CSharpSyntaxTree.ParseText(code, path: "Output.cs"),
        };
        var compilation = CSharpCompilation.Create("local-fn-output-stub",
            trees,
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should compile without errors. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }

    private static LocalFunctionLiftResult Preprocess(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("local-fn-test",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preprocessor = new LocalFunctionLiftPreprocessor();
        return preprocessor.Preprocess(source, compilation.GetSemanticModel(tree));
    }

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll")),
    };

    private static void AssertCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("local-fn-output",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should compile without errors. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }

    /// <summary>
    /// 按给定的预处理符号编译输出：条件编译区（#if DEBUG）在真实工程里会启用，
    /// 必须验证两种配置都能解析提升后的方法名。
    /// </summary>
    private static void AssertCompilesWithSymbols(string code, params string[] symbols)
    {
        var tree = CSharpSyntaxTree.ParseText(
            code, new CSharpParseOptions(preprocessorSymbols: symbols));
        var compilation = CSharpCompilation.Create("local-fn-output-symbols",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            $"Output should compile without errors (symbols: {string.Join(",", symbols)}). Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }

    private static void AssertNoLocalFunctions(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<LocalFunctionStatementSyntax>());
    }

    private static void AssertNoSyntaxErrors(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Output should be valid C#. Errors:\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
