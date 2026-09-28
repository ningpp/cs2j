using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class RefOutInRewriterUnitTests
{
    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("refout-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (tree, compilation.GetSemanticModel(tree));
    }

    [Fact]
    public void CleanFile_NoRefOutIn_ReturnedUnchanged()
    {
        var src = "public class C { public int Value; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void RefParam_InternalMethod_BecomesByRef()
    {
        var src = @"
public class C
{
    public void M(ref int a)
    {
        a = 42;
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("ByRef<int> a", result.OutputCode);
        Assert.Contains("a.Value = 42", result.OutputCode);
        Assert.Equal(1, result.Statistics.RefParamsRewritten);
    }

    [Fact]
    public void OutParam_InternalMethod_BecomesByRef()
    {
        var src = @"
public class C
{
    public bool TryGet(out int result)
    {
        result = 42;
        return true;
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("ByRef<int> result", result.OutputCode);
        Assert.Contains("result.Value = 42", result.OutputCode);
        Assert.Equal(1, result.Statistics.OutParamsRewritten);
    }

    [Fact]
    public void InParam_InternalMethod_StrippedToByValue()
    {
        var src = @"
public class C
{
    public void M(in int a)
    {
        int x = a;
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.DoesNotContain("in int", result.OutputCode);
        Assert.Equal(1, result.Statistics.InByValueParams);
    }

    [Fact]
    public void ScopedParam_Stripped()
    {
        var src = @"
public class C
{
    public void M(scoped ref int a)
    {
        a = 42;
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.DoesNotContain("scoped", result.OutputCode);
        Assert.Equal(1, result.Statistics.ScopedRemoved);
    }

    [Fact]
    public void CallSite_InternalRefArg_UsesAccessorProxy()
    {
        var src = @"
public class C
{
    public void M(ref int a) { a = 10; }
    public void Use()
    {
        int x = 5;
        M(ref x);
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("ByRef<int>.Create", result.OutputCode);
    }

    [Fact]
    public void CallSite_InternalOutArg_LiftsWithWriteBack()
    {
        var src = @"
public class C
{
    public void M(out int a) { a = 10; }
    public void Use()
    {
        int x;
        M(out x);
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("ByRef<int>.Create", result.OutputCode);
    }

    [Fact]
    public void CallSite_ExternalRefArg_PreservedAsIs()
    {
        var src = @"
public class C
{
    public void Use()
    {
        int x = 5;
        int.TryParse(""42"", out x);
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        // int.TryParse is external — out preserved
        Assert.Contains("out x", result.OutputCode);
    }

    [Fact]
    public void CallSite_HolderSlotDirectPass_StripsRefOutModifier()
    {
        var src = @"
public class C
{
    public void Inner(ref int a) { a = 10; }
    public void Outer(ref int a)
    {
        Inner(ref a);
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        // Inner(ref a) → Inner(a) — slot direct pass
        Assert.Contains("Inner(a)", result.OutputCode);
    }

    [Fact]
    public void CallSite_ArrayElementRef_UsesArrayProxy()
    {
        var src = @"
public class C
{
    public void M(ref int a) { a = 10; }
    public void Use(int[] arr)
    {
        M(ref arr[0]);
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("ByRef<int>.Create(arr, 0)", result.OutputCode);
    }

    [Fact]
    public void Delegate_RefParam_RewrittenToByRef()
    {
        var src = @"
public delegate void D(ref int a);

public class C
{
    public void M(ref int a) { a = 42; }
    public void Use()
    {
        D d = M;
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("delegate void D(ByRef<int> a)", result.OutputCode);
    }

    [Fact]
    public void InterfaceAndImpl_Internal_BothRewritten()
    {
        var src = @"
public interface ICalculator
{
    void Calculate(ref int value);
}

public class Calculator : ICalculator
{
    public void Calculate(ref int value) { value = 42; }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("ByRef<int> value", result.OutputCode);
    }

    [Fact]
    public void Generic_RefParam_RewrittenWithGenericHolder()
    {
        var src = @"
public class C<T>
{
    public void M(ref T a) { }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("ByRef<T> a", result.OutputCode);
    }

    [Fact]
    public void DllImportMethod_Locked_NotRewritten()
    {
        var src = @"
using System.Runtime.InteropServices;

public class C
{
    [DllImport(""kernel32"")]
    public static extern void M(ref int a);
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        // Locked: ref preserved
        Assert.Contains("ref int a", result.OutputCode);
        Assert.True(result.Statistics.LockedContractMembers > 0);
    }

    [Fact]
    public void ExternalBCLCall_RefOut_Preserved()
    {
        var src = @"
using System.Collections.Generic;

public class C
{
    public void Use()
    {
        var dict = new Dictionary<string, int>();
        dict.TryGetValue(""key"", out int v);
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        // Dictionary.TryGetValue is external — out preserved
        Assert.Contains("out int v", result.OutputCode);
    }

    [Fact]
    public void PostVerify_RewrittenFile_NoInternalRefOut()
    {
        var src = @"
public class C
{
    public void M(ref int a) { a = 42; }
    public void Use()
    {
        int x = 5;
        M(ref x);
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);

        var verifyResult = RefOutInPostVerifier.Verify(result.OutputCode);
        Assert.True(verifyResult.IsClean, "Internal ref/out should be eliminated");
        Assert.True(verifyResult.HasCs2jByRefUsing, "using Cs2jByRef should be injected");
    }

    // ── Task 3: real-compile harness + statement-level lifting ─────────────

    private static string RewriteAndAssertCompiles(string src, bool expectChanged = true)
    {
        var byRefSrc = File.ReadAllText(FindRepoRoot("src/Cs2jRefOutIn/ByRef.cs"));
        var refStructSrc = File.ReadAllText(FindRepoRoot("src/Cs2jRefOutIn/RefStructByRef.cs"));
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.Equal(expectChanged, result.Changed);

        var outTree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var supportTree = CSharpSyntaxTree.ParseText(byRefSrc);
        var refStructTree = CSharpSyntaxTree.ParseText(refStructSrc);
        var compilation = CSharpCompilation.Create("refout-verify",
            new[] { outTree, supportTree, refStructTree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "output must compile: " + string.Join("; ", errors.Select(d => d.ToString()))
            + "\n===== OUTPUT =====\n" + result.OutputCode);
        return result.OutputCode;
    }

    internal static string FindRepoRoot(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, relative)))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException($"repo root with '{relative}' not found");
        return Path.Combine(dir.FullName, relative);
    }

    [Fact]
    public void RefReturn_ArrayElement_BecomesByRefFactory()
    {
        var src = @"
public class C
{
    public ref int GetRef(int[] arr, int index) { return ref arr[index]; }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("public ByRef<int> GetRef(int[] arr, int index)", result);
        Assert.Contains("return ByRef<int>.Create(arr, index);", result);
    }

    [Fact]
    public void RefProperty_DeclarationAndBodyRewritten()
    {
        var src = @"
public class C
{
    private int _f;
    public ref int P
    {
        get { return ref _f; }
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("ByRef<int> P", result);
        Assert.Contains("Create(() => _f", result);
    }

    [Fact]
    public void RefProperty_UseSite_DirectPass()
    {
        var src = @"
public class C
{
    private int _f;
    public ref int P => ref _f;
    public void Bump(ref int v) { v++; }
    public void Use() { Bump(ref P); }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("Bump(P)", result);   // P already returns ByRef<int>
    }

    [Fact]
    public void RefLocal_Rebinding_ReplacesProxy()
    {
        var src = @"
public class C
{
    public ref int GetRef(int[] arr, int index) { return ref arr[index]; }
    public int Use()
    {
        int[] arr = { 1, 2, 3 };
        ref int r = ref GetRef(arr, 0);
        r = ref arr[2];
        r = 99;
        return arr[2];
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("r = ByRef<int>.Create(arr, 2);", result);
        Assert.Contains("r.Value = 99;", result);
    }

    [Fact]
    public void InOverloadCollision_KeepsIn_AndWarns()
    {
        var src = @"
public class C
{
    public void M(int a) { }
    public void M(in int a) { }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("in int a", result.OutputCode);
        Assert.Equal(1, result.Statistics.InOverloadKept);
        Assert.Contains(result.Diagnostics, d => d.Id == "CS2J3302");
    }

    [Fact]
    public void PureCallSiteFile_GetsUsingInjection()
    {
        var src = @"
public class Caller
{
    public void Use()
    {
        int x = 1;
        Other.Bump(ref x);
    }
}
public class Other { public static void Bump(ref int v) { v++; } }";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("using Cs2jByRef;", result);
    }

    [Fact]
    public void RefStructParam_UsesRefStructByRefSlot()
    {
        var src = @"
using System;
public class C
{
    public void M(ref Span<int> s) { s = s.Slice(1); }
    public int Use()
    {
        Span<int> span = new int[] { 1, 2, 3 };
        M(ref span);
        return span[0];
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("Cs2jByRef.RefStructByRef<System.Span<int>> s", result);
        Assert.Contains(".Create(span)", result);
        Assert.Contains("span = __ref", result);   // copy-out read-back
    }

    [Fact]
    public void OutVar_InIfCondition_HoistedAndReadBack()
    {
        var src = @"
public class C
{
    public bool TryGet(out int r) { r = 42; return true; }
    public int Use()
    {
        if (TryGet(out var v)) { return v; }
        return 0;
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("var __out", result);
        Assert.Contains("v = __out", result);
    }

    [Fact]
    public void RefCall_InAssignmentRhs_Hoisted()
    {
        var src = @"
public class C
{
    public int Bump2(ref int v) { v++; return v; }
    public int Use()
    {
        int x = 1;
        int y = 5 + Bump2(ref x);
        return x + y;
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("ByRef<int>.Create(() => x, v => x = v)", result);
        Assert.Contains("var __ref", result);   // ref → accessor proxy
    }

    [Fact]
    public void MemberChain_RefArg_AccessorProxy()
    {
        var src = @"
public class H { public int Value; public void Update(ref int v) { v++; } }
public class C
{
    public void Use()
    {
        var h = new H();
        h.Value = 1;
        h.Update(ref h.Value);
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("Create(() => h.Value, v => h.Value = v)", result);
    }

    [Fact]
    public void ShortCircuit_RightSideNotUnconditionallyExecuted()
    {
        var src = @"
public class C
{
    public bool Flag;
    public bool TryGet(out int r) { r = 1; return true; }
    public int Use()
    {
        if (Flag && TryGet(out var v)) { return v; }
        return 0;
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("if (Flag)", result);   // right side wrapped in a conditional block
    }

    [Fact]
    public void WhileCondition_OutCall_RestructuredToWhileTrue()
    {
        var src = @"
public class C
{
    public int N;
    public bool Next(out int r) { r = N; return N-- > 0; }
    public int Drain()
    {
        int total = 0;
        while (Next(out var v))
        {
            total += v;
        }
        return total;
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("while (true)", result);
        Assert.Contains("if (!", result);
    }

    [Fact]
    public void ReturnStatement_OutCall_HoistedBeforeReturn()
    {
        var src = @"
public class C
{
    public bool TryGet(out int r) { r = 7; return true; }
    public int Use()
    {
        return TryGet(out var v) ? v : 0;
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("var __out", result);
        Assert.Contains("return __cond", result);
    }
}
