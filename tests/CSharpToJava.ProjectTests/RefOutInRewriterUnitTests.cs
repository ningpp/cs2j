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
}
