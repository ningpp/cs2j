using System.Reflection;
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

        var outTree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var compilation = CSharpCompilation.Create("refout-verify",
            new[] { outTree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var violations = RefOutInPostVerifier.Verify(new[] { outTree }, compilation);
        Assert.Empty(violations);
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
    public void Lambda_RefParam_RewrittenForInternalDelegate()
    {
        var src = @"
public class C
{
    public delegate void Bump(ref int v);
    public void Use()
    {
        Bump b = (ref int x) => { x = x + 1; };
        int n = 0;
        b(ref n);
    }
}";
        var result = RewriteAndAssertCompiles(src);
        Assert.Contains("ByRef<int> x", result);
        Assert.Contains("x.Value = x.Value + 1", result);
        Assert.Contains("b(__ref", result);   // slot direct pass at the delegate call site
    }

    [Fact]
    public void RefField_ExplicitError_CS2J3303()
    {
        var src = "public class C { ref int _f; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.Contains(result.Diagnostics,
            d => d.Id == "CS2J3303" && d.Severity == RefOutInSeverity.Error);
    }

    [Fact]
    public void MultiDimArrayRef_ExplicitError_CS2J3303()
    {
        var src = @"
public class C
{
    public void M(ref int v) { }
    public void Use() { int[,] grid = new int[2, 2]; M(ref grid[0, 1]); }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.Contains(result.Diagnostics, d => d.Id == "CS2J3303");
    }

    [Fact]
    public void Verifier_FlagsInternalRefParamResidue()
    {
        var tree = CSharpSyntaxTree.ParseText("public class C { public void M(ref int a) { } }");
        var compilation = CSharpCompilation.Create("v", new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var violations = RefOutInPostVerifier.Verify(new[] { tree }, compilation);
        Assert.Contains(violations, v => v.Message.Contains("still declares ref/out"));
    }

    [Fact]
    public void Verifier_AllowsExternalRefArguments()
    {
        var tree = CSharpSyntaxTree.ParseText(@"
public class C
{
    public void M(string s, out int r) { r = 0; }
    public void Use() { M(""x"", out var v); }
}");
        var compilation = CSharpCompilation.Create("v", new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var violations = RefOutInPostVerifier.Verify(new[] { tree }, compilation);
        Assert.DoesNotContain(violations, v => v.Message.Contains("ref expression"));
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
        Assert.Contains("ByRef<int> __out", result);
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
        Assert.Contains("ByRef<int> __ref", result);   // ref → accessor proxy
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
        Assert.Contains("ByRef<int> __out", result);
        Assert.Contains("return __cond", result);
    }

    // ── C40 M3 hard prerequisite: declaration-site receiver classification ──
    // Probe evidence (Temp/cs2j-m3-probe): closures over Span locals fail
    // CS8175 (local) / CS9108 (parameter); writing a readonly proxy fails
    // CS0200. Four classes: array -> ByRef.Create(arr,i); Span-like / other
    // ref-struct receiver -> ScopedByRef (ref field, no closure); heap
    // receiver -> accessor lambdas; readonly forms -> ReadOnly* types.

    private static readonly MetadataReference[] DeclSiteRefs =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
    };

    private static (MetadataReference Reference, byte[] Image) CompilePeerToMetadata(string peerSrc)
    {
        // Peer types model *external/metadata* members (their ref indexers
        // are NOT rewritten by the pass under test), so the accessor/Scoped
        // classifications stay in effect. Source-defined members living in
        // the processed tree are holder-to-holder flow and are covered by
        // the ForeachSourceC40ProjectTests M3 chain.
        var peerCompilation = CSharpCompilation.Create(
            "refout-declsite-peer-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(peerSrc) },
            DeclSiteRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var peerEmit = peerCompilation.Emit(stream);
        Assert.True(peerEmit.Success, string.Join("\n",
            peerEmit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())));
        var image = stream.ToArray();
        return (MetadataReference.CreateFromImage(image), image);
    }

    private static (RefOutInRewriterResult result, string errorText) RewriteAndCompile(
        string src, string? peerSrc = null)
    {
        (MetadataReference Reference, byte[] Image)? peer =
            peerSrc == null ? null : CompilePeerToMetadata(peerSrc);
        var refs = peer == null
            ? DeclSiteRefs
            : DeclSiteRefs.Append(peer.Value.Reference).ToArray();
        var srcTree = CSharpSyntaxTree.ParseText(src);
        var modelCompilation = CSharpCompilation.Create("refout-declsite-model",
            new[] { srcTree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var result = new RefOutInRewriter().Rewrite(src, modelCompilation.GetSemanticModel(srcTree));

        var supportTree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(FindRepoRoot("src/Cs2jRefOutIn/ByRef.cs")));
        var outTrees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(result.OutputCode),
            supportTree,
        };
        var compilation = CSharpCompilation.Create("refout-declsite",
            outTrees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return (result, string.Join("\n", errors.Select(d => d.ToString())));
    }

    private static int RewriteCompileAndRun(string src, string? peerSrc = null)
    {
        var (result, errorText) = RewriteAndCompile(src, peerSrc);
        (MetadataReference Reference, byte[] Image)? peer =
            peerSrc == null ? null : CompilePeerToMetadata(peerSrc);
        var refs = peer == null
            ? DeclSiteRefs
            : DeclSiteRefs.Append(peer.Value.Reference).ToArray();
        var supportTree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(FindRepoRoot("src/Cs2jRefOutIn/ByRef.cs")));
        var outTrees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(result.OutputCode),
            supportTree,
        };
        var compilation = CSharpCompilation.Create("refout-declsite-run",
            outTrees, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join("\n",
            emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())) + "\n" + errorText + "\n===== OUTPUT =====\n" + result.OutputCode);

        var peerAssembly = peer == null ? null : Assembly.Load(peer.Value.Image);
        ResolveEventHandler? peerResolver = peerAssembly == null
            ? null
            : (_, e) => e.Name == peerAssembly.FullName ? peerAssembly : null;
        if (peerResolver != null)
            AppDomain.CurrentDomain.AssemblyResolve += peerResolver;
        try
        {
            var assembly = Assembly.Load(stream.ToArray());
            var method = assembly.GetType("C", throwOnError: true)!
                .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
            return (int)method.Invoke(null, null)!;
        }
        finally
        {
            if (peerResolver != null)
                AppDomain.CurrentDomain.AssemblyResolve -= peerResolver;
        }
    }

    [Fact]
    public void RefLocal_ArrayElement_KeepsArrayFactory()
    {
        var src = @"
public class C
{
    public static int Run()
    {
        int[] arr = { 1, 2, 3 };
        ref int x = ref arr[0];
        x = 9;
        return arr[0] * 100 + x;
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.Contains("ByRef<int> x = ByRef<int>.Create(arr, 0);", result.OutputCode);
        Assert.Equal(1, result.Statistics.ArrayElementProxies);
        Assert.Equal(909, RewriteCompileAndRun(src));
    }

    [Fact]
    public void RefLocal_SpanElement_UsesScopedByRefFactory_AndWritesThrough()
    {
        var src = @"
using System;
public class C
{
    public static int Run()
    {
        Span<int> span = new int[] { 1, 2, 3 };
        ref int x = ref span[1];
        x = 9;
        return span[1] * 100 + x;
    }
}";
        var (result, errorText) = RewriteAndCompile(src);
        Assert.Equal("", errorText);
        Assert.Contains("ScopedByRef<int> x = ScopedByRef<int>.Create(span, 1);", result.OutputCode);
        Assert.DoesNotContain("() => span", result.OutputCode);   // no closure over span
        Assert.Equal(1, result.Statistics.ScopedElementProxies);
        Assert.Equal(909, RewriteCompileAndRun(src));
    }

    [Fact]
    public void RefLocal_SpanElement_InsideForLoop_EachIterationDoubles()
    {
        var src = @"
using System;
public class C
{
    public static int Run()
    {
        Span<int> span = new int[] { 1, 2, 3, 4 };
        for (int i = 0; i < span.Length; i++)
        {
            ref int x = ref span[i];
            x = x * 2;
        }
        int sum = 0;
        for (int i = 0; i < span.Length; i++) sum += span[i];
        return sum;
    }
}";
        var (result, errorText) = RewriteAndCompile(src);
        Assert.Equal("", errorText);
        Assert.Contains("ScopedByRef<int>.Create(span, i)", result.OutputCode);
        Assert.Equal(20, RewriteCompileAndRun(src));
    }

    [Fact]
    public void RefLocal_RefVarOverSpan_ResolvesConcreteTypeArgument()
    {
        var src = @"
using System;
public class C
{
    public static int Run()
    {
        Span<int> span = new int[] { 1, 2, 3 };
        ref var x = ref span[1];
        x = 9;
        return span[1];
    }
}";
        var (result, errorText) = RewriteAndCompile(src);
        Assert.Equal("", errorText);
        Assert.Contains("ScopedByRef<int>", result.OutputCode);
        Assert.DoesNotContain("ScopedByRef<var>", result.OutputCode);
        Assert.Equal(9, RewriteCompileAndRun(src));
    }

    [Fact]
    public void RefReadonlyLocal_SpanElement_UsesReadOnlyScopedByRef()
    {
        var src = @"
using System;
public class C
{
    public static int Run()
    {
        Span<int> span = new int[] { 1, 2, 33 };
        ref readonly int x = ref span[2];
        return x;
    }
}";
        var (result, errorText) = RewriteAndCompile(src);
        Assert.Equal("", errorText);
        Assert.Contains("ReadOnlyScopedByRef<int> x = ReadOnlyScopedByRef<int>.Create(span, 2);",
            result.OutputCode);
        Assert.Equal(1, result.Statistics.ScopedElementProxies);
        Assert.Equal(0, result.Statistics.ReadOnlyProxies);
        Assert.Equal(33, RewriteCompileAndRun(src));
    }

    [Fact]
    public void RefReadonlyLocal_ReadOnlySpanElement_UsesRosOverload()
    {
        var src = @"
using System;
public class C
{
    public static int Run()
    {
        ReadOnlySpan<int> span = new int[] { 1, 2, 34 };
        ref readonly int x = ref span[2];
        return x;
    }
}";
        var (result, errorText) = RewriteAndCompile(src);
        Assert.Equal("", errorText);
        Assert.Contains("ReadOnlyScopedByRef<int>.Create(span, 2)", result.OutputCode);
        Assert.Equal(1, result.Statistics.ScopedElementProxies);
        Assert.Equal(34, RewriteCompileAndRun(src));
    }

    [Fact]
    public void RefReadonlyLocal_ArrayElement_UsesReadOnlyAccessor()
    {
        var src = @"
public class C
{
    public static int Run()
    {
        int[] arr = { 1, 2, 35 };
        ref readonly int x = ref arr[2];
        return x;
    }
}";
        var (result, errorText) = RewriteAndCompile(src);
        Assert.Equal("", errorText);
        Assert.Contains("ReadOnlyByRef<int> x = ReadOnlyByRef<int>.Create(() => arr[2]);",
            result.OutputCode);
        Assert.Equal(1, result.Statistics.ReadOnlyProxies);
        Assert.Equal(35, RewriteCompileAndRun(src));
    }

    [Fact]
    public void RefLocal_HeapIndexer_UsesAccessorLambdaProxy()
    {
        var peer = @"
public sealed class Box
{
    private readonly int[] _data = new int[4];
    public ref int this[int i] { get { return ref _data[i]; } }
}";
        var src = @"
public class C
{
    public static int Run()
    {
        var box = new Box();
        ref int x = ref box[0];
        x = 41;
        return box[0] + 1;
    }
}";
        var (result, errorText) = RewriteAndCompile(src, peer);
        Assert.Equal("", errorText);
        Assert.Contains("ByRef<int>.Create(() => box[0], v => box[0] = v)", result.OutputCode);
        Assert.Equal(1, result.Statistics.AccessorProxies);
        Assert.Equal(42, RewriteCompileAndRun(src, peer));
    }

    [Fact]
    public void RefLocal_CustomRefStructIndexer_UsesRefScopedForm()
    {
        var peer = @"
public ref struct CustomList
{
    private int[] _a;
    public CustomList(int[] a) { _a = a; }
    public ref int this[int i] { get { return ref _a[i]; } }
    public int Count => _a.Length;
}";
        var src = @"
public class C
{
    public static int Run()
    {
        CustomList col = new CustomList(new[] { 1, 2, 3 });
        ref int x = ref col[1];
        x = 77;
        int sum = 0;
        for (int i = 0; i < col.Count; i++) sum += col[i];
        return sum;
    }
}";
        var (result, errorText) = RewriteAndCompile(src, peer);
        Assert.Equal("", errorText);
        Assert.Contains("ScopedByRef<int> x = ScopedByRef<int>.Create(ref col[1]);",
            result.OutputCode);
        Assert.Equal(81, RewriteCompileAndRun(src, peer));
    }

    [Fact]
    public void RefLocal_MultiDimArrayElement_ReportsCS2J3303()
    {
        var src = @"
public class C
{
    public static int Run()
    {
        int[,] grid = new int[2, 2];
        ref int x = ref grid[0, 1];
        x = 5;
        return grid[0, 1];
    }
}";
        var (tree, model) = ParseWithSemantics(src);
        var result = new RefOutInRewriter().Rewrite(src, model);
        Assert.Contains(result.Diagnostics, d => d.Id == "CS2J3303");
    }

    [Fact]
    public void ReadOnlyProxies_CannotBeWritten_CompilerReportsCS0200()
    {
        // Span receiver: ReadOnlyScopedByRef.Value is get-only.
        var spanSrc = @"
using System;
public class C
{
    public static int Run()
    {
        Span<int> span = new int[] { 1, 2, 33 };
        ref readonly int x = ref span[2];
        return x;
    }
}";
        var (spanResult, _) = RewriteAndCompile(spanSrc);
        var injected = spanResult.OutputCode.Replace("return x.Value;", "x.Value = 9; return x.Value;");
        var supportTree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(FindRepoRoot("src/Cs2jRefOutIn/ByRef.cs")));
        var compilation = CSharpCompilation.Create("refout-readonly-lock",
            new[] { CSharpSyntaxTree.ParseText(injected), supportTree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Contains(compilation.GetDiagnostics(), d => d.Id == "CS0200");

        // Heap receiver: ReadOnlyByRef.Value is get-only as well.
        var heapSrc = @"
public class C
{
    public static int Run()
    {
        int[] arr = { 1, 2, 35 };
        ref readonly int x = ref arr[2];
        return x;
    }
}";
        var (heapResult, _) = RewriteAndCompile(heapSrc);
        var heapInjected = heapResult.OutputCode.Replace("return x.Value;", "x.Value = 9; return x.Value;");
        var heapCompilation = CSharpCompilation.Create("refout-readonly-lock-heap",
            new[] { CSharpSyntaxTree.ParseText(heapInjected), supportTree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        Assert.Contains(heapCompilation.GetDiagnostics(), d => d.Id == "CS0200");
    }
}
