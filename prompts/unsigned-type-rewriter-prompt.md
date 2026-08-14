# 前置转换：无符号类型（byte/ushort/uint/ulong）类化重写

## 任务背景

cs2j 当前能正确处理 `sbyte`/`short`/`int`/`long`，但对于 `byte`/`ushort`/`uint`/`ulong` 处理不正确。`config/TypeMappings.json` 顶部（第 24–46 行）有一套**有损映射**：`System.Byte` → `int`、`System.UInt16` → `short`、`System.UInt32` → `int`、`System.UInt64` → `long`，导致 `uint.MaxValue`（4294967295）变成 `-1`。同一文件后面（第 5160–5180 行）又有一套 compat 类映射，两套冲突，实际生效的是顶部那套有损映射。

## 目标

新增一个**前置转换**（纯 C# → C# 源码重写，**绝对不涉及任何 Java 侧修改**），参照 `GotoEliminator` 和 `ReadOnlyStructMaker` 的设计模式。

前置转换的职责：
1. 将 C# 无符号类型关键字 `byte`/`ushort`/`uint`/`ulong` 替换为对应的 `System.*` 全名形式
2. 将无符号类型上的运算符表达式重写为扩展方法调用（引入 C# 扩展方法辅助文件）
3. 处理 cast、数组、泛型、字面量、const、enum、ref/out 等所有相关语法上下文

**前置转换的输出必须是合法的、可编译通过的 C# 代码**，与 `GotoEliminator`/`ReadOnlyStructMaker` 一致。

**核心类型替换规则**：

| C# 关键字 | 替换为（C# 全名） |
|-----------|-------------------|
| `byte`    | `System.Byte`     |
| `ushort`  | `System.UInt16`   |
| `uint`    | `System.UInt32`   |
| `ulong`   | `System.UInt64`   |

> `sbyte`/`short`/`int`/`long` 不受影响。

---

## 硬性约束（必须全部满足）

### 1. 运算符重载 → 扩展方法调用

对 `byte`/`ushort`/`uint`/`ulong` 类型的二元/一元运算，在 C# 层面重写为扩展方法调用。前置转换生成一个 C# 扩展方法辅助文件（`_Cs2jUnsignedExtensions.cs`），复制到目标项目目录中，并在处理的每个文件顶部注入 `using Cs2jUnsigned;`。

**运算符 → 扩展方法映射**：

| C# 运算符 | 重写为（C# 扩展方法调用） |
|-----------|--------------------------|
| `a + b`   | `a.Add(b)`               |
| `a - b`   | `a.Subtract(b)`          |
| `a * b`   | `a.Multiply(b)`          |
| `a / b`   | `a.Divide(b)`            |
| `a % b`   | `a.Remainder(b)`         |
| `a & b`   | `a.And(b)`               |
| `a \| b`  | `a.Or(b)`                |
| `a ^ b`   | `a.Xor(b)`               |
| `~a`      | `a.Not()`                |
| `a << n`  | `a.LeftShift(n)`         |
| `a >> n`  | `a.RightShift(n)`        |
| `a == b`  | `a.Equals(b)`            |
| `a != b`  | `!a.Equals(b)`           |
| `a < b`   | `a.CompareTo(b) < 0`     |
| `a <= b`  | `a.CompareTo(b) <= 0`    |
| `a > b`   | `a.CompareTo(b) > 0`     |
| `a >= b`  | `a.CompareTo(b) >= 0`    |
| `a++`     | `a = a.Add(1)`           |
| `a--`     | `a = a.Subtract(1)`      |
| `++a`     | `a = a.Add(1)`           |
| `--a`     | `a = a.Subtract(1)`      |
| `a += b`  | `a = a.Add(b)`           |
| `a -= b`  | `a = a.Subtract(b)`      |
| `a *= b`  | `a = a.Multiply(b)`      |
| `a /= b`  | `a = a.Divide(b)`        |
| `a %= b`  | `a = a.Remainder(b)`     |
| `a &= b`  | `a = a.And(b)`           |
| `a \|= b` | `a = a.Or(b)`            |
| `a ^= b`  | `a = a.Xor(b)`           |

**判定规则**：只有当运算符的**操作数类型**（通过 SemanticModel 解析）是 `byte`/`ushort`/`uint`/`ulong` 时才重写。非无符号类型的运算符不受影响。字面量操作数（如 `a + 1`）保持原样（`a.Add(1)`），扩展方法接受对应基本类型参数。

**扩展方法辅助文件**（`_Cs2jUnsignedExtensions.cs`）示例结构：

```csharp
namespace Cs2jUnsigned;

public static class UnsignedExtensions
{
    // System.UInt32 (uint) 扩展方法
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

    // System.UInt64 (ulong) 扩展方法
    public static ulong Add(this ulong a, ulong b) => unchecked(a + b);
    // ... 同上模式

    // System.UInt16 (ushort) 扩展方法
    public static ushort Add(this ushort a, ushort b) => unchecked((ushort)(a + b));
    // ... 同上模式

    // System.Byte (byte) 扩展方法
    public static byte Add(this byte a, byte b) => unchecked((byte)(a + b));
    // ... 同上模式
}
```

> **设计要点**：扩展方法内部使用 C# 原生运算符（`unchecked` 确保无符号溢出语义），扩展方法签名使用 C# 基本类型（`uint`/`ulong`/`ushort`/`byte`，它们是 `System.UInt32` 等的别名）。前置转换将源码中的 `uint` 替换为 `System.UInt32` 后，扩展方法仍然适用（类型等价）。辅助文件本身不经过类型替换。

### 2. cast 强制转换

| C# cast | 重写为（C# 表达式） |
|---------|-------------------|
| `(uint)x` | `(System.UInt32)x` 保持 cast（C# 合法） |
| `(ulong)x` | `(System.UInt64)x` |
| `(ushort)x` | `(System.UInt16)x` |
| `(byte)x` | `(System.Byte)x` |
| `(int)u`（u 为 uint） | `u.ToInt32()` 扩展方法 |
| `(long)u`（u 为 uint，无符号扩展） | `u.ToInt64Unsigned()` 扩展方法 |
| `(long)ul`（ul 为 ulong） | `ul.ToInt64()` 扩展方法 |
| `(short)us` | `us.ToInt16()` 扩展方法 |
| `(byte)b`（b 为 byte→Byte） | `us.ToByte()` 扩展方法 |

> **设计要点**：无符号类型之间的 cast（如 `(uint)x`）保持 C# cast 语法，仅替换类型关键字。从无符号类型到有符号类型的 cast（如 `(int)u`）重写为扩展方法调用，因为 Java 侧需要无符号到有符号的特殊转换语义。扩展方法辅助文件中需提供 `ToInt32()`/`ToInt64Unsigned()`/`ToInt64()` 等方法。

`default(uint)` → 保持 `default(System.UInt32)`（C# 合法语法）。

### 3. 一维及多维数组

- **一维数组**：`uint[] arr` → `System.UInt32[] arr`（仅类型关键字替换）。`new uint[10]` → `new System.UInt32[10]`。
- **多维数组**：`uint[,] matrix` → `System.UInt32[,] matrix`。`new uint[3, 4]` → `new System.UInt32[3, 4]`。
- **锯齿数组**：`uint[][] jagged` → `System.UInt32[][] jagged`。
- **数组初始化器**：`new uint[] { 1u, 2u }` → `new System.UInt32[] { 1u, 2u }`（类型替换，字面量保持）。
- **byte[] 特殊处理**：`byte[]` 的元素类型替换为 `System.Byte[]`（与其他无符号类型一致，不做特殊保留）。是否保留 `byte[]` 为 Java `byte[]` 由后续主转换器决定，前置转换统一替换。

### 4. 泛型代码

- `List<uint>` → `List<System.UInt32>`（类型实参替换）。
- `Dictionary<uint, ulong>` → `Dictionary<System.UInt32, System.UInt64>`。
- `IReadOnlyCollection<byte>` → `IReadOnlyCollection<System.Byte>`。
- 泛型方法 `T Method<T>(T x) where T : struct` 中的约束不受影响。
- 泛型类型参数实例化：`new GenericHolder<uint>()` → `new GenericHolder<System.UInt32>()`。
- `typeof(uint)` → `typeof(System.UInt32)`。

### 5. 其他必须覆盖的场景（补充完善）

#### 5.1 字面量
- `5u` / `5U` → 保持不变（C# 合法字面量，`System.UInt32` 接受 `5u`）。
- `5ul` / `5UL` / `5lu` / `5LU` → 保持不变。
- `5`（在 uint 上下文中，如 `uint x = 5;`）→ 类型替换后 `System.UInt32 x = 5;`，字面量 `5` 需添加 `u` 后缀变为 `5u` 以确保 C# 编译通过（`System.UInt32 x = 5;` 实际合法，因为 `5` 可隐式转换为 `uint`，但显式加 `u` 更清晰）。

#### 5.2 const 字段
`const uint X = 5;` → `const System.UInt32 X = 5;`（C# 合法，`System.UInt32` 是 `uint` 的全名，`const` 支持基本类型别名）。类型关键字替换即可，无需特殊处理。

#### 5.3 enum 底层类型
`enum E : uint { A, B }` → `enum E : System.UInt32 { A, B }`（C# 合法，enum 底层类型支持全名形式）。

#### 5.4 成员访问
`uint.MaxValue` → `System.UInt32.MaxValue`（`uint` 关键字作为成员访问的接收者时替换为全名）。`uint.MinValue` → `System.UInt32.MinValue`。`ulong.MaxValue` → `System.UInt64.MaxValue`。

#### 5.5 静态方法调用
`uint.Parse("123")` → `System.UInt32.Parse("123")`。`uint.TryParse(s, out v)` → `System.UInt32.TryParse(s, out v)`。`uint.ToString()` 实例方法 → 类型替换不影响实例方法调用。

#### 5.6 ref/out 参数
`void M(ref uint x)` → `void M(ref System.UInt32 x)`（C# 合法）。`void M(out uint x)` → `void M(out System.UInt32 x)`。

#### 5.7 nullable
`uint? x` → `System.UInt32? x`（C# 合法）。

#### 5.8 隐式/显式接口实现
接口方法 `uint Compute()` → `System.UInt32 Compute()`，实现类同步替换。

#### 5.9 switch 语句
`switch (uintVal)` 不受影响（switch 表达式类型替换已在变量声明处完成）。`case 5u:` 字面量保持不变。

#### 5.10 表达式树 / LINQ
LINQ 重写器内部用 `SpecialType` 判断，不受关键字替换影响（`System.UInt32` 的 `SpecialType` 仍为 `System_UInt32`）。

#### 5.11 委托/lambda
`Func<uint, uint> f = x => x + 1;` → `Func<System.UInt32, System.UInt32> f = x => x.Add(1);`（参数类型替换 + 运算符重写）。

#### 5.12 局部函数
`uint Add(uint a, uint b) => a + b;` → `System.UInt32 Add(System.UInt32 a, System.UInt32 b) => a.Add(b);`

#### 5.13 using 别名
`using U = System.UInt32;` 不受影响。`using U = uint;` → `using U = System.UInt32;`（C# 不支持 `using U = uint;` 语法，所以这种情况不存在）。

---

## 实现步骤

### Step 1：Core——新建 `UnsignedTypeRewriter`

在 `src/CSharpToJava.Core/` 下新建目录 `UnsignedTypeRewriter/`，包含以下文件（**参照 `GotoEliminator/` 和 `ReadOnlyStructMaker/` 的代码风格**）：

#### 1.1 `UnsignedTypeRewriterOptions.cs`
```csharp
namespace CSharpToJava.Core.UnsignedTypeRewriter;

public sealed record UnsignedTypeRewriterOptions(
    bool Verbose = false,
    bool Strict = false,
    bool EnableOperatorRewrite = true,
    bool EnableCastRewrite = true);
```

#### 1.2 `UnsignedTypeRewriterStatistics.cs`
```csharp
public sealed class UnsignedTypeRewriterStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }
    public int TypesReplaced { get; set; }
    public int OperatorsRewritten { get; set; }
    public int CastsRewritten { get; set; }
    public int ArraysRewritten { get; set; }
    public int GenericArgsReplaced { get; set; }
}
```

#### 1.3 `UnsignedTypeRewriterDiagnostic.cs`
```csharp
public enum UnsignedTypeSeverity { Info, Warning, Error }
public sealed record UnsignedTypeRewriterDiagnostic(
    UnsignedTypeSeverity Severity, string Message);
```

#### 1.4 `UnsignedTypeRewriterResult.cs`
```csharp
public sealed record UnsignedTypeRewriterResult(
    string OutputCode, bool Changed,
    IReadOnlyList<UnsignedTypeRewriterDiagnostic> Diagnostics,
    UnsignedTypeRewriterStatistics Statistics);
```

#### 1.5 `UnsignedTypeRewriter.cs`（核心入口）
```csharp
public sealed class UnsignedTypeRewriter
{
    public UnsignedTypeRewriterResult Rewrite(
        string sourceCode,
        SemanticModel? semanticModel = null,
        UnsignedTypeRewriterOptions? options = null)
    {
        // 1. Parse source
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        // 2. dirty 检测：是否含 byte/ushort/uint/ulong 关键字
        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new(sourceCode, Changed: false, ...);
        }
        // 3. 运行 UnsignedTypeSyntaxRewriter（类型替换 + 运算符重写）
        var rewriter = new UnsignedTypeSyntaxRewriter(semanticModel, options, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        // 4. 注入 using Cs2jUnsigned;（仅当发生了运算符重写时）
        if (rewriter.UsedExtensions) newRoot = InjectUsing(newRoot);
        // 5. Formatter.Format
        using var workspace = new AdhocWorkspace();
        newRoot = (CompilationUnitSyntax)Formatter.Format(newRoot, workspace);
        // 6. 返回结果
        return new(newRoot.ToFullString(), Changed: true, diagnostics, stats);
    }
}
```

#### 1.6 `UnsignedTypeSyntaxRewriter.cs`（继承 `CSharpSyntaxRewriter`）

核心重写器，负责所有语法树级别的转换。**需要 `SemanticModel`** 来判断运算符操作数的实际类型（区分 `uint + int` 和 `int + int`）。

**类型关键字映射表**：
```csharp
private static readonly Dictionary<SyntaxKind, string> KeywordToFullName = new()
{
    { SyntaxKind.ByteKeyword,   "System.Byte"   },
    { SyntaxKind.UShortKeyword, "System.UInt16" },
    { SyntaxKind.UIntKeyword,   "System.UInt32" },
    { SyntaxKind.ULongKeyword,  "System.UInt64" },
};
```

**运算符 → 方法名映射表**：
```csharp
private static readonly Dictionary<SyntaxKind, string> BinaryOpToMethod = new()
{
    { SyntaxKind.AddExpression,        "Add"       },
    { SyntaxKind.SubtractExpression,   "Subtract"  },
    { SyntaxKind.MultiplyExpression,   "Multiply"  },
    { SyntaxKind.DivideExpression,     "Divide"    },
    { SyntaxKind.ModuloExpression,     "Remainder" },
    { SyntaxKind.BitwiseAndExpression, "And"       },
    { SyntaxKind.BitwiseOrExpression,  "Or"        },
    { SyntaxKind.ExclusiveOrExpression,"Xor"       },
    { SyntaxKind.LeftShiftExpression,  "LeftShift" },
    { SyntaxKind.RightShiftExpression, "RightShift"},
};
```

**判定操作数是否为无符号类型**：
```csharp
private static bool IsUnsignedType(ITypeSymbol? type)
    => type?.SpecialType is SpecialType.System_Byte
        or SpecialType.System_UInt16
        or SpecialType.System_UInt32
        or SpecialType.System_UInt64;
```

**重写的 Visit 方法清单**：

| Visit 方法 | 职责 |
|-----------|------|
| `VisitPredefinedType` | 类型关键字替换：`byte`→`System.Byte` 等 |
| `VisitBinaryExpression` | 二元运算 → 扩展方法调用（需语义模型判定操作数类型） |
| `VisitUnaryExpression` | 一元运算（`~a`） |
| `VisitAssignmentExpression` | 复合赋值（`a += b` → `a = a.Add(b)`） |
| `VisitCastExpression` | cast 处理（无符号间 cast 仅替换类型；无符号→有符号 cast 重写为扩展方法） |
| `VisitPostfixUnaryExpression` | `a++`/`a--` |
| `VisitPrefixUnaryExpression` | `++a`/`--a`/`~a` |
| `VisitArrayType` | 数组元素类型替换（多维/锯齿/一维） |
| `VisitArrayCreationExpression` | `new uint[10]` → `new System.UInt32[10]` |
| `VisitGenericName` | 泛型类型实参替换（`List<uint>` → `List<System.UInt32>`） |
| `VisitTypeArgumentList` | 泛型类型实参列表 |
| `VisitMemberAccessExpression` | `uint.MaxValue` → `System.UInt32.MaxValue` |
| `VisitInvocationExpression` | `uint.Parse(...)` → `System.UInt32.Parse(...)` |
| `VisitFieldDeclaration` | 字段类型替换 |
| `VisitMethodDeclaration` | 返回类型 + 参数类型替换 |
| `VisitPropertyDeclaration` | 属性类型替换 |
| `VisitParameter` | 参数类型替换 |
| `VisitLocalDeclarationStatement` | 局部变量类型替换 |
| `VisitVariableDeclaration` | 变量声明类型替换 |
| `VisitNullableType` | `uint?` → `System.UInt32?` |
| `VisitTypeOfExpression` | `typeof(uint)` → `typeof(System.UInt32)` |
| `VisitEnumDeclaration` | enum 底层类型替换 |
| `VisitObjectCreationExpression` | `new GenericHolder<uint>()` 泛型实参 |
| `VisitLocalFunctionStatement` | 局部函数类型替换 |
| `VisitParenthesizedLambdaExpression` | lambda 参数/返回类型替换 |
| `VisitSimpleLambdaExpression` | lambda 参数类型替换 |
| `VisitParenthesizedExpression` | 透传（递归子节点） |

#### 1.7 `UnsignedExtensionsFileContent.cs`

提供生成的 C# 扩展方法辅助文件的完整内容：

```csharp
namespace CSharpToJava.Core.UnsignedTypeRewriter;

public static class UnsignedExtensionsFileContent
{
    public const string FileName = "_Cs2jUnsignedExtensions.cs";

    // 返回完整的 C# 扩展方法文件内容
    public static string Generate() { ... }
}
```

辅助文件包含 `Cs2jUnsigned` 命名空间下的 `UnsignedExtensions` 静态类，为 `System.UInt32`/`System.UInt64`/`System.UInt16`/`System.Byte` 提供全部运算符扩展方法及 `CompareTo`/`ToInt32`/`ToInt64Unsigned` 等转换方法。

### Step 2：CLI——新建 `ProjectUnsignedTypePreprocessor`

在 `src/CSharpToJava.CLI/` 下新建 `ProjectUnsignedTypePreprocessor.cs`，**完全参照 `ProjectGotoPreprocessor.cs` 和 `ProjectReadOnlyStructPreprocessor.cs` 的结构**：

```csharp
internal sealed class ProjectUnsignedTypePreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public UnsignedTypeRewriterOptions RewriterOptions { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
}

internal sealed class ProjectUnsignedTypePreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required UnsignedTypeRewriterStatistics Statistics { get; init; }
    public required IReadOnlyList<UnsignedTypeRewriterDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectUnsignedTypePreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-unsigned-src";

    public static async Task<ProjectUnsignedTypePreprocessResult> PreprocessAsync(
        ProjectUnsignedTypePreprocessRequest request)
    {
        // 参照 ProjectReadOnlyStructPreprocessor：
        // 1. ResolveSourceLayout（与 ProjectGotoPreprocessor 共用逻辑）
        // 2. 创建 intermediateRoot
        // 3. 复制项目文件（.csproj/.sln），过滤不支持的项目
        // 4. CopyAncestorMsBuildDirectoryFiles
        // 5. 解析所有 .cs 为 SyntaxTree，创建 CSharpCompilation（allowUnsafe: true）
        // 6. 对每个 .cs 文件运行 UnsignedTypeRewriter.Rewrite（传入 SemanticModel）
        // 7. 若任一文件发生了运算符重写，将 _Cs2jUnsignedExtensions.cs 写入
        //    intermediateRoot 根目录（或与 .sln 同级），并确保它被包含在编译中
        // 8. dotnet restore
        // 9. 返回结果
    }
}
```

**关键**：
- 必须像 `ProjectReadOnlyStructPreprocessor` 那样先构建 `CSharpCompilation`，再为每个语法树获取 `SemanticModel` 传给 Rewriter（运算符/cast 的类型判定需要语义模型）。
- 扩展方法辅助文件 `_Cs2jUnsignedExtensions.cs` 只需在至少一个文件发生运算符重写时写入一次。如果使用 SDK 风格的 .csproj（默认 glob 包含所有 .cs），文件放在项目目录下会自动被包含。如果项目不使用默认 glob，需考虑将其添加到 .csproj 的 Compile Include 中。

### Step 3：CLI——集成到 `Program.cs`

#### 3.1 新增 verb `unsigned-type`（单文件/目录模式）
参照 `eliminate-goto` 和 `make-readonly` verb（`Program.cs:2866-2882` 和 `Program.cs:2884-2976`）：
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
    [Option("no-operator-rewrite", HelpText = "Disable operator → extension method rewrite")]
    public bool NoOperatorRewrite { get; set; }
    [Option("no-cast-rewrite", HelpText = "Disable cast rewrite")]
    public bool NoCastRewrite { get; set; }
    [Option('v', "verbose")]
    public bool Verbose { get; set; }
    [Option("strict")]
    public bool Strict { get; set; }
}
```

#### 3.2 在 `Parser.Default.ParseArguments` 中注册新 verb
```csharp
return await Parser.Default.ParseArguments<
    ConvertOptions, ConvertProjectOptions, AnalyzeOptions,
    EliminateGotoOptions, MakeReadOnlyOptions, UnsignedTypeOptions>(args)
    .MapResult(
        (ConvertOptions opts) => ConvertFile(opts),
        (ConvertProjectOptions opts) => ConvertProject(opts),
        (AnalyzeOptions opts) => AnalyzeProject(opts),
        (EliminateGotoOptions opts) => EliminateGoto(opts),
        (MakeReadOnlyOptions opts) => MakeReadonly(opts),
        (UnsignedTypeOptions opts) => UnsignedType(opts),
        errs => Task.FromResult(1));
```

#### 3.3 实现 `UnsignedType` 方法
参照 `MakeReadonly`（`Program.cs:2776-2863`）和 `EliminateGoto`（`Program.cs:2686-2767`）：
- 文件模式：读取单文件 → 构建 `CSharpCompilation` 获取 `SemanticModel` → 调用 `UnsignedTypeRewriter.Rewrite` → 输出结果
- 目录模式：调用 `ProjectUnsignedTypePreprocessor.PreprocessAsync`

#### 3.4 在 `ConvertProjectOptions` 中新增选项
参照 `NoEliminateGoto`/`NoMakeReadOnly`（`Program.cs:3035-3039`）：
```csharp
[Option("no-unsigned-type", Default = false,
    HelpText = "Disable the default unsigned-type preprocessing step")]
public bool NoUnsignedType { get; set; }

public bool UnsignedType => !NoUnsignedType;
```

#### 3.5 在 `ConvertProject` 方法中插入预处理步骤

**执行顺序**：`UnsignedType` → `MakeReadOnly` → `EliminateGoto` → 主转换。

> 理由：UnsignedType 是纯类型替换 + 表达式重写，应在最前面执行，让后续的 ReadOnlyStruct/GotoEliminator 看到的是已替换类型的代码。三者都是 C#→C# 前置转换。

参照 `MakeReadOnly` 集成点（`Program.cs:184-221`）和 `EliminateGoto` 集成点（`Program.cs:223-245`）：
```csharp
if (opts.UnsignedType)
{
    var unsignedResult = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
        new ProjectUnsignedTypePreprocessRequest
        {
            SourcePath = opts.Source,
            DestinationRoot = opts.Destination,
            Verbose = opts.Verbose,
        });
    Console.WriteLine(
        $"unsigned-type: {unsignedResult.Statistics.FilesTransformed} transformed, " +
        $"{unsignedResult.Statistics.FilesSkippedClean} clean, " +
        $"{unsignedResult.Statistics.TypesReplaced} types replaced, " +
        $"{unsignedResult.Statistics.OperatorsRewritten} operators rewritten");
    foreach (var diagnostic in unsignedResult.Diagnostics)
    {
        // 输出 diagnostics（参照 MakeReadOnly 的处理方式）
    }
    opts.Source = unsignedResult.PreprocessedSourcePath;
    opts.FingerprintSourcePath ??= unsignedResult.OriginalSourcePath;
    opts.FingerprintInputRoot ??= unsignedResult.SourceRoot;
}

if (opts.MakeReadOnly) { ... }  // 既有
if (opts.EliminateGoto) { ... }  // 既有
```

#### 3.6 在 `GetPreservedIntermediateDirectories` 中注册
参照 `Program.cs:1636-1646`：
```csharp
if (opts.UnsignedType)
{
    preserved.Add(ProjectUnsignedTypePreprocessor.IntermediateDirectoryName);
}
```

#### 3.7 在 `GetConversionFingerprintTokens` 中注册
参照 `Program.cs:2361-2368`：
```csharp
$"unsigned-type={opts.UnsignedType}",
```

---

## 完善的单元测试

### 测试 fixture 项目

在 `tests/` 下新建 `SampleUnsignedSolution/`，包含一个解决方案和两个项目（Lib + App），覆盖所有场景：

```
tests/SampleUnsignedSolution/
├── SampleUnsignedSolution.sln
├── SampleUnsignedLib/
│   ├── SampleUnsignedLib.csproj
│   ├── UnsignedTypes.cs          # 基本类型字段/属性/参数/返回值
│   ├── UnsignedOperators.cs      # 全部运算符表达式
│   ├── UnsignedCasts.cs          # cast 强制转换
│   ├── UnsignedArrays.cs         # 一维/多维/锯齿数组
│   ├── UnsignedGenerics.cs       # 泛型容器
│   ├── UnsignedLiterals.cs       # 字面量
│   ├── UnsignedConst.cs          # const 字段
│   ├── UnsignedEnum.cs           # enum 底层类型
│   ├── UnsignedRefOut.cs         # ref/out 参数
│   ├── UnsignedNullable.cs       # nullable
│   ├── UnsignedStaticMembers.cs  # uint.MaxValue / uint.Parse
│   └── UnsignedLambda.cs         # 委托/lambda/局部函数
└── SampleUnsignedApp/
    ├── SampleUnsignedApp.csproj  # 引用 Lib
    └── Program.cs                # 调用 Lib，测试跨项目
```

**fixture C# 代码必须覆盖**：
1. **基本类型**：`byte`/`ushort`/`uint`/`ulong` 作为字段、属性、方法参数、返回值、局部变量
2. **运算符**：`+`、`-`、`*`、`/`、`%`、`&`、`|`、`^`、`~`、`<<`、`>>`、`==`、`!=`、`<`、`<=`、`>`、`>=`、`++`、`--`、`+=`、`-=`、`*=`、`/=`、`%=`
3. **cast**：`(uint)intVal`、`(int)uintVal`、`(ulong)longVal`、`(long)ulongVal`、`(byte)intVal`
4. **一维数组**：`uint[] arr = new uint[10]; arr[0] = 5u;`
5. **多维数组**：`uint[,] matrix = new uint[3, 4]; matrix[0, 0] = 1u;`
6. **锯齿数组**：`uint[][] jagged = new uint[2][];`
7. **泛型**：`List<uint>`、`Dictionary<uint, ulong>`、`IReadOnlyCollection<byte>`
8. **字面量**：`5u`、`5ul`、`0xFFFFFFFFu`
9. **const**：`const uint MaxVal = uint.MaxValue;`
10. **enum**：`enum Flag : uint { None, All }`
11. **ref/out**：`void Parse(string s, out uint result)`、`void Increment(ref uint x)`
12. **nullable**：`uint? maybe = null;`
13. **静态成员**：`uint.MaxValue`、`uint.Parse("123")`、`uint.TryParse(s, out v)`
14. **default**：`default(uint)`
15. **typeof**：`typeof(uint)`
16. **混合运算**：`uint a = 5; int b = 3; var c = a + (uint)b;`
17. **lambda**：`Func<uint, uint> f = x => x + 1;`
18. **局部函数**：`uint Add(uint a, uint b) => a + b;`

### 测试类

在 `tests/CSharpToJava.ProjectTests/` 下新建两个测试文件：

#### `UnsignedTypeRewriterUnitTests.cs`

直接测试 Core Rewriter（单文件，需构造 SemanticModel）。参照 `Program.cs:2776-2800` 中 `MakeReadonly` 单文件模式构建 SemanticModel 的方式：

```csharp
public class UnsignedTypeRewriterUnitTests
{
    private static (SyntaxTree tree, SemanticModel model) ParseWithSemantics(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create("unsigned-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (tree, compilation.GetSemanticModel(tree));
    }

    [Fact]
    public void CleanFile_NoUnsignedKeywords_ReturnedUnchanged()
    {
        var src = "public class C { public int Value; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void FieldDeclaration_KeywordReplacedWithFullName()
    {
        var src = "public class C { public uint Value; public byte B; public ushort S; public ulong L; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.True(result.Changed);
        Assert.Contains("System.UInt32 Value", result.OutputCode);
        Assert.Contains("System.Byte B", result.OutputCode);
        Assert.Contains("System.UInt16 S", result.OutputCode);
        Assert.Contains("System.UInt64 L", result.OutputCode);
    }

    [Fact]
    public void BinaryOperator_ConvertedToExtensionMethodCall()
    {
        var src = "public class C { public uint Add(uint a, uint b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a.Add(b)", result.OutputCode);
        Assert.DoesNotContain("a + b", result.OutputCode);
        Assert.Contains("using Cs2jUnsigned;", result.OutputCode);
    }

    [Fact]
    public void ComparisonOperator_ConvertedToCompareTo()
    {
        var src = "public class C { public bool Less(uint a, uint b) { return a < b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a.CompareTo(b) < 0", result.OutputCode);
    }

    [Fact]
    public void EqualityOperator_ConvertedToEquals()
    {
        var src = "public class C { public bool Eq(uint a, uint b) { return a == b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a.Equals(b)", result.OutputCode);
    }

    [Fact]
    public void CompoundAssignment_ConvertedToMethodCall()
    {
        var src = "public class C { public void Inc(ref uint a) { a += 5; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a = a.Add(5)", result.OutputCode);
    }

    [Fact]
    public void PostIncrement_ConvertedToAddOne()
    {
        var src = "public class C { public void Inc(uint a) { a++; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a = a.Add(1)", result.OutputCode);
    }

    [Fact]
    public void Cast_UnsignedToUnsigned_TypeReplacedOnly()
    {
        var src = "public class C { public uint Cast(int x) { return (uint)x; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("(System.UInt32)x", result.OutputCode);
    }

    [Fact]
    public void Cast_UnsignedToSigned_ConvertedToExtensionMethod()
    {
        var src = "public class C { public int ToInt(uint x) { return (int)x; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("x.ToInt32()", result.OutputCode);
    }

    [Fact]
    public void OneDimensionalArray_ElementTypeReplaced()
    {
        var src = "public class C { public uint[] arr = new uint[10]; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32[] arr", result.OutputCode);
        Assert.Contains("new System.UInt32[10]", result.OutputCode);
    }

    [Fact]
    public void MultiDimensionalArray_ElementTypeReplaced()
    {
        var src = "public class C { public uint[,] matrix = new uint[3, 4]; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32[,] matrix", result.OutputCode);
        Assert.Contains("new System.UInt32[3, 4]", result.OutputCode);
    }

    [Fact]
    public void JaggedArray_ElementTypeReplaced()
    {
        var src = "public class C { public uint[][] jagged = new uint[2][]; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32[][] jagged", result.OutputCode);
    }

    [Fact]
    public void Generic_TypeArgumentReplaced()
    {
        var src = "using System.Collections.Generic; public class C { public List<uint> list = new List<uint>(); }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("List<System.UInt32>", result.OutputCode);
    }

    [Fact]
    public void GenericDictionary_BothTypeArgsReplaced()
    {
        var src = "using System.Collections.Generic; public class C { public Dictionary<uint, ulong> dict; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Dictionary<System.UInt32, System.UInt64>", result.OutputCode);
    }

    [Fact]
    public void ConstField_TypeReplaced()
    {
        var src = "public class C { const uint MaxVal = 4294967295u; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("const System.UInt32 MaxVal", result.OutputCode);
    }

    [Fact]
    public void EnumUnderlyingType_Replaced()
    {
        var src = "public enum Flag : uint { None = 0, All = 0xFFFFFFFFu }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("enum Flag : System.UInt32", result.OutputCode);
    }

    [Fact]
    public void StaticMemberAccess_Replaced()
    {
        var src = "public class C { public ulong Max() { return uint.MaxValue; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32.MaxValue", result.OutputCode);
    }

    [Fact]
    public void StaticMethodCall_Replaced()
    {
        var src = "public class C { public uint Parse(string s) { return uint.Parse(s); } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32.Parse(s)", result.OutputCode);
    }

    [Fact]
    public void NullableType_Replaced()
    {
        var src = "public class C { public uint? maybe; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32? maybe", result.OutputCode);
    }

    [Fact]
    public void RefOutParameter_TypeReplaced()
    {
        var src = "public class C { public void M(ref uint x, out uint y) { y = x; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("ref System.UInt32 x", result.OutputCode);
        Assert.Contains("out System.UInt32 y", result.OutputCode);
    }

    [Fact]
    public void TypeOf_Replaced()
    {
        var src = "public class C { public System.Type T() { return typeof(uint); } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("typeof(System.UInt32)", result.OutputCode);
    }

    [Fact]
    public void Lambda_ParameterAndReturnTypeReplaced()
    {
        var src = "using System; public class C { public Func<uint, uint> f = x => x + 1; }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("Func<System.UInt32, System.UInt32>", result.OutputCode);
        Assert.Contains("x.Add(1)", result.OutputCode);
    }

    [Fact]
    public void LocalFunction_TypeReplacedAndOperatorRewritten()
    {
        var src = "public class C { public void M() { uint Add(uint a, uint b) => a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("System.UInt32 Add(System.UInt32 a, System.UInt32 b)", result.OutputCode);
        Assert.Contains("a.Add(b)", result.OutputCode);
    }

    [Fact]
    public void MixedTypeOperation_IntOperandNotRewritten()
    {
        var src = "public class C { public int Add(int a, int b) { return a + b; } }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        // int + int 不应被重写
        Assert.DoesNotContain("a.Add(b)", result.OutputCode);
        Assert.Contains("a + b", result.OutputCode);
    }

    [Fact]
    public void AllOperators_Covered()
    {
        var src = @"public class C {
            public uint a, b;
            public void Test() {
                uint r1 = a + b; uint r2 = a - b; uint r3 = a * b;
                uint r4 = a / b; uint r5 = a % b; uint r6 = a & b;
                uint r7 = a | b; uint r8 = a ^ b; uint r9 = ~a;
                uint r10 = a << 2; uint r11 = a >> 2;
                bool e1 = a == b; bool e2 = a != b;
                bool e3 = a < b; bool e4 = a <= b;
                bool e5 = a > b; bool e6 = a >= b;
                a++; b--; ++a; --b;
                a += b; a -= b; a *= b; a /= b; a %= b;
                a &= b; a |= b; a ^= b;
            }
        }";
        var (tree, model) = ParseWithSemantics(src);
        var result = new UnsignedTypeRewriter().Rewrite(src, model);
        Assert.Contains("a.Add(b)", result.OutputCode);
        Assert.Contains("a.Subtract(b)", result.OutputCode);
        Assert.Contains("a.Multiply(b)", result.OutputCode);
        Assert.Contains("a.Divide(b)", result.OutputCode);
        Assert.Contains("a.Remainder(b)", result.OutputCode);
        Assert.Contains("a.And(b)", result.OutputCode);
        Assert.Contains("a.Or(b)", result.OutputCode);
        Assert.Contains("a.Xor(b)", result.OutputCode);
        Assert.Contains("a.Not()", result.OutputCode);
        Assert.Contains("a.LeftShift(2)", result.OutputCode);
        Assert.Contains("a.RightShift(2)", result.OutputCode);
        Assert.Contains("a.Equals(b)", result.OutputCode);
        Assert.Contains("!a.Equals(b)", result.OutputCode);
        Assert.Contains("a.CompareTo(b) < 0", result.OutputCode);
        Assert.Contains("a.CompareTo(b) <= 0", result.OutputCode);
        Assert.Contains("a.CompareTo(b) > 0", result.OutputCode);
        Assert.Contains("a.CompareTo(b) >= 0", result.OutputCode);
    }
}
```

#### `UnsignedTypeProjectTests.cs`

测试完整项目级前置转换流程（参照 `RefOutInProjectTests.cs` 和 `UintKeywordMaxValueTests.cs` 的模式）：

```csharp
public class UnsignedTypeProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleUnsignedSolution");

    [Fact]
    public async Task LibProject_AllUnsignedTypes_RewrittenCorrectly()
    {
        var libDir = Path.Combine(FixtureDir, "SampleUnsignedLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        // 运行前置转换
        var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
            new ProjectUnsignedTypePreprocessRequest
            {
                SourcePath = libDir,
                DestinationRoot = Path.Combine(libDir, "..", ".test-output"),
                Verbose = false,
            });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.True(result.Statistics.FilesTransformed > 0);

        // 验证中间目录中生成的 C# 文件
        var intermediateDir = result.IntermediateRoot;
        Assert.True(Directory.Exists(intermediateDir));

        // 验证类型替换
        var typesContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedTypes.cs"));
        Assert.Contains("System.UInt32", typesContent);
        Assert.Contains("System.UInt64", typesContent);
        Assert.Contains("System.UInt16", typesContent);
        Assert.Contains("System.Byte", typesContent);

        // 验证运算符重写
        var opsContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedOperators.cs"));
        Assert.Contains(".Add(", opsContent);
        Assert.Contains(".Subtract(", opsContent);
        Assert.Contains(".Multiply(", opsContent);
        Assert.Contains(".Equals(", opsContent);
        Assert.Contains(".CompareTo(", opsContent);

        // 验证 cast 重写
        var castsContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedCasts.cs"));
        Assert.Contains("(System.UInt32)", castsContent);
        Assert.Contains(".ToInt32()", castsContent);

        // 验证数组
        var arraysContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedArrays.cs"));
        Assert.Contains("System.UInt32[]", arraysContent);
        Assert.Contains("System.UInt32[,]", arraysContent);
        Assert.Contains("System.UInt32[][]", arraysContent);

        // 验证泛型
        var genericsContent = await File.ReadAllTextAsync(
            Path.Combine(intermediateDir, "UnsignedGenerics.cs"));
        Assert.Contains("List<System.UInt32>", genericsContent);
        Assert.Contains("Dictionary<System.UInt32, System.UInt64>", genericsContent);

        // 验证扩展方法文件存在
        Assert.True(File.Exists(Path.Combine(intermediateDir, "_Cs2jUnsignedExtensions.cs")),
            "Extension methods file not generated");

        // 验证 using 注入
        Assert.Contains("using Cs2jUnsigned;", opsContent);

        // 验证中间目录的 C# 代码能编译通过
        // (可通过 dotnet build 验证，或用 Roslyn 编译验证)
    }

    [Fact]
    public async Task AppProject_CrossProjectRewrite_Succeeds()
    {
        var appDir = Path.Combine(FixtureDir, "SampleUnsignedApp");
        var libDir = Path.Combine(FixtureDir, "SampleUnsignedLib");
        Assert.True(Directory.Exists(appDir));

        var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
            new ProjectUnsignedTypePreprocessRequest
            {
                SourcePath = Path.Combine(FixtureDir, "SampleUnsignedSolution.sln"),
                DestinationRoot = Path.Combine(FixtureDir, ".test-output-sln"),
                Verbose = false,
            });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }

    [Fact]
    public async Task SlnProject_FullRewrite_AllFilesSucceed()
    {
        var slnPath = Path.Combine(FixtureDir, "SampleUnsignedSolution.sln");
        Assert.True(File.Exists(slnPath), $"Solution not found: {slnPath}");

        var result = await ProjectUnsignedTypePreprocessor.PreprocessAsync(
            new ProjectUnsignedTypePreprocessRequest
            {
                SourcePath = slnPath,
                DestinationRoot = Path.Combine(FixtureDir, ".test-output-sln"),
                Verbose = false,
            });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.True(result.Statistics.FilesTransformed > 0);
    }
}
```

### 更新 `CSharpToJava.ProjectTests.csproj`

参照既有的 `SampleProject`/`SampleDefaultSolution`/`SampleRefOutInSolution` 的 Content Include 模式（`CSharpToJava.ProjectTests.csproj:36-57`），添加：

```xml
<ItemGroup>
  <Content Include="..\SampleUnsignedSolution\**\*.*">
    <Link>SampleUnsignedSolution\%(RecursiveDir)%(Filename)%(Extension)</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

---

## 验收标准

1. `dotnet build` 整个解决方案零错误。
2. `dotnet test` 全部通过，包括：
   - 新增的 `UnsignedTypeRewriterUnitTests`（单文件级别，覆盖全部场景）
   - 新增的 `UnsignedTypeProjectTests`（项目级别，含跨项目和解决方案级测试）
   - 既有测试无回归（特别是 `UintKeywordMaxValueTests`、`RefOutInProjectTests`、`DefaultValueProjectTests`）
3. 手动验证：对 fixture 项目执行 `unsigned-type` verb，输出的 C# 代码：
   - `uint` → `System.UInt32`（所有语法上下文）
   - `a + b` → `a.Add(b)`（扩展方法调用）
   - `(uint)x` → `(System.UInt32)x`；`(int)x`（x 为 uint）→ `x.ToInt32()`
   - `uint[]` → `System.UInt32[]`
   - `List<uint>` → `List<System.UInt32>`
   - `uint.MaxValue` → `System.UInt32.MaxValue`
   - `_Cs2jUnsignedExtensions.cs` 文件已生成
   - `using Cs2jUnsigned;` 已注入
4. 输出的 C# 代码能通过 `dotnet build` 编译。
5. `unsigned-type` verb 单独可用（单文件 + 目录模式）。
6. `convert-project --no-unsigned-type` 可禁用前置转换。
7. 代码风格与既有 `GotoEliminator`/`ReadOnlyStructMaker` 一致。

## 参考文件

| 文件 | 用途 |
|------|------|
| `src/CSharpToJava.Core/GotoEliminator/GotoEliminator.cs` | 前置转换入口模式参照 |
| `src/CSharpToJava.Core/GotoEliminator/GotoEliminatorOptions.cs` | Options record 模式 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMaker.cs` | 需 SemanticModel 的前置转换参照 |
| `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs` | CSharpSyntaxRewriter 子类参照 |
| `src/CSharpToJava.CLI/ProjectGotoPreprocessor.cs` | 项目级预处理器参照 |
| `src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs` | 需编译的预处理器参照 |
| `src/CSharpToJava.CLI/Program.cs:184-221` | MakeReadOnly 集成点参照 |
| `src/CSharpToJava.CLI/Program.cs:223-245` | EliminateGoto 集成点参照 |
| `src/CSharpToJava.CLI/Program.cs:2686-2767` | EliminateGoto verb 实现参照 |
| `src/CSharpToJava.CLI/Program.cs:2776-2863` | make-readonly verb 实现参照 |
| `src/CSharpToJava.CLI/Program.cs:2978-3072` | ConvertProjectOptions 结构参照 |
| `src/CSharpToJava.CLI/Program.cs:3035-3063` | NoEliminateGoto/NoMakeReadOnly 选项参照 |
| `tests/CSharpToJava.ProjectTests/RefOutInProjectTests.cs` | 项目测试模式参照 |
| `tests/CSharpToJava.ProjectTests/UintKeywordMaxValueTests.cs` | uint 相关测试参照 |
| `tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj:36-57` | Content Include 模式参照 |
| `tests/SampleRefOutInSolution/` | fixture 解决方案结构参照 |
