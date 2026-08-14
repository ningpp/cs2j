# Unsigned Type Rewriter Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the C#→C# unsigned-type (byte/ushort/uint/ulong) preprocessing pipeline described in `prompts/unsigned-type-rewriter-prompt.md`, with a Core rewriter, project preprocessor, CLI verb, convert-project integration, fixture projects, and tests.

**Architecture:** A Roslyn `CSharpSyntaxRewriter` replaces unsigned keyword types with `System.*` full names and rewrites operators/casts into `Cs2jUnsigned.UnsignedExtensions` calls using a `SemanticModel`; a CLI preprocessor copies the project into `.cs2j-unsigned-src`, runs the rewriter per file against one compilation, writes `_Cs2jUnsignedExtensions.cs` when needed, and restores. Ordering in `convert-project`: UnsignedType → MakeReadOnly → EliminateGoto.

**Tech Stack:** .NET 10, Roslyn (Microsoft.CodeAnalysis.CSharp 4.12), CommandLineParser, xUnit, C# SDK-style fixture projects.

---

## Task 1: Fixture projects + failing tests (RED)

**Files:**
- Create: `tests/SampleUnsignedSolution/SampleUnsignedSolution.sln`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/SampleUnsignedLib.csproj`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedTypes.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedOperators.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedCasts.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedArrays.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedGenerics.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedLiterals.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedConst.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedEnum.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedRefOut.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedNullable.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedStaticMembers.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedLambda.cs`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedApp/SampleUnsignedApp.csproj`
- Create: `tests/SampleUnsignedSolution/SampleUnsignedApp/Program.cs`
- Create: `tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs`
- Create: `tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs`
- Modify: `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`
- Modify: `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj` (InternalsVisibleTo)

### Step 1.1: Lib csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

### Step 1.2: App csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SampleUnsignedLib\SampleUnsignedLib.csproj" />
  </ItemGroup>
</Project>
```

### Step 1.3: Solution file

Same layout as `tests/SampleRefOutInSolution/SampleRefOutIn.sln`, with project GUIDs `{AAAAAAAA-...}` and `{BBBBBBBB-...}` replaced by `{CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC}` (Lib) and `{DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD}` (App), names `SampleUnsignedLib` / `SampleUnsignedApp`.

### Step 1.4: Fixture C# files

`UnsignedTypes.cs`:
```csharp
namespace SampleUnsignedLib;

public sealed class UnsignedTypes
{
    public byte ByteField;
    public ushort UShortField;
    public uint UIntField;
    public ulong ULongField;

    public byte ByteProp { get; set; }
    public ushort UShortProp { get; set; }
    public uint UIntProp { get; set; }
    public ulong ULongProp { get; set; }

    public uint Echo(uint value) => value;
    public ulong Echo(ulong value) => value;
    public ushort Echo(ushort value) => value;
    public byte Echo(byte value) => value;
}
```

`UnsignedOperators.cs`:
```csharp
namespace SampleUnsignedLib;

public sealed class UnsignedOperators
{
    public uint Add(uint a, uint b) => a + b;
    public uint Subtract(uint a, uint b) => a - b;
    public uint Multiply(uint a, uint b) => a * b;
    public uint Divide(uint a, uint b) => a / b;
    public uint Remainder(uint a, uint b) => a % b;
    public uint And(uint a, uint b) => a & b;
    public uint Or(uint a, uint b) => a | b;
    public uint Xor(uint a, uint b) => a ^ b;
    public uint Not(uint a) => ~a;
    public uint LeftShift(uint a, int n) => a << n;
    public uint RightShift(uint a, int n) => a >> n;
    public bool Equal(uint a, uint b) => a == b;
    public bool NotEqual(uint a, uint b) => a != b;
    public bool Less(uint a, uint b) => a < b;
    public bool LessOrEqual(uint a, uint b) => a <= b;
    public bool Greater(uint a, uint b) => a > b;
    public bool GreaterOrEqual(uint a, uint b) => a >= b;

    public void Mutate(ref uint a, uint b)
    {
        a++;
        a--;
        ++a;
        --a;
        a += b; a -= b; a *= b; a /= b; a %= b;
        a &= b; a |= b; a ^= b;
    }
}
```

`UnsignedCasts.cs`:
```csharp
namespace SampleUnsignedLib;

public sealed class UnsignedCasts
{
    public uint ToUInt(int x) => (uint)x;
    public ulong ToULong(long x) => (ulong)x;
    public ushort ToUShort(int x) => (ushort)x;
    public byte ToByte(int x) => (byte)x;

    public int ToInt32(uint x) => (int)x;
    public long ToInt64Unsigned(uint x) => (long)x;
    public long ToInt64(ulong x) => (long)x;
    public short ToInt16(ushort x) => (short)x;
    public sbyte ToSByte(byte x) => (sbyte)x;
}
```

`UnsignedArrays.cs`:
```csharp
using System.Collections.Generic;

namespace SampleUnsignedLib;

public sealed class UnsignedArrays
{
    public uint[] Arr = new uint[10];
    public uint[,] Matrix = new uint[3, 4];
    public uint[][] Jagged = new uint[2][];
    public byte[] Bytes = new byte[] { 1u, 2u };

    public void Fill()
    {
        Arr[0] = 5u;
        Matrix[0, 0] = 1u;
        Jagged[0] = new uint[] { 1u, 2u };
    }
}
```

`UnsignedGenerics.cs`:
```csharp
using System.Collections.Generic;

namespace SampleUnsignedLib;

public sealed class UnsignedGenerics
{
    public List<uint> List = new List<uint>();
    public Dictionary<uint, ulong> Dict = new Dictionary<uint, ulong>();
    public IReadOnlyCollection<byte> Bytes = new List<byte>();
    public GenericHolder<uint> Holder = new GenericHolder<uint>();
}

public sealed class GenericHolder<T>
{
    public T? Value;
}
```

`UnsignedLiterals.cs`:
```csharp
namespace SampleUnsignedLib;

public sealed class UnsignedLiterals
{
    public uint A = 5;
    public uint B = 5u;
    public ulong C = 5ul;
    public uint D = 0xFFFFFFFFu;
    public uint Mixed()
    {
        uint a = 5;
        int b = 3;
        var c = a + (uint)b;
        return c;
    }
}
```

`UnsignedConst.cs`:
```csharp
namespace SampleUnsignedLib;

public sealed class UnsignedConst
{
    public const uint MaxVal = uint.MaxValue;
    public const ulong Big = 0xFFFFFFFFFFFFFFFFul;
}
```

`UnsignedEnum.cs`:
```csharp
namespace SampleUnsignedLib;

public enum Flag : uint
{
    None = 0,
    All = 0xFFFFFFFFu,
}
```

`UnsignedRefOut.cs`:
```csharp
namespace SampleUnsignedLib;

public sealed class UnsignedRefOut
{
    public bool TryParse(string s, out uint result)
    {
        result = 0u;
        return uint.TryParse(s, out result);
    }

    public void Increment(ref uint x) => x = x.Add(1);
}
```

> Note: `x.Add(1)` compiles only after the extension file is generated; the fixture source itself is never built before preprocessing, and the intermediate project receives the extension file.

`UnsignedNullable.cs`:
```csharp
namespace SampleUnsignedLib;

public sealed class UnsignedNullable
{
    public uint? Maybe = null;
    public System.Type TypeOf() => typeof(uint);
    public uint Default() => default(uint);
}
```

`UnsignedStaticMembers.cs`:
```csharp
namespace SampleUnsignedLib;

public sealed class UnsignedStaticMembers
{
    public uint Max => uint.MaxValue;
    public uint Min => uint.MinValue;
    public ulong ULongMax => ulong.MaxValue;
    public uint Parse(string s) => uint.Parse(s);
    public bool TryParse(string s, out uint v) => uint.TryParse(s, out v);
}
```

`UnsignedLambda.cs`:
```csharp
using System;

namespace SampleUnsignedLib;

public sealed class UnsignedLambda
{
    public Func<uint, uint> Func = x => x + 1;

    public uint Local(uint a, uint b) => Add(a, b);

    private static uint Add(uint a, uint b) => a + b;
}
```

`Program.cs`:
```csharp
using SampleUnsignedLib;

var types = new UnsignedTypes { UIntField = 1u };
var ops = new UnsignedOperators();
uint sum = ops.Add(types.UIntField, 2u);
var casts = new UnsignedCasts();
int i = casts.ToInt32(sum);
Console.WriteLine(i);
```

### Step 1.5: Unit tests

Copy the complete `UnsignedTypeRewriterUnitTests` class from `prompts/unsigned-type-rewriter-prompt.md` (the "完善的单元测试" section) into `tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs`.

### Step 1.6: Project tests

Copy the complete `UnsignedTypeProjectTests` class from the same prompt section into `tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs`.

### Step 1.7: csproj content include

Add to `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj`:

```xml
<ItemGroup>
  <!-- Include SampleUnsignedSolution as content so it's available at test runtime -->
  <Content Include="..\SampleUnsignedSolution\**\*.*">
    <Link>SampleUnsignedSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

### Step 1.8: InternalsVisibleTo

Add to `src/CSharpToJava.CLI/CSharpToJava.CLI.csproj` inside the existing `AssemblyAttribute` ItemGroup:

```xml
<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
  <_Parameter1>CSharpToJava.ProjectTests</_Parameter1>
</AssemblyAttribute>
```

### Step 1.9: Verify RED

Run:
`dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedType" -v minimal`

Expected: FAIL because `UnsignedTypeRewriter` / `ProjectUnsignedTypePreprocessor` do not exist.

### Step 1.10: Commit

```bash
git add tests/SampleUnsignedSolution tests/CSharpToJava.ProjectTests/UnsignedTypeRewriterUnitTests.cs tests/CSharpToJava.ProjectTests/UnsignedTypeProjectTests.cs tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj src/CSharpToJava.CLI/CSharpToJava.CLI.csproj
git commit -m "test: add unsigned-type rewriter unit/project tests and fixture"
```

---

## Task 2: Core support records + keyword replacement (GREEN)

**Files:**
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeRewriterOptions.cs`
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeRewriterStatistics.cs`
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeRewriterDiagnostic.cs`
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeRewriterResult.cs`
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs`
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeRewriter.cs`
- Create: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedExtensionsFileContent.cs`

### Step 2.1: Options

```csharp
namespace CSharpToJava.Core.UnsignedTypeRewriter;

public sealed record UnsignedTypeRewriterOptions(
    bool Verbose = false,
    bool Strict = false,
    bool EnableOperatorRewrite = true,
    bool EnableCastRewrite = true);
```

### Step 2.2: Statistics

```csharp
namespace CSharpToJava.Core.UnsignedTypeRewriter;

public sealed class UnsignedTypeRewriterStatistics
{
    public int FilesScanned;
    public int FilesTransformed;
    public int FilesSkippedClean;
    public int TypesReplaced;
    public int OperatorsRewritten;
    public int CastsRewritten;
    public int ArraysRewritten;
    public int GenericArgsReplaced;
}
```

### Step 2.3: Diagnostic

```csharp
namespace CSharpToJava.Core.UnsignedTypeRewriter;

public enum UnsignedTypeSeverity { Info, Warning, Error }

public sealed record UnsignedTypeRewriterDiagnostic(
    UnsignedTypeSeverity Severity, string Message);
```

### Step 2.4: Result

```csharp
namespace CSharpToJava.Core.UnsignedTypeRewriter;

public sealed record UnsignedTypeRewriterResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<UnsignedTypeRewriterDiagnostic> Diagnostics,
    UnsignedTypeRewriterStatistics Statistics);
```

### Step 2.5: Syntax rewriter (type replacement + dirty detection)

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

internal sealed class UnsignedTypeSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly UnsignedTypeRewriterOptions _options;
    private readonly UnsignedTypeRewriterStatistics _stats;
    private readonly List<UnsignedTypeRewriterDiagnostic> _diagnostics;

    public bool UsedExtensions { get; private set; }

    private static readonly Dictionary<SyntaxKind, string> KeywordToFullName = new()
    {
        { SyntaxKind.ByteKeyword,   "System.Byte"   },
        { SyntaxKind.UShortKeyword, "System.UInt16" },
        { SyntaxKind.UIntKeyword,   "System.UInt32" },
        { SyntaxKind.ULongKeyword,  "System.UInt64" },
    };

    public UnsignedTypeSyntaxRewriter(
        SemanticModel? semanticModel,
        UnsignedTypeRewriterOptions options,
        UnsignedTypeRewriterStatistics stats,
        List<UnsignedTypeRewriterDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    internal static bool IsDirty(CompilationUnitSyntax root)
        => root.DescendantTokens().Any(t => KeywordToFullName.ContainsKey(t.Kind()));

    internal static bool ContainsUnsignedKeyword(SyntaxNode node)
        => node.DescendantTokens().Any(t => KeywordToFullName.ContainsKey(t.Kind()));

    public override SyntaxNode? VisitPredefinedType(PredefinedTypeSyntax node)
    {
        if (!KeywordToFullName.TryGetValue(node.Keyword.Kind(), out var fullName))
            return base.VisitPredefinedType(node);

        _stats.TypesReplaced++;
        return SyntaxFactory.ParseTypeName(fullName).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        // C# parses `uint.MaxValue` / `uint.Parse(...)` as IdentifierName
        // with a keyword token; replacing it yields System.UInt32.MaxValue.
        if (KeywordToFullName.TryGetValue(node.Identifier.Kind(), out var fullName))
        {
            _stats.TypesReplaced++;
            return SyntaxFactory.ParseTypeName(fullName).WithTriviaFrom(node);
        }
        return base.VisitIdentifierName(node);
    }

    public override SyntaxNode? VisitArrayType(ArrayTypeSyntax node)
    {
        if (ContainsUnsignedKeyword(node.ElementType))
            _stats.ArraysRewritten++;
        return base.VisitArrayType(node);
    }

    public override SyntaxNode? VisitTypeArgumentList(TypeArgumentListSyntax node)
    {
        if (node.Arguments.Any(ContainsUnsignedKeyword))
            _stats.GenericArgsReplaced++;
        return base.VisitTypeArgumentList(node);
    }
}
```

### Step 2.6: Entry point

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

public sealed class UnsignedTypeRewriter
{
    public UnsignedTypeRewriterResult Rewrite(
        string sourceCode,
        SemanticModel? semanticModel = null,
        UnsignedTypeRewriterOptions? options = null)
    {
        options ??= new UnsignedTypeRewriterOptions();
        var stats = new UnsignedTypeRewriterStatistics { FilesScanned = 1 };
        var diagnostics = new List<UnsignedTypeRewriterDiagnostic>();

        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();

        if (!UnsignedTypeSyntaxRewriter.IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new UnsignedTypeRewriterResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new UnsignedTypeSyntaxRewriter(semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (rewriter.UsedExtensions)
        {
            newRoot = InjectUsing(newRoot);
        }

        using var workspace = new AdhocWorkspace();
        newRoot = (CompilationUnitSyntax)Formatter.Format(newRoot, workspace);

        stats.FilesTransformed = 1;
        return new UnsignedTypeRewriterResult(newRoot.ToFullString(), true, diagnostics, stats);
    }

    private static CompilationUnitSyntax InjectUsing(CompilationUnitSyntax root)
    {
        if (root.Usings.Any(u => u.Name?.ToString() == "Cs2jUnsigned"))
            return root;

        var directive = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Cs2jUnsigned"))
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
        return root.AddUsings(directive);
    }
}
```

### Step 2.7: Extension file content (initial)

```csharp
namespace CSharpToJava.Core.UnsignedTypeRewriter;

public static class UnsignedExtensionsFileContent
{
    public const string FileName = "_Cs2jUnsignedExtensions.cs";

    public static string Generate() => """
        namespace Cs2jUnsigned;

        public static class UnsignedExtensions
        {
            // System.Byte
            public static byte Add(this byte a, byte b) => unchecked((byte)(a + b));
            public static byte Subtract(this byte a, byte b) => unchecked((byte)(a - b));
            public static byte Multiply(this byte a, byte b) => unchecked((byte)(a * b));
            public static byte Divide(this byte a, byte b) => (byte)(a / b);
            public static byte Remainder(this byte a, byte b) => (byte)(a % b);
            public static byte And(this byte a, byte b) => (byte)(a & b);
            public static byte Or(this byte a, byte b) => (byte)(a | b);
            public static byte Xor(this byte a, byte b) => (byte)(a ^ b);
            public static byte Not(this byte a) => unchecked((byte)~a);
            public static byte LeftShift(this byte a, int n) => unchecked((byte)(a << n));
            public static byte RightShift(this byte a, int n) => unchecked((byte)(a >> n));
            public static int CompareTo(this byte a, byte b) => a.CompareTo(b);
            public static sbyte ToSByte(this byte a) => unchecked((sbyte)a);
            public static short ToInt16(this byte a) => a;
            public static int ToInt32(this byte a) => a;
            public static long ToInt64(this byte a) => a;

            // System.UInt16
            public static ushort Add(this ushort a, ushort b) => unchecked((ushort)(a + b));
            public static ushort Subtract(this ushort a, ushort b) => unchecked((ushort)(a - b));
            public static ushort Multiply(this ushort a, ushort b) => unchecked((ushort)(a * b));
            public static ushort Divide(this ushort a, ushort b) => (ushort)(a / b);
            public static ushort Remainder(this ushort a, ushort b) => (ushort)(a % b);
            public static ushort And(this ushort a, ushort b) => (ushort)(a & b);
            public static ushort Or(this ushort a, ushort b) => (ushort)(a | b);
            public static ushort Xor(this ushort a, ushort b) => (ushort)(a ^ b);
            public static ushort Not(this ushort a) => unchecked((ushort)~a);
            public static ushort LeftShift(this ushort a, int n) => unchecked((ushort)(a << n));
            public static ushort RightShift(this ushort a, int n) => unchecked((ushort)(a >> n));
            public static int CompareTo(this ushort a, ushort b) => a.CompareTo(b);
            public static byte ToByte(this ushort a) => unchecked((byte)a);
            public static sbyte ToSByte(this ushort a) => unchecked((sbyte)a);
            public static short ToInt16(this ushort a) => unchecked((short)a);
            public static int ToInt32(this ushort a) => a;
            public static long ToInt64(this ushort a) => a;

            // System.UInt32
            public static uint Add(this uint a, uint b) => unchecked(a + b);
            public static uint Subtract(this uint a, uint b) => unchecked(a - b);
            public static uint Multiply(this uint a, uint b) => unchecked(a * b);
            public static uint Divide(this uint a, uint b) => a / b;
            public static uint Remainder(this uint a, uint b) => a % b;
            public static uint And(this uint a, uint b) => a & b;
            public static uint Or(this uint a, uint b) => a | b;
            public static uint Xor(this uint a, uint b) => a ^ b;
            public static uint Not(this uint a) => ~a;
            public static uint LeftShift(this uint a, int n) => a << n;
            public static uint RightShift(this uint a, int n) => a >> n;
            public static int CompareTo(this uint a, uint b) => a.CompareTo(b);
            public static byte ToByte(this uint a) => unchecked((byte)a);
            public static sbyte ToSByte(this uint a) => unchecked((sbyte)a);
            public static short ToInt16(this uint a) => unchecked((short)a);
            public static int ToInt32(this uint a) => unchecked((int)a);
            public static long ToInt64Unsigned(this uint a) => a;

            // System.UInt64
            public static ulong Add(this ulong a, ulong b) => unchecked(a + b);
            public static ulong Subtract(this ulong a, ulong b) => unchecked(a - b);
            public static ulong Multiply(this ulong a, ulong b) => unchecked(a * b);
            public static ulong Divide(this ulong a, ulong b) => a / b;
            public static ulong Remainder(this ulong a, ulong b) => a % b;
            public static ulong And(this ulong a, ulong b) => a & b;
            public static ulong Or(this ulong a, ulong b) => a | b;
            public static ulong Xor(this ulong a, ulong b) => a ^ b;
            public static ulong Not(this ulong a) => ~a;
            public static ulong LeftShift(this ulong a, int n) => a << n;
            public static ulong RightShift(this ulong a, int n) => a >> n;
            public static int CompareTo(this ulong a, ulong b) => a.CompareTo(b);
            public static byte ToByte(this ulong a) => unchecked((byte)a);
            public static sbyte ToSByte(this ulong a) => unchecked((sbyte)a);
            public static short ToInt16(this ulong a) => unchecked((short)a);
            public static int ToInt32(this ulong a) => unchecked((int)a);
            public static long ToInt64(this ulong a) => unchecked((long)a);
        }
        """;
}
```

### Step 2.8: Verify partial GREEN

Run the same filtered test command. Expected: tests covering type replacement (`FieldDeclaration_KeywordReplacedWithFullName`, arrays, generics, nullable, const, enum, static member, typeof, ref/out, lambdas/local functions, clean file) pass; operator/cast tests still fail.

### Step 2.9: Commit

```bash
git add src/CSharpToJava.Core/UnsignedTypeRewriter
git commit -m "feat: add unsigned-type keyword replacement rewriter core"
```

---

## Task 3: Operators, casts, literals (GREEN)

**Files:**
- Modify: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs`

### Step 3.1: Add operator/cast maps and helpers

Add to `UnsignedTypeSyntaxRewriter`:

```csharp
private static readonly Dictionary<SyntaxKind, string> BinaryOpToMethod = new()
{
    { SyntaxKind.AddExpression,         "Add"       },
    { SyntaxKind.SubtractExpression,    "Subtract"  },
    { SyntaxKind.MultiplyExpression,    "Multiply"  },
    { SyntaxKind.DivideExpression,      "Divide"    },
    { SyntaxKind.ModuloExpression,      "Remainder" },
    { SyntaxKind.BitwiseAndExpression,  "And"       },
    { SyntaxKind.BitwiseOrExpression,   "Or"        },
    { SyntaxKind.ExclusiveOrExpression, "Xor"       },
    { SyntaxKind.LeftShiftExpression,   "LeftShift" },
    { SyntaxKind.RightShiftExpression,  "RightShift"},
};

private static readonly Dictionary<SyntaxKind, string> CompoundAssignmentToMethod = new()
{
    { SyntaxKind.AddAssignmentExpression,        "Add"       },
    { SyntaxKind.SubtractAssignmentExpression,   "Subtract"  },
    { SyntaxKind.MultiplyAssignmentExpression,   "Multiply"  },
    { SyntaxKind.DivideAssignmentExpression,     "Divide"    },
    { SyntaxKind.ModuloAssignmentExpression,     "Remainder" },
    { SyntaxKind.AndAssignmentExpression,        "And"       },
    { SyntaxKind.OrAssignmentExpression,         "Or"        },
    { SyntaxKind.ExclusiveOrAssignmentExpression,"Xor"       },
};
```

Add helpers:

```csharp
private static bool IsUnsignedType(ITypeSymbol? type)
    => type?.SpecialType is SpecialType.System_Byte
        or SpecialType.System_UInt16
        or SpecialType.System_UInt32
        or SpecialType.System_UInt64;

private bool IsUnsignedExpression(ExpressionSyntax expression)
    => _semanticModel != null && IsUnsignedType(_semanticModel.GetTypeInfo(expression).Type);

private static ExpressionSyntax Clone(ExpressionSyntax expression)
    => SyntaxFactory.ParseExpression(expression.ToString());

private static InvocationExpressionSyntax Invoke(ExpressionSyntax receiver, string method, ExpressionSyntax? argument = null)
{
    var memberAccess = SyntaxFactory.MemberAccessExpression(
        SyntaxKind.SimpleMemberAccessExpression,
        receiver,
        SyntaxFactory.IdentifierName(method));
    var arguments = argument == null
        ? SyntaxFactory.ArgumentList()
        : SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { SyntaxFactory.Argument(argument) }));
    return SyntaxFactory.InvocationExpression(memberAccess, arguments);
}

private static ExpressionSyntax LiteralOne()
    => SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
```

### Step 3.2: Binary operators

```csharp
public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
{
    var shouldRewrite = _options.EnableOperatorRewrite
        && _semanticModel != null
        && BinaryOpToMethod.ContainsKey(node.Kind())
        && (IsUnsignedExpression(node.Left) || IsUnsignedExpression(node.Right));

    var rewritten = (BinaryExpressionSyntax)base.VisitBinaryExpression(node)!;
    if (!shouldRewrite) return rewritten;

    UsedExtensions = true;
    _stats.OperatorsRewritten++;

    return rewritten.Kind() switch
    {
        SyntaxKind.EqualsExpression => Invoke(rewritten.Left, "Equals", rewritten.Right),
        SyntaxKind.NotEqualsExpression => SyntaxFactory.PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            Invoke(rewritten.Left, "Equals", rewritten.Right)),
        SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
            or SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression =>
            SyntaxFactory.BinaryExpression(
                ComparisonKind(rewritten.Kind()),
                Invoke(rewritten.Left, "CompareTo", rewritten.Right),
                LiteralOne()),
        _ => Invoke(rewritten.Left, BinaryOpToMethod[rewritten.Kind()], rewritten.Right),
    };
}

private static SyntaxKind ComparisonKind(SyntaxKind kind) => kind switch
{
    SyntaxKind.LessThanExpression => SyntaxKind.LessThanExpression,
    SyntaxKind.LessThanOrEqualExpression => SyntaxKind.LessThanOrEqualExpression,
    SyntaxKind.GreaterThanExpression => SyntaxKind.GreaterThanExpression,
    _ => SyntaxKind.GreaterThanOrEqualExpression,
};
```

> Comparison operands use `CompareTo(b) < 0` etc.; the prompt's mapping is satisfied with the literal `0` on the right.

### Step 3.3: Unary, compound assignment, increments

```csharp
public override SyntaxNode? VisitUnaryExpression(UnaryExpressionSyntax node)
{
    var shouldRewrite = _options.EnableOperatorRewrite
        && node.IsKind(SyntaxKind.BitwiseNotExpression)
        && IsUnsignedExpression(node.Operand);
    var rewritten = (UnaryExpressionSyntax)base.VisitUnaryExpression(node)!;
    if (!shouldRewrite) return rewritten;
    UsedExtensions = true;
    _stats.OperatorsRewritten++;
    return Invoke(rewritten.Operand, "Not");
}

public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
{
    var shouldRewrite = _options.EnableOperatorRewrite
        && CompoundAssignmentToMethod.ContainsKey(node.Kind())
        && IsUnsignedExpression(node.Left);
    var rewritten = (AssignmentExpressionSyntax)base.VisitAssignmentExpression(node)!;
    if (!shouldRewrite) return rewritten;
    UsedExtensions = true;
    _stats.OperatorsRewritten++;
    return SyntaxFactory.AssignmentExpression(
        SyntaxKind.SimpleAssignmentExpression,
        rewritten.Left,
        Invoke(Clone(rewritten.Left), CompoundAssignmentToMethod[rewritten.Kind()], rewritten.Right));
}

public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
{
    var method = node.Kind() switch
    {
        SyntaxKind.PostIncrementExpression => "Add",
        SyntaxKind.PostDecrementExpression => "Subtract",
        _ => null,
    };
    var shouldRewrite = method != null && IsUnsignedExpression(node.Operand);
    var rewritten = (PostfixUnaryExpressionSyntax)base.VisitPostfixUnaryExpression(node)!;
    if (!shouldRewrite || method == null) return rewritten;
    UsedExtensions = true;
    _stats.OperatorsRewritten++;
    return SyntaxFactory.AssignmentExpression(
        SyntaxKind.SimpleAssignmentExpression,
        rewritten.Operand,
        Invoke(Clone(rewritten.Operand), method, LiteralOne()));
}

public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
{
    var method = node.Kind() switch
    {
        SyntaxKind.PreIncrementExpression => "Add",
        SyntaxKind.PreDecrementExpression => "Subtract",
        _ => null,
    };
    var shouldRewrite = method != null && IsUnsignedExpression(node.Operand);
    var rewritten = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node)!;
    if (!shouldRewrite || method == null) return rewritten;
    UsedExtensions = true;
    _stats.OperatorsRewritten++;
    return SyntaxFactory.AssignmentExpression(
        SyntaxKind.SimpleAssignmentExpression,
        rewritten.Operand,
        Invoke(Clone(rewritten.Operand), method, LiteralOne()));
}
```

### Step 3.4: Casts

```csharp
public override SyntaxNode? VisitCastExpression(CastExpressionSyntax node)
{
    string? castMethod = null;
    if (_options.EnableCastRewrite && _semanticModel != null)
    {
        var sourceType = _semanticModel.GetTypeInfo(node.Expression).Type;
        if (IsUnsignedType(sourceType))
            castMethod = GetSignedCastMethod(node.Type, sourceType!);
    }

    var rewritten = (CastExpressionSyntax)base.VisitCastExpression(node)!;
    if (castMethod == null) return rewritten;

    UsedExtensions = true;
    _stats.CastsRewritten++;
    return Invoke(rewritten.Expression, castMethod);
}

private static string? GetSignedCastMethod(TypeSyntax targetType, ITypeSymbol sourceType)
{
    if (targetType is not PredefinedTypeSyntax predefined)
        return null;

    return predefined.Keyword.Kind() switch
    {
        SyntaxKind.SByteKeyword => "ToSByte",
        SyntaxKind.ShortKeyword => "ToInt16",
        SyntaxKind.IntKeyword => "ToInt32",
        SyntaxKind.LongKeyword =>
            sourceType.SpecialType == SpecialType.System_UInt32 ? "ToInt64Unsigned" : "ToInt64",
        _ => null,
    };
}
```

> Unsigned→unsigned casts (e.g. `(uint)x`, `(byte)us`) stay C# casts; only their keyword is replaced.

### Step 3.5: Literal `u` suffix

```csharp
public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
{
    var rewritten = (LiteralExpressionSyntax)base.VisitLiteralExpression(node)!;
    if (_semanticModel == null
        || !rewritten.IsKind(SyntaxKind.NumericLiteralExpression)
        || rewritten.Token.Text.Contains('u')
        || rewritten.Token.Text.Contains('U'))
    {
        return rewritten;
    }

    var convertedType = _semanticModel.GetTypeInfo(node).ConvertedType;
    if (convertedType?.SpecialType != SpecialType.System_UInt32)
        return rewritten;

    var token = rewritten.Token;
    return rewritten.WithToken(SyntaxFactory.Token(
        token.Kind(),
        token.Text + "u",
        token.ValueText,
        token.Value));
}
```

### Step 3.6: Verify GREEN

Run the filtered unit tests. Expected: all `UnsignedTypeRewriterUnitTests` pass.

### Step 3.7: Commit

```bash
git add src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs
git commit -m "feat: rewrite unsigned operators, casts, and uint literals"
```

---

## Task 4: Project preprocessor (GREEN project tests)

**Files:**
- Create: `src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs`

### Step 4.1: Preprocessor skeleton

Implement `ProjectUnsignedTypePreprocessor` by copying the structure of `ProjectReadOnlyStructPreprocessor` and simplifying:

- Request/result records exactly as in the prompt.
- `IntermediateDirectoryName = ".cs2j-unsigned-src"`.
- Copy `IgnoredDirectoryNames`, `EnumerateSourceFiles`, `ResolveSourceLayout`, `MapToIntermediate`, `ResolveRestoreTarget`, `RunDotNetRestore`, `GetBasicReferences`, `AddWindowsDesktopReferences`, `CopyAncestorMsBuildDirectoryFiles`, `CopyMsBuildThisFileDirectoryReferences`, `TryLoadProjectGraph`, `GetCommonRoot`, `IsSameOrDescendantPath`, and `MergeStatistics` verbatim from `ProjectReadOnlyStructPreprocessor.cs`, renamed to `UnsignedTypeSourceLayout` where needed.
- Main flow:
  1. Resolve layout, delete/recreate intermediate root.
  2. Copy project files (with `SupportedProjectFiltering` and sln rewrite).
  3. `CopyAncestorMsBuildDirectoryFiles`.
  4. Parse all `.cs` files into `CSharpCompilation` (`OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true`).
  5. For each `.cs` file: `new UnsignedTypeRewriter().Rewrite(sourceCode, compilation.GetSemanticModel(tree), request.RewriterOptions)`; merge stats; copy when unchanged, write when changed.
  6. Track `usedExtensions |= result.Statistics.OperatorsRewritten > 0 || result.Statistics.CastsRewritten > 0`.
  7. After the file loop, if `usedExtensions`, write `UnsignedExtensionsFileContent.Generate()` to `Path.Combine(intermediateRoot, UnsignedExtensionsFileContent.FileName)`. For non-SDK csproj files (no `Sdk=` attribute and no `<Compile Include`), append a `Compile Include` item before `</Project>`.
  8. `dotnet restore` the mapped target; add an `Error` diagnostic when it fails.
  9. Return result with `Success = !diagnostics.Any(d => d.Severity == UnsignedTypeSeverity.Error)`.

### Step 4.2: Verify project tests

Run:
`dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj --filter "FullyQualifiedName~UnsignedType" -v minimal`

Expected: all tests pass. If `dotnet restore`/build fails, fix the generated intermediate and re-run.

### Step 4.3: Commit

```bash
git add src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs
git commit -m "feat: add project-level unsigned-type preprocessor"
```

---

## Task 5: CLI integration

**Files:**
- Modify: `src/CSharpToJava.CLI/Program.cs`

### Step 5.1: Register verb

Change `ParseArguments` generic list and `MapResult` to include `UnsignedTypeOptions` and `UnsignedType(opts)` (prompt §3.2).

### Step 5.2: Add `UnsignedTypeOptions`

```csharp
[Verb("unsigned-type", HelpText = "Rewrite byte/ushort/uint/ulong keywords to System.* full names")]
class UnsignedTypeOptions
{
    [Option('i', "input", SetName = "file", HelpText = "Input .cs file")]
    public string? Input { get; set; }
    [Option('o', "output", SetName = "file", HelpText = "Output .cs file (default: stdout)")]
    public string? Output { get; set; }
    [Option('s', "source", SetName = "dir", HelpText = "Source directory")]
    public string? Source { get; set; }
    [Option('d', "destination", SetName = "dir", HelpText = "Destination directory")]
    public string? Destination { get; set; }
    [Option("no-operator-rewrite", Default = false, HelpText = "Disable operator → extension method rewrite")]
    public bool NoOperatorRewrite { get; set; }
    [Option("no-cast-rewrite", Default = false, HelpText = "Disable cast rewrite")]
    public bool NoCastRewrite { get; set; }
    [Option('v', "verbose", Default = false)]
    public bool Verbose { get; set; }
    [Option("strict", Default = false)]
    public bool Strict { get; set; }
}
```

### Step 5.3: Implement `UnsignedType`

File mode: read input, build a single-tree compilation with `ProjectUnsignedTypePreprocessor.GetBasicReferences()`, call `Rewrite` with the semantic model and options (`EnableOperatorRewrite = !NoOperatorRewrite`, `EnableCastRewrite = !NoCastRewrite`, `Verbose`, `Strict`); write output (and write the extension file next to the output when the result used extensions), or print to stdout.

Directory mode: call `ProjectUnsignedTypePreprocessor.PreprocessAsync` with `Force = true`; print stats and diagnostics; return `Success ? 0 : 1`.

Make `GetBasicReferences()` in `ProjectUnsignedTypePreprocessor` `internal static` so `Program` can reuse it.

### Step 5.4: `ConvertProjectOptions` + `ConvertProject`

- Add `[Option("no-unsigned-type", ...)] public bool NoUnsignedType { get; set; }` and `public bool UnsignedType => !NoUnsignedType;` (prompt §3.4).
- Insert the unsigned preprocess block at the top of the Java preprocessing chain, before the existing `MakeReadOnly` block (prompt §3.5), printing the same summary line as the prompt.
- In `GetPreservedDestinationDirectories`, add:
  ```csharp
  if (opts.UnsignedType)
  {
      preserved.Add(ProjectUnsignedTypePreprocessor.IntermediateDirectoryName);
  }
  ```
- In `GetProjectConversionOptionTokens`, add `$"unsigned-type={opts.UnsignedType}"`.

### Step 5.5: Verify build + CLI smoke

Run:
`dotnet build src/CSharpToJava.CLI/CSharpToJava.CLI.csproj`

Then:
```bash
dotnet run --project src/CSharpToJava.CLI -- unsigned-type -i tests/SampleUnsignedSolution/SampleUnsignedLib/UnsignedOperators.cs -o _out_unsigned.cs
```
Expected: output contains `System.UInt32`, `.Add(`, `using Cs2jUnsigned;`.

Then run `unsigned-type -s tests/SampleUnsignedSolution -d _out_unsigned_dir` and `dotnet build _out_unsigned_dir/.cs2j-unsigned-src/SampleUnsignedSolution.sln`.

### Step 5.6: Commit

```bash
git add src/CSharpToJava.CLI/Program.cs src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs
git commit -m "feat: integrate unsigned-type verb and convert-project option"
```

---

## Task 6: Full verification + code review

**Files:** none (verification only).

### Step 6.1: Full build

Run `dotnet build CSharpToJavaConverter.slnx -c Debug`. Expected: 0 errors.

### Step 6.2: Full tests

Run `dotnet test CSharpToJavaConverter.slnx --no-build -c Debug`. Expected: all tests pass (baseline Java-compat failure must be absent because `mvn compile` was run).

### Step 6.3: Requirement checklist

Verify each acceptance item from the prompt:
- `uint` → `System.UInt32` in every fixture file under `.cs2j-unsigned-src`.
- `a + b` → `a.Add(b)`, comparisons → `CompareTo`, casts → `ToInt32()`/`ToInt64Unsigned()`/`ToInt64()`/`ToInt16()`/`ToSByte()`.
- Arrays/generics/member access rewritten.
- `_Cs2jUnsignedExtensions.cs` exists and `using Cs2jUnsigned;` is injected only in files that used extensions.
- `--no-unsigned-type` disables the step (verify with `convert-project --no-unsigned-type` on the fixture).

### Step 6.4: Code review

Use `requesting-code-review` skill: dispatch a fresh code-reviewer subagent with base `23f6f210`, head = current HEAD, plan path, and prompt path; fix Critical/Important findings.

### Step 6.5: Final commit

Commit any review fixes, then report completion with fresh build/test evidence.
