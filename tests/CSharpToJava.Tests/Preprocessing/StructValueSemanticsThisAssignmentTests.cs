using System.Reflection;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// SV041 根因修复单测：struct 实例方法/构造函数/访问器整体赋值 this 的语义保持改写。
/// ①实例方法（含表达式位置/链式/复合/自增减/泛型/循环/try/async 形态）走方法级
/// this 持有者 copy-in/copy-out（与 SV040-d 同机制），方法返回后调用者存储按 C#
/// 语义被整体覆盖，逃逸别名不被破坏；②构造函数与属性/索引器/事件访问器走字段式
/// 展开（承接 E13 ConstructorNormalizer 既定模型），覆盖 readonly 字段/get-only
/// 自动属性，构造函数抛错时调用者存储保持原值；③含 base 访问的方法走字段式展开；
/// ④真无保持形态（访问器含 readonly 字段、ctor/访问器非语句位置、base 方法遇
/// readonly 字段或非语句位置、静态成员）保持显式 SV041；⑤与 SV040-d 持有者机制
/// 组合；⑥改写幂等（二次运行零 diff）。
/// </summary>
public class StructValueSemanticsThisAssignmentTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }

    private static CSharpCompilation BuildCompilation(params string[] sources)
    {
        var trees = sources.Select(s => (SyntaxTree)CSharpSyntaxTree.ParseText(s, ParseOptions)).ToArray();
        return CSharpCompilation.Create(
            "svs-this-assignment-test",
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private sealed class PlanRun
    {
        public required StructValueSemanticsPreprocessor Preprocessor { get; init; }
        public required StructValueSemanticsStatistics Statistics { get; init; }
        public required List<StructValueSemanticsDiagnostic> Diagnostics { get; init; }
        public required string Output { get; init; }
        public required bool Changed { get; init; }
    }

    private static PlanRun Analyze(string source, StructValueSemanticsOptions? options = null)
    {
        var compilation = BuildCompilation(source);
        var pp = new StructValueSemanticsPreprocessor(options ?? new StructValueSemanticsOptions());
        pp.BindCompilation(compilation);

        var stats = new StructValueSemanticsStatistics();
        var diags = new List<StructValueSemanticsDiagnostic>();
        var tree = compilation.SyntaxTrees.Single();
        var result = pp.RewriteFile(
            tree.GetText().ToString(), compilation.GetSemanticModel(tree), stats, diags);

        return new PlanRun
        {
            Preprocessor = pp,
            Statistics = stats,
            Diagnostics = diags,
            Output = result.Changed ? result.OutputCode : tree.GetText().ToString(),
            Changed = result.Changed,
        };
    }

    private static void AssertOutputCompiles(string output)
    {
        var errors = BuildCompilation(output).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "Rewritten output failed to compile:\n" + string.Join("\n", errors.Select(e => e.ToString()))
            + "\n--- output ---\n" + output);
    }

    private static string Normalized(string code) =>
        CSharpSyntaxTree.ParseText(code, ParseOptions).GetRoot().NormalizeWhitespace().ToFullString();

    private static ClonePlan Plan(PlanRun run, string qualifiedName = "Lib.Box") =>
        Assert.Single(run.Preprocessor.Plans.Where(p => p.StructQualifiedName == qualifiedName));

    private static void AssertInfeasible(
        PlanRun run, StructValueSemanticsDiagnosticCode code, string qualifiedName = "Lib.Box")
    {
        var plan = Plan(run, qualifiedName);
        Assert.Equal(ClonePlanOutcome.Infeasible, plan.Outcome);
        Assert.False(plan.IsActive);
        Assert.Equal(code, plan.FailCode);
        Assert.Contains(run.Diagnostics, d =>
            d.Code == code && d.Severity == StructValueSemanticsSeverity.Error);
        Assert.Null(plan.GeneratedCloneMethod);
        Assert.Null(plan.GeneratedCopyConstructor);
        Assert.DoesNotContain(StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, run.Output);
        Assert.DoesNotContain(StructValueSemanticsSyntaxFacts.ThisAssignTempPrefix, run.Output);
    }

    private static Assembly EmitAssembly(string output)
    {
        var compilation = BuildCompilation(output);
        using var pe = new MemoryStream();
        var emit = compilation.Emit(pe);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n", emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()))
            + "\n--- output ---\n" + output);
        pe.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(pe.ToArray());
    }

    private static object Boxed(Assembly assembly, string name, Action<Type>? init = null)
    {
        var type = assembly.GetType(name)!;
        var instance = Activator.CreateInstance(type)!;
        init?.Invoke(type);
        return instance;
    }

    private static object Invoke(object instance, string method, params object?[] args)
        => instance.GetType().GetMethod(method)!.Invoke(instance, args)!;

    // ---------- 方法形态：持有者 copy-in/copy-out ----------

    [Fact]
    public void Method_ThisAssignDefault_RewrittenAndCallerStorageZeroed()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Y;
                public void Reset()
                {
                    Y = X;
                    this = default;
                }
            }
            """);

        var plan = Plan(run);
        Assert.True(plan.IsActive);
        Assert.Single(plan.ThisCaptureRewrites);
        Assert.Equal(1, run.Statistics.ThisCaptureMethodsRewritten);
        var output = Normalized(run.Output);
        Assert.Contains("var __cs2jTc0 = this;", output);
        Assert.Contains("__cs2jTc0 = default;", output);
        Assert.Contains("finally", output);
        Assert.Contains("this = __cs2jTc0;", output);
        AssertOutputCompiles(run.Output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(type)!;
        type.GetField("X")!.SetValue(instance, 7);
        type.GetField("Y")!.SetValue(instance, 9);
        type.GetMethod("Reset")!.Invoke(instance, null);
        Assert.Equal(0, type.GetField("X")!.GetValue(instance));
        Assert.Equal(0, type.GetField("Y")!.GetValue(instance));
    }

    [Fact]
    public void Method_ThisAssignAnotherInstance_RuntimeOverwritesAndAliasIsolated()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Inner { public int V; public void Bump() => V++; }
            public struct Box
            {
                public Inner In;
                public int X;
                public void Overwrite(Box other) { this = other; }
                public void MutateInner() { In.Bump(); }
            }
            """);

        Assert.True(Plan(run).IsActive);
        AssertOutputCompiles(run.Output);
        var output = Normalized(run.Output);
        // RHS 非新鲜值：赋值经持有者时仍按 VS-1b 插 Clone（深拷贝嵌套 struct）。
        Assert.Contains("__cs2jTc0 = other.Clone();", output);

        var assembly = EmitAssembly(run.Output);
        var boxType = assembly.GetType("Lib.Box")!;
        var innerType = assembly.GetType("Lib.Inner")!;
        var a = Activator.CreateInstance(boxType)!;
        var b = Activator.CreateInstance(boxType)!;
        // 值类型字段经反射取值得到装箱副本，改后必须回写父实例。
        var aInner = boxType.GetField("In")!.GetValue(a)!;
        innerType.GetField("V")!.SetValue(aInner, 11);
        boxType.GetField("In")!.SetValue(a, aInner);
        boxType.GetField("X")!.SetValue(a, 42);

        boxType.GetMethod("Overwrite")!.Invoke(b, new[] { a });
        // 调用者存储被整体覆盖。
        Assert.Equal(42, boxType.GetField("X")!.GetValue(b));
        Assert.Equal(11, innerType.GetField("V")!.GetValue(boxType.GetField("In")!.GetValue(b)!));

        // 经 b 变异嵌套字段不影响 a（深拷贝隔离）。
        boxType.GetMethod("MutateInner")!.Invoke(b, null);
        Assert.Equal(12, innerType.GetField("V")!.GetValue(boxType.GetField("In")!.GetValue(b)!));
        Assert.Equal(11, innerType.GetField("V")!.GetValue(boxType.GetField("In")!.GetValue(a)!));
    }

    [Fact]
    public void Method_ThisAssignNew_ReadingOldThisOnRhs_RuntimeSeesOldThenOverwrites()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public void Inc() { this = new Box { X = X + 1 }; }
            }
            """);

        AssertOutputCompiles(run.Output);
        Assert.Contains("__cs2jTc0 = new Box", Normalized(run.Output));

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(type)!;
        type.GetField("X")!.SetValue(instance, 41);
        type.GetMethod("Inc")!.Invoke(instance, null);
        Assert.Equal(42, type.GetField("X")!.GetValue(instance));
    }

    [Fact]
    public void Method_ThisAssignInExpressionPosition_RuntimeValueAndWriteBack()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int ReplaceAndRead(int a) => (this = new Box { X = a }).X;
            }
            """);

        Assert.True(Plan(run).IsActive);
        AssertOutputCompiles(run.Output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(type)!;
        var returned = type.GetMethod("ReplaceAndRead")!.Invoke(instance, new object[] { 55 });
        Assert.Equal(55, returned);
        Assert.Equal(55, type.GetField("X")!.GetValue(instance));
    }

    [Fact]
    public void Method_ChainedAssignmentThroughThis_RuntimeBothTargetsOverwritten()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                // a 为按值形参：链式赋值只覆盖形参槽与 this，返回形参观察链式效果。
                public Box Chain(Box a, Box b)
                {
                    a = this = b;
                    return a;
                }
            }
            """);

        AssertOutputCompiles(run.Output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var self = Activator.CreateInstance(type)!;
        var a = Activator.CreateInstance(type)!;
        var b = Activator.CreateInstance(type)!;
        type.GetField("X")!.SetValue(b, 3);
        var returned = type.GetMethod("Chain")!.Invoke(self, new[] { a, b });
        Assert.Equal(3, type.GetField("X")!.GetValue(self));
        Assert.Equal(3, type.GetField("X")!.GetValue(returned));
    }

    [Fact]
    public void Method_CompoundThisAssignmentAndIncrement_RewrittenAndRuntimeCorrect()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public static Box operator +(Box a, Box b) => new Box { X = a.X + b.X };
                public static Box operator ++(Box a) => new Box { X = a.X + 10 };
                public void Add(Box o) { this += o; }
                public void Next() { this++; }
            }
            """);

        Assert.True(Plan(run).IsActive);
        AssertOutputCompiles(run.Output);
        var output = Normalized(run.Output);
        // 两个方法各持一个持有者，计数器按文件内顺序分配。
        Assert.Contains("__cs2jTc0 += o;", output);
        Assert.Contains("__cs2jTc1++;", output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var self = Activator.CreateInstance(type)!;
        var other = Activator.CreateInstance(type)!;
        type.GetField("X")!.SetValue(self, 5);
        type.GetField("X")!.SetValue(other, 7);
        type.GetMethod("Add")!.Invoke(self, new[] { other });
        Assert.Equal(12, type.GetField("X")!.GetValue(self));
        type.GetMethod("Next")!.Invoke(self, null);
        Assert.Equal(22, type.GetField("X")!.GetValue(self));
    }

    [Fact]
    public void Method_ThisAssignInsideLoopSwitchTry_RuntimeFinalStateFromLastIteration()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Last;
                public void Run(int n)
                {
                    for (int i = 0; i < n; i++)
                    {
                        switch (i)
                        {
                            case 0: this = new Box { X = 1, Last = 1 }; break;
                            default: this = new Box { X = i, Last = i }; break;
                        }
                        try { this = new Box { X = X + 1, Last = i }; }
                        catch { }
                    }
                }
            }
            """);

        AssertOutputCompiles(run.Output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(type)!;
        type.GetMethod("Run")!.Invoke(instance, new object[] { 3 });
        Assert.Equal(3, type.GetField("X")!.GetValue(instance));
        Assert.Equal(2, type.GetField("Last")!.GetValue(instance));
    }

    [Fact]
    public void Method_ThisAssign_WithExplicitReceiverLambda_OnlyHolderForAssignment()
    {
        // SegmentStringBuilder.ToString 形态：lambda 只用显式接收者参数（非 this 捕获），
        // 方法仍因 this = default 走持有者；lambda 内形参不被路由。
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                // string.Create 形态：lambda 经显式接收者形参访问状态，不捕获 this。
                private static int Use(int seed, Box b, System.Func<Box, int> map) => seed + map(b);
                public int Run(int seed)
                {
                    int r = Use(seed, this, b => b.X + 1);
                    this = default;
                    return r;
                }
            }
            """);

        AssertOutputCompiles(run.Output);
        var output = Normalized(run.Output);
        Assert.Contains("b => b.X + 1", output);
        Assert.DoesNotContain("b => __cs2jTc", output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(type)!;
        type.GetField("X")!.SetValue(instance, 40);
        var returned = type.GetMethod("Run")!.Invoke(instance, new object[] { 1 });
        Assert.Equal(42, returned);
        Assert.Equal(0, type.GetField("X")!.GetValue(instance));
    }

    [Fact]
    public void Method_ThisAssignCombinedWithCapturingLambda_SingleHolderWithCopyOut()
    {
        // 与 005fb40a 机制组合：闭包 + 整体赋值同走一个持有者，写入出口统一回写。
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Y;
                public void Arm()
                {
                    System.Action a = () => X++;
                    a();
                    this = default;
                    Y = 9;
                }
            }
            """);

        var plan = Plan(run);
        Assert.Single(plan.ThisCaptureRewrites);
        Assert.True(plan.ThisCaptureRewrites[0].HasWrites);
        Assert.Empty(plan.ThisAssignmentExpansions);
        AssertOutputCompiles(run.Output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(type)!;
        type.GetMethod("Arm")!.Invoke(instance, null);
        Assert.Equal(0, type.GetField("X")!.GetValue(instance));
        Assert.Equal(9, type.GetField("Y")!.GetValue(instance));
    }

    [Fact]
    public void GenericMethod_ThisAssignNew_CompilesAndRuntimeOverwrites()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Node<T> where T : struct
            {
                public T Value;
                public int Tag;
                public void Replace(T v) { this = new Node<T> { Value = v, Tag = 1 }; }
            }
            """);

        AssertOutputCompiles(run.Output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Node`1")!.MakeGenericType(typeof(int));
        var instance = Activator.CreateInstance(type)!;
        type.GetMethod("Replace")!.Invoke(instance, new object[] { 33 });
        Assert.Equal(33, type.GetField("Value")!.GetValue(instance));
        Assert.Equal(1, type.GetField("Tag")!.GetValue(instance));
    }

    // ---------- 构造函数形态：字段式展开 ----------

    [Fact]
    public void Constructor_ThisAssignNew_BranchShape_ExpandedFieldByField()
    {
        // XsdDuration 形态：一个分支 this = new(...)，另一分支逐字段赋值。
        var run = Analyze("""
            namespace Lib;
            public struct Dur
            {
                public int A;
                public int B;
                public int C;
                public Dur(int a, int b, int c, bool whole)
                {
                    A = 0; B = 0; C = 0;
                    if (whole)
                    {
                        this = new Dur(a, b, c, false);
                    }
                    else
                    {
                        A = a; B = b; C = c;
                    }
                }
            }
            """);

        var plan = Plan(run, "Lib.Dur");
        Assert.True(plan.IsActive);
        Assert.Single(plan.ThisAssignmentExpansions);
        Assert.Equal(1, run.Statistics.ThisAssignmentMembersExpanded);
        AssertOutputCompiles(run.Output);
        var output = Normalized(run.Output);
        Assert.Contains("var __cs2jTa0 = new Dur(a, b, c, false);", output);
        Assert.Contains("this.A = __cs2jTa0.A;", output);
        Assert.Contains("this.C = __cs2jTa0.C;", output);
        Assert.DoesNotContain(StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Dur")!;
        var whole = Activator.CreateInstance(type, 1, 2, 3, true);
        Assert.Equal(1, type.GetField("A")!.GetValue(whole));
        Assert.Equal(2, type.GetField("B")!.GetValue(whole));
        Assert.Equal(3, type.GetField("C")!.GetValue(whole));
        var split = Activator.CreateInstance(type, 4, 5, 6, false);
        Assert.Equal(6, type.GetField("C")!.GetValue(split));
    }

    [Fact]
    public void Constructor_ThisAssignDefault_ExpandedAndRuntimeZeroed()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Y;
                public Box(int x, bool reset)
                {
                    X = x;
                    Y = x;
                    if (reset) this = default;
                }
            }
            """);

        AssertOutputCompiles(run.Output);
        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var reset = Activator.CreateInstance(type, 7, true);
        Assert.Equal(0, type.GetField("X")!.GetValue(reset));
        Assert.Equal(0, type.GetField("Y")!.GetValue(reset));
        var kept = Activator.CreateInstance(type, 7, false);
        Assert.Equal(7, type.GetField("Y")!.GetValue(kept));
    }

    [Fact]
    public void Constructor_EarlyThrowAfterFieldWrites_CallerStorageUntouched()
    {
        // C# 语义：new S(...) 在临时存储执行构造，抛错时目标槽保留旧值；
        // 字段式展开（直写 this 存储）保持该语义。
        var run = Analyze("""
            namespace Lib;
            using System;
            public struct Box
            {
                public int A;
                public int B;
                public Box(int mode)
                {
                    A = 0; B = 0;
                    if (mode == 1)
                    {
                        this = default;
                        A = 100;
                        throw new InvalidOperationException("boom");
                    }
                    A = 1; B = 2;
                }
            }
            """);

        AssertOutputCompiles(run.Output);
        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var slot = Activator.CreateInstance(type);
        type.GetField("A")!.SetValue(slot, 111);
        type.GetField("B")!.SetValue(slot, 222);
        var ex = Assert.ThrowsAny<Exception>(() => Activator.CreateInstance(type, 1));
        Assert.Contains("boom", ex.InnerException?.Message ?? ex.Message);
        Assert.Equal(111, type.GetField("A")!.GetValue(slot));
        Assert.Equal(222, type.GetField("B")!.GetValue(slot));
    }

    [Fact]
    public void Constructor_ReadonlyFieldAndGetOnlyAutoProperty_ExpansionCompiles()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public readonly int R;
                public int P { get; }
                public int M;
                public Box(int r, int p, int m, bool whole)
                {
                    if (whole)
                    {
                        this = new Box(r + 1, p + 1, m + 1, false);
                    }
                    else
                    {
                        R = r; P = p; M = m;
                    }
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        AssertOutputCompiles(run.Output);
        var output = Normalized(run.Output);
        Assert.Contains("this.R = __cs2jTa0.R;", output);
        Assert.Contains("this.P = __cs2jTa0.P;", output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var whole = Activator.CreateInstance(type, 1, 2, 3, true);
        // 展开右值为 new Box(2, 3, 4, false)：R=2、P=3、M=4。
        Assert.Equal(2, type.GetField("R")!.GetValue(whole));
        Assert.Equal(3, type.GetProperty("P")!.GetValue(whole));
        Assert.Equal(4, type.GetField("M")!.GetValue(whole));
    }

    // ---------- 访问器形态：字段式展开 ----------

    [Fact]
    public void PropertySetter_ThisAssignDefault_ExpandedAndRuntimeZeroedThenSet()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int Y;
                public int P
                {
                    get => Y;
                    set { this = default; Y = value; }
                }
            }
            """);

        Assert.True(Plan(run).IsActive);
        Assert.Equal(1, run.Statistics.ThisAssignmentMembersExpanded);
        AssertOutputCompiles(run.Output);
        var output = Normalized(run.Output);
        Assert.Contains("var __cs2jTa0 = default(global::Lib.Box);", output);
        Assert.Contains("this.X = __cs2jTa0.X;", output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(type)!;
        type.GetField("X")!.SetValue(instance, 8);
        type.GetProperty("P")!.SetValue(instance, 17);
        Assert.Equal(0, type.GetField("X")!.GetValue(instance));
        Assert.Equal(17, type.GetField("Y")!.GetValue(instance));
    }

    [Fact]
    public void IndexerAndEventAccessors_ThisAssign_ExpandedAndRuntimeObservable()
    {
        var run = Analyze("""
            namespace Lib;
            using System;
            public struct Box
            {
                private Action _e;
                public int X;
                public int this[int i]
                {
                    get => X;
                    set { this = default; X = value; }
                }
                public event Action E
                {
                    add { this = default; _e = (Action)Delegate.Combine(_e, value); }
                    remove { _e = (Action)Delegate.Remove(_e, value); }
                }
                public void Fire() => _e?.Invoke();
            }
            """);

        Assert.True(Plan(run).IsActive);
        Assert.Equal(2, run.Statistics.ThisAssignmentMembersExpanded);
        AssertOutputCompiles(run.Output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(type)!;
        type.GetProperty("Item")!.SetValue(instance, 23, new object[] { 0 });
        Assert.Equal(23, type.GetField("X")!.GetValue(instance));

        var fired = false;
        Action handler = () => fired = true;
        var eEvent = type.GetEvent("E")!;
        eEvent.AddEventHandler(instance, handler);
        Assert.Equal(0, type.GetField("X")!.GetValue(instance));
        type.GetMethod("Fire")!.Invoke(instance, null);
        Assert.True(fired);
    }

    [Fact]
    public void ExpressionBodiedAccessor_ThisAssign_ExpandedIntoBlockBody()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int P { set => this = default; }
            }
            """);

        Assert.True(Plan(run).IsActive);
        AssertOutputCompiles(run.Output);
        var output = Normalized(run.Output);
        Assert.Contains("set", output);
        Assert.Contains("var __cs2jTa0 = default(global::Lib.Box);", output);
        Assert.DoesNotContain("set =>", output);
    }

    // ---------- base 访问方法：字段式展开 ----------

    [Fact]
    public void MethodWithBaseCall_ThisAssignStatement_ExpandedFieldByField()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public string Label()
                {
                    this = new Box { X = 0 };
                    return base.ToString()!;
                }
            }
            """);

        var plan = Plan(run);
        Assert.Empty(plan.ThisCaptureRewrites);
        Assert.Single(plan.ThisAssignmentExpansions);
        Assert.Equal(1, run.Statistics.ThisAssignmentMembersExpanded);
        AssertOutputCompiles(run.Output);
        var output = Normalized(run.Output);
        // NormalizeWhitespace 会把对象初始化器展开为多行，按片段断言。
        Assert.Contains("var __cs2jTa0 = new Box", output);
        Assert.Contains("this.X = __cs2jTa0.X;", output);
        Assert.DoesNotContain("__cs2jTc", output);

        var assembly = EmitAssembly(run.Output);
        var type = assembly.GetType("Lib.Box")!;
        var instance = Activator.CreateInstance(type)!;
        type.GetField("X")!.SetValue(instance, 5);
        var label = (string)type.GetMethod("Label")!.Invoke(instance, null)!;
        Assert.Equal("Lib.Box", label);
        Assert.Equal(0, type.GetField("X")!.GetValue(instance));
    }

    // ---------- 嵌套 struct ----------

    [Fact]
    public void NestedStruct_DisposePattern_HolderRewriteRuntimeResetsRental()
    {
        // RegexCompiler.RentedLocalBuilder 形态：嵌套 struct 方法归还池后 this = default。
        var run = Analyze("""
            namespace Lib;
            using System.Collections.Generic;
            public class Compiler
            {
                private struct Rented
                {
                    public Stack<int> Pool;
                    public int Local;
                    public void Dispose()
                    {
                        Pool.Push(Local);
                        this = default;
                    }
                }
            }
            """);

        var plan = Plan(run, "Lib.Compiler.Rented");
        Assert.True(plan.IsActive);
        Assert.Single(plan.ThisCaptureRewrites);
        AssertOutputCompiles(run.Output);

        var assembly = EmitAssembly(run.Output);
        var compilerType = assembly.GetType("Lib.Compiler")!;
        var rentedType = assembly.GetType("Lib.Compiler+Rented")!;
        var compiler = Activator.CreateInstance(compilerType)!;
        var pool = new System.Collections.Generic.Stack<int>();
        pool.Push(99);
        var rented = Activator.CreateInstance(rentedType)!;
        rentedType.GetField("Pool")!.SetValue(rented, pool);
        rentedType.GetField("Local")!.SetValue(rented, 5);
        rentedType.GetMethod("Dispose")!.Invoke(rented, null);
        Assert.Equal(5, pool.Peek());
        Assert.Null(rentedType.GetField("Pool")!.GetValue(rented));
        Assert.Equal(0, rentedType.GetField("Local")!.GetValue(rented));
    }

    // ---------- 残余无解形态：保持显式 SV041 ----------

    [Fact]
    public void AccessorWithReadonlyField_ThisAssign_StaysSV041()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public readonly int R;
                public int X;
                public Box(int r) { R = r; X = 0; }
                public int P { set { this = default; } }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV041);
    }

    [Fact]
    public void Constructor_ThisAssignInExpressionPosition_StaysSV041()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public Box(int a)
                {
                    var y = (this = new Box { X = a }).X;
                    X = y;
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV041);
    }

    [Fact]
    public void MethodWithBaseCall_ThisAssignInExpressionPosition_StaysSV041()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public int M(Box a, Box b)
                {
                    string s = base.ToString()!;
                    a = this = b;
                    return X;
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV041);
    }

    [Fact]
    public void MethodWithBaseCallAndReadonlyField_ThisAssign_StaysSV041()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public readonly int R;
                public Box(int r) { R = r; }
                public void Reset()
                {
                    string s = base.ToString()!;
                    this = default;
                }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV041);
    }

    [Fact]
    public void StaticMember_ThisAssign_StaysSV041()
    {
        var run = Analyze("""
            namespace Lib;
            public struct Box
            {
                public int X;
                public static void Reset() { this = default; }
            }
            """);
        AssertInfeasible(run, StructValueSemanticsDiagnosticCode.SV041);
    }

    // ---------- 幂等 ----------

    [Theory]
    [InlineData("""
        namespace Lib;
        public struct Box
        {
            public int X;
            public void Reset() { this = default; }
        }
        """)]
    [InlineData("""
        namespace Lib;
        public struct Box
        {
            public int X;
            public int Y;
            public Box(int x, int y, bool whole)
            {
                X = x; Y = y;
                if (whole) this = new Box(0, 0, false);
            }
        }
        """)]
    [InlineData("""
        namespace Lib;
        public struct Box
        {
            public int X;
            public int P { set { this = default; X = value; } }
        }
        """)]
    public void RewriteIsIdempotent_SecondRunProducesNoChange(string source)
    {
        var first = Analyze(source);
        AssertOutputCompiles(first.Output);
        var second = Analyze(first.Output);
        Assert.False(second.Changed);
        Assert.Equal(Normalized(first.Output), Normalized(second.Output));
    }
}
