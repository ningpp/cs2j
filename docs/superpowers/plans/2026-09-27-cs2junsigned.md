# Cs2jUnsigned Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新建自包含类库 `src/Cs2jUnsigned`，以 `sbyte/short/int/long` 模拟 `dotnet.System.UByte/UShort/UInt/ULong` 四个 readonly struct，语义/API/转换与原生无符号类型等价。

**Architecture:** 四个 `public readonly struct`（命名空间 `dotnet.System`）薄封装 internal 静态助手 `UnsignedBits`；助手用位掩码、无符号位移、符号位折半除法、decimal 典范表示实现全部算术、比较、Parse、ToString、浮点转换。项目零引用（**禁止依赖 Cs2jUnsignedMath**）、零 `using`、零无符号关键字/类型。

**Tech Stack:** C# 11 / net10.0；xUnit（既有 `tests/CSharpToJava.Tests`，以原生 `byte/ushort/uint/ulong` 为运行时 oracle）；PowerShell 下 `dotnet build` / `dotnet test`。

**设计规格：** `docs/superpowers/specs/2026-09-27-cs2junsigned-design.md`（含 net10 探针锁定的 Parse/浮点/移位行为，实现必须与之相符）。

---

## 文件结构

| 文件 | 职责 |
|---|---|
| `src/Cs2jUnsigned/Cs2jUnsigned.csproj` | 类库工程；net10.0/Lang11/Nullable&ImplicitUsings 关闭；InternalsVisibleTo 测试 |
| `src/Cs2jUnsigned/UnsignedBits.cs` | internal static：8/16/32/64 域算术、比较、跨位宽折叠、decimal/浮点转换、Hex 格式化、Parse/TryParse 核心 |
| `src/Cs2jUnsigned/UByte.cs` | `public readonly struct UByte`（sbyte 存储） |
| `src/Cs2jUnsigned/UShort.cs` | `public readonly struct UShort`（short 存储） |
| `src/Cs2jUnsigned/UInt.cs` | `public readonly struct UInt`（int 存储） |
| `src/Cs2jUnsigned/ULong.cs` | `public readonly struct ULong`（long 存储） |
| `tests/CSharpToJava.Tests/Cs2jUnsigned/UnsignedBitsTests.cs` | 助手直接测试 |
| `tests/CSharpToJava.Tests/Cs2jUnsigned/UByteParityTests.cs` 等 4 个 | 各类型算术/比较/转换 oracle 对照 |
| `tests/CSharpToJava.Tests/Cs2jUnsigned/ParseFormatParityTests.cs` | 四类型 Parse/TryParse/ToString 对照 |
| 修改：`CSharpToJavaConverter.slnx` | 登记新项目 |
| 修改：`tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` | 增加 ProjectReference |

跨类型转换算子归属约定（避免重复定义）：**每个跨类型算子只定义在目标类型中**（向宽 implicit、向窄 explicit）。

> **编译注意（Task 2-6 全部实现源码生效，实测 CS0234）：** 文件位于 `namespace dotnet.System` 内时，裸写 `System.X` 会经外层 `dotnet` 命名空间绑定为 `dotnet.System.X`。所有 BCL 类型全名引用必须加 `global::` 前缀（`global::System.IEquatable<T>`、`global::System.Globalization.NumberStyles`、`global::System.Math`、`global::System.OverflowException` 等）；命名空间声明本身保持 `namespace dotnet.System`。下文代码块中的 `System.` 在落地时一律按此替换。测试文件中需要引用枚举类型时用文件别名 `using St = System.Globalization.NumberStyles;`（测试工程不在 dotnet.System 命名空间内，无需 global::）。

---

## Task 1: 工程脚手架

**Files:**
- Create: `src/Cs2jUnsigned/Cs2jUnsigned.csproj`
- Modify: `CSharpToJavaConverter.slnx`
- Modify: `tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`

- [ ] **Step 1: 创建 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>11</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">
      <_Parameter1>CSharpToJava.Tests</_Parameter1>
    </AssemblyAttribute>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 登记到解决方案**

在 `CSharpToJavaConverter.slnx` 第 5 行（Cs2jArray 项目条目）之后插入：

```xml
  <Project Path="src\Cs2jUnsigned\Cs2jUnsigned.csproj" Id="7c8d9e0f-1a2b-4c3d-9e8f-0a1b2c3d4e5f" Type="Microsoft.VisualStudio.Project" />
```

在 `<Folder Name="/src/">` 的 ProjectRef 列表末尾（`a1b2c3d4-...` 那行之后）插入：

```xml
    <ProjectRef Id="7c8d9e0f-1a2b-4c3d-9e8f-0a1b2c3d4e5f" />
```

- [ ] **Step 3: 测试工程增加引用**

在 `tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj` 的 ProjectReference ItemGroup 中追加：

```xml
    <ProjectReference Include="..\..\src\Cs2jUnsigned\Cs2jUnsigned.csproj" />
```

- [ ] **Step 4: 验证**

Run（PowerShell，仓库根目录）：`dotnet build CSharpToJavaConverter.slnx -v q`
Expected: 成功（新项目暂无 .cs 文件也可构建；若 SDK 因无源码报错，先创建 Task 2 的空占位再构建——正常 SDK 允许空项目）。

- [ ] **Step 5: Commit**

`git add src/Cs2jUnsigned/Cs2jUnsigned.csproj CSharpToJavaConverter.slnx tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj`
`git commit -m "feat(unsigned): scaffold Cs2jUnsigned class library"`

---

## Task 2: UnsignedBits 助手（算术/比较/转换/格式化/解析）

**Files:**
- Create: `src/Cs2jUnsigned/UnsignedBits.cs`
- Test: `tests/CSharpToJava.Tests/Cs2jUnsigned/UnsignedBitsTests.cs`

- [ ] **Step 1: 写失败测试**

创建 `tests/CSharpToJava.Tests/Cs2jUnsigned/UnsignedBitsTests.cs`（完整内容）：

```csharp
using dotnet.System;
using St = System.Globalization.NumberStyles;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class UnsignedBitsTests
{
    [Fact]
    public void Arithmetic8_16_WrapsAndMasks()
    {
        Assert.Equal(unchecked((sbyte)0), UnsignedBits.Add8(unchecked((sbyte)-1), (sbyte)1));      // 255+1
        Assert.Equal(unchecked((sbyte)200), UnsignedBits.Mul8((sbyte)20, (sbyte)10));
        Assert.Equal(unchecked((sbyte)0), UnsignedBits.Shl8((sbyte)1, 8));
        Assert.Equal(unchecked((sbyte)1), UnsignedBits.Shl8((sbyte)1, 32));
        Assert.Equal(unchecked((sbyte)0x0F), UnsignedBits.Shr8(unchecked((sbyte)0xF0), 4));
        Assert.Equal(unchecked((sbyte)0xFF), UnsignedBits.Not8((sbyte)0));
        Assert.Equal(1, UnsignedBits.Cmp8(unchecked((sbyte)200), (sbyte)100));
        Assert.Equal(unchecked((short)0), UnsignedBits.Add16(unchecked((short)-1), (short)1));     // 65535+1
        Assert.Equal(unchecked((short)0), UnsignedBits.Shl16((short)1, 16));
        Assert.Equal(1, UnsignedBits.Cmp16(unchecked((short)-1), (short)1));
        Assert.Equal(unchecked((sbyte)0x34), UnsignedBits.Narrow32To8(0x1234));
    }

    [Fact]
    public void Arithmetic32_DivideUsesUnsignedOrder()
    {
        Assert.Equal(1, UnsignedBits.Divide32(-1, -2));        // 0xFFFFFFFF / 0xFFFFFFFE = 1
        Assert.Equal(2147483648L, (long)UnsignedBits.Divide32(unchecked((int)0x80000000), 1) & 0xFFFFFFFFL);
        Assert.Equal(1, UnsignedBits.Cmp32(-1, 1));
        Assert.Equal(-1, UnsignedBits.Cmp32(1, -1));
        Assert.Equal(1, UnsignedBits.Shr32(-1, 31));           // 0xFFFFFFFF >>> 31 = 1
    }

    [Fact]
    public void Arithmetic64_HighBitDivisorFold()
    {
        Assert.Equal(1L, UnsignedBits.Divide64(-1L, -1L));          // max/max
        Assert.Equal(0L, UnsignedBits.Divide64(-2L, -1L));          // (max-1)/max
        Assert.Equal(9223372036854775807L, UnsignedBits.Divide64(-1L, 2L));
        Assert.Equal(1L, UnsignedBits.Remainder64(-1L, 2L));
        Assert.Equal(3074457345618258602L, UnsignedBits.Divide64(unchecked((long)0x8000000000000000), 3L));
        Assert.Equal(1, UnsignedBits.Cmp64(-1L, 1L));
        Assert.Equal(-1, UnsignedBits.Cmp64(1L, -1L));
        Assert.Equal(0, UnsignedBits.Cmp64(-2L, -2L));
        Assert.Throws<System.DivideByZeroException>(() => UnsignedBits.Divide64(1L, 0L));
    }

    [Fact]
    public void DecimalFold64_RoundTrips()
    {
        Assert.Equal(18446744073709551615m, UnsignedBits.ToDecimal64(-1L));
        Assert.Equal(9223372036854775808m, UnsignedBits.ToDecimal64(unchecked((long)0x8000000000000000)));
        Assert.Equal(-1L, UnsignedBits.FromDecimal64(18446744073709551615m));
        Assert.Equal(unchecked((long)0x8000000000000000), UnsignedBits.FromDecimal64(9223372036854775808m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.FromDecimal64(-1m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.FromDecimal64(18446744073709551616m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.FromDecimal64(1.5m));
    }

    [Fact]
    public void DoubleConversions_MatchOracle()
    {
        Assert.Equal(0, UnsignedBits.FromDouble8(double.NaN));
        Assert.Equal(unchecked((sbyte)-1), UnsignedBits.FromDouble8(double.PositiveInfinity));
        Assert.Equal(unchecked((sbyte)200), UnsignedBits.FromDouble8(200.9));
        Assert.Equal(-1, UnsignedBits.FromDouble32(4294967295.0));
        Assert.Equal(0, UnsignedBits.FromDouble32(-0.5));
        Assert.Equal(-1L, UnsignedBits.FromDouble64(18446744073709551615.0));
        Assert.Equal(unchecked((long)0x8000000000000000), UnsignedBits.FromDouble64(9223372036854775808.0));
        Assert.Equal(0L, UnsignedBits.FromDouble64(double.NaN));
        Assert.Equal(18446744073709551615.0.ToString("R"), UnsignedBits.ToDouble64(-1L).ToString("R"));
    }

    [Fact]
    public void FormatHex_MinimalNibblesAndPadding()
    {
        Assert.Equal("1", UnsignedBits.FormatHex(1L, "X"));
        Assert.Equal("001", UnsignedBits.FormatHex(1L, "X3"));
        Assert.Equal("ffffffffffffffff", UnsignedBits.FormatHex(-1L, "x"));
        Assert.Equal("0000FFFFFFFFFFFFFFFF", UnsignedBits.FormatHex(-1L, "X20"));
        Assert.Equal("FF", UnsignedBits.FormatHex(255L, "X2"));
    }

    [Fact]
    public void ParseUnsigned_OracleBehaviors()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(100m, UnsignedBits.ParseUnsigned("1e2", St.Integer | St.AllowExponent, inv, 4294967295m));
        Assert.Equal(100m, UnsignedBits.ParseUnsigned("1,00", St.Integer | St.AllowThousands, inv, 4294967295m));
        Assert.Equal(1000m, UnsignedBits.ParseUnsigned("1,,000", St.Integer | St.AllowThousands, inv, 4294967295m));
        Assert.Throws<System.FormatException>(() => UnsignedBits.ParseUnsigned(",100", St.Integer | St.AllowThousands, inv, 4294967295m));
        Assert.Equal(1m, UnsignedBits.ParseUnsigned("1.0", St.Integer | St.AllowDecimalPoint, inv, 4294967295m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.ParseUnsigned("1.5", St.Integer | St.AllowDecimalPoint, inv, 4294967295m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.ParseUnsigned("(1)", St.AllowParentheses | St.AllowLeadingSign, inv, 4294967295m));
        Assert.Equal(0m, UnsignedBits.ParseUnsigned("(0)", St.AllowParentheses, inv, 4294967295m));
        Assert.Throws<System.FormatException>(() => UnsignedBits.ParseUnsigned("+1-", St.AllowLeadingSign | St.AllowTrailingSign, inv, 4294967295m));
        Assert.Throws<System.FormatException>(() => UnsignedBits.ParseUnsigned("0xFF", St.HexNumber, inv, 4294967295m));
        Assert.Equal(10m, UnsignedBits.ParseUnsigned("1010", St.AllowBinarySpecifier, inv, 255m));
        Assert.Throws<System.FormatException>(() => UnsignedBits.ParseUnsigned("0b1010", St.AllowBinarySpecifier, inv, 255m));
        Assert.Throws<System.ArgumentException>(() => UnsignedBits.ParseUnsigned("1", St.HexNumber | St.AllowThousands, inv, 4294967295m));
        Assert.Throws<System.ArgumentException>(() => UnsignedBits.ParseUnsigned("1", St.AllowBinarySpecifier | St.AllowLeadingSign, inv, 255m));
        Assert.False(UnsignedBits.TryParseUnsigned("999", St.Integer, inv, 255m, out _));
        Assert.False(UnsignedBits.TryParseUnsigned("0xFF", St.HexNumber, inv, 255m, out _));
        Assert.Throws<System.ArgumentNullException>(() => UnsignedBits.ParseUnsigned(null, St.Integer, inv, 255m));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~Cs2jUnsigned.UnsignedBitsTests" -v q`
Expected: 编译失败（UnsignedBits 不存在）。

- [ ] **Step 3: 实现 `src/Cs2jUnsigned/UnsignedBits.cs`（完整内容）**

```csharp
namespace dotnet.System
{
    internal static class UnsignedBits
    {
        // ================================================================
        // 8-bit domain (sbyte storage)
        // ================================================================
        public static sbyte Add8(sbyte a, sbyte b) { unchecked { return (sbyte)(((int)a & 0xFF) + ((int)b & 0xFF)); } }
        public static sbyte Sub8(sbyte a, sbyte b) { unchecked { return (sbyte)(((int)a & 0xFF) - ((int)b & 0xFF)); } }
        public static sbyte Mul8(sbyte a, sbyte b) { unchecked { return (sbyte)(((int)a & 0xFF) * ((int)b & 0xFF)); } }
        public static sbyte Div8(sbyte a, sbyte b) { return (sbyte)(((int)a & 0xFF) / ((int)b & 0xFF)); }
        public static sbyte Rem8(sbyte a, sbyte b) { return (sbyte)(((int)a & 0xFF) % ((int)b & 0xFF)); }
        public static sbyte And8(sbyte a, sbyte b) { return (sbyte)((int)a & (int)b); }
        public static sbyte Or8(sbyte a, sbyte b) { return (sbyte)((int)a | (int)b); }
        public static sbyte Xor8(sbyte a, sbyte b) { return (sbyte)((int)a ^ (int)b); }
        public static sbyte Not8(sbyte a) { return (sbyte)~(int)a; }
        public static sbyte Shl8(sbyte a, int n) { unchecked { return (sbyte)(((int)a & 0xFF) << (n & 31)); } }
        public static sbyte Shr8(sbyte a, int n) { return (sbyte)((((int)a & 0xFF) >>> (n & 31)) & 0xFF); }
        public static int Cmp8(sbyte a, sbyte b) { int x = (int)a & 0xFF; int y = (int)b & 0xFF; return x < y ? -1 : x > y ? 1 : 0; }

        // ================================================================
        // 16-bit domain (short storage)
        // ================================================================
        public static short Add16(short a, short b) { unchecked { return (short)(((int)a & 0xFFFF) + ((int)b & 0xFFFF)); } }
        public static short Sub16(short a, short b) { unchecked { return (short)(((int)a & 0xFFFF) - ((int)b & 0xFFFF)); } }
        public static short Mul16(short a, short b) { unchecked { return (short)(((int)a & 0xFFFF) * ((int)b & 0xFFFF)); } }
        public static short Div16(short a, short b) { return (short)(((int)a & 0xFFFF) / ((int)b & 0xFFFF)); }
        public static short Rem16(short a, short b) { return (short)(((int)a & 0xFFFF) % ((int)b & 0xFFFF)); }
        public static short And16(short a, short b) { return (short)((int)a & (int)b); }
        public static short Or16(short a, short b) { return (short)((int)a | (int)b); }
        public static short Xor16(short a, short b) { return (short)((int)a ^ (int)b); }
        public static short Not16(short a) { return (short)~(int)a; }
        public static short Shl16(short a, int n) { unchecked { return (short)(((int)a & 0xFFFF) << (n & 31)); } }
        public static short Shr16(short a, int n) { return (short)((((int)a & 0xFFFF) >>> (n & 31)) & 0xFFFF); }
        public static int Cmp16(short a, short b) { int x = (int)a & 0xFFFF; int y = (int)b & 0xFFFF; return x < y ? -1 : x > y ? 1 : 0; }

        // ================================================================
        // 32-bit domain (int storage, unsigned interpretation)
        // ================================================================
        public static int Add32(int a, int b) { unchecked { return a + b; } }
        public static int Sub32(int a, int b) { unchecked { return a - b; } }
        public static int Mul32(int a, int b) { unchecked { return a * b; } }
        public static int Divide32(int a, int b) { return (int)(((long)a & 0xFFFFFFFFL) / ((long)b & 0xFFFFFFFFL)); }
        public static int Remainder32(int a, int b) { return (int)(((long)a & 0xFFFFFFFFL) % ((long)b & 0xFFFFFFFFL)); }
        public static int And32(int a, int b) { return a & b; }
        public static int Or32(int a, int b) { return a | b; }
        public static int Xor32(int a, int b) { return a ^ b; }
        public static int Not32(int a) { return ~a; }
        public static int Shl32(int a, int n) { return a << (n & 31); }
        public static int Shr32(int a, int n) { return a >>> (n & 31); }
        public static int Cmp32(int a, int b)
        {
            long x = (long)a & 0xFFFFFFFFL;
            long y = (long)b & 0xFFFFFFFFL;
            return x < y ? -1 : x > y ? 1 : 0;
        }

        // ================================================================
        // 64-bit domain (long storage, unsigned interpretation)
        // ================================================================
        public static long Add64(long a, long b) { unchecked { return a + b; } }
        public static long Sub64(long a, long b) { unchecked { return a - b; } }
        public static long Mul64(long a, long b) { unchecked { return a * b; } }
        public static long And64(long a, long b) { return a & b; }
        public static long Or64(long a, long b) { return a | b; }
        public static long Xor64(long a, long b) { return a ^ b; }
        public static long Not64(long a) { return ~a; }
        public static long Shl64(long a, int n) { return a << (n & 63); }
        public static long Shr64(long a, int n) { return a >>> (n & 63); }
        public static int Cmp64(long a, long b)
        {
            if (a == b) return 0;
            if (a >= 0 && b < 0) return -1;
            if (a < 0 && b >= 0) return 1;
            return a < b ? -1 : 1;
        }
        public static long Divide64(long a, long b)
        {
            if (b == 0)
            {
                return a / b; // 自然抛 DivideByZeroException
            }
            if (b < 0)
            {
                return Cmp64(a, b) < 0 ? 0L : 1L;
            }
            if (a >= 0)
            {
                return a / b;
            }
            long quotient = unchecked(((a >>> 1) / b) * 2L);
            long remainder = unchecked(a - quotient * b);
            return quotient + (Cmp64(remainder, b) >= 0 ? 1L : 0L);
        }
        public static long Remainder64(long a, long b) { unchecked { return a - Divide64(a, b) * b; } }

        // ================================================================
        // Cross-width narrowing / extension
        // ================================================================
        public static short Extend8To16(sbyte v) { return (short)((int)v & 0xFF); }
        public static int Extend8To32(sbyte v) { return (int)v & 0xFF; }
        public static long Extend8To64(sbyte v) { return (long)((int)v & 0xFF); }
        public static int Extend16To32(short v) { return (int)v & 0xFFFF; }
        public static long Extend16To64(short v) { return (long)((int)v & 0xFFFF); }
        public static long Extend32To64(int v) { return (long)v & 0xFFFFFFFFL; }
        public static sbyte Narrow16To8(short v) { return (sbyte)v; }
        public static sbyte Narrow32To8(int v) { return (sbyte)(v & 0xFF); }
        public static short Narrow32To16(int v) { return (short)(v & 0xFFFF); }
        public static sbyte Narrow64To8(long v) { return (sbyte)(v & 0xFFL); }
        public static short Narrow64To16(long v) { return (short)(v & 0xFFFFL); }
        public static int Narrow64To32(long v) { return (int)v; }

        // ================================================================
        // Decimal / float canonical conversions
        // ================================================================
        public static decimal ToDecimal8(sbyte v) { return (decimal)((int)v & 0xFF); }
        public static decimal ToDecimal16(short v) { return (decimal)((int)v & 0xFFFF); }
        public static decimal ToDecimal32(int v) { return (decimal)((long)v & 0xFFFFFFFFL); }
        public static decimal ToDecimal64(long v)
        {
            unchecked { return (decimal)(v >>> 1) * 2m + (decimal)(v & 1L); }
        }
        public static sbyte FromDecimal8(decimal d)
        {
            if (d < 0m || d > 255m || d != System.Math.Truncate(d)) throw new System.OverflowException();
            return (sbyte)(int)d;
        }
        public static short FromDecimal16(decimal d)
        {
            if (d < 0m || d > 65535m || d != System.Math.Truncate(d)) throw new System.OverflowException();
            return (short)(int)d;
        }
        public static int FromDecimal32(decimal d)
        {
            if (d < 0m || d > 4294967295m || d != System.Math.Truncate(d)) throw new System.OverflowException();
            return (int)(long)d;
        }
        public static long FromDecimal64(decimal d)
        {
            if (d < 0m || d > 18446744073709551615m || d != System.Math.Truncate(d)) throw new System.OverflowException();
            unchecked { return d >= 9223372036854775808m ? (long)(d - 18446744073709551616m) : (long)d; }
        }
        public static double ToDouble8(sbyte v) { return (double)((int)v & 0xFF); }
        public static double ToDouble16(short v) { return (double)((int)v & 0xFFFF); }
        public static double ToDouble32(int v) { return (double)((long)v & 0xFFFFFFFFL); }
        public static double ToDouble64(long v) { return (double)ToDecimal64(v); }
        public static float ToSingle8(sbyte v) { return (float)((int)v & 0xFF); }
        public static float ToSingle16(short v) { return (float)((int)v & 0xFFFF); }
        public static float ToSingle32(int v) { return (float)((long)v & 0xFFFFFFFFL); }
        public static float ToSingle64(long v) { return (float)ToDecimal64(v); }
        // 可移植饱和规则（见规格 §5.3）：NaN/负→0；截断后超 MaxValue→MaxValue。
        public static sbyte FromDouble8(double d)
        {
            if (double.IsNaN(d) || d < 0.0) return (sbyte)0;
            double t = System.Math.Truncate(d);
            if (t > 255.0) return unchecked((sbyte)-1);
            return (sbyte)(long)t;
        }
        public static short FromDouble16(double d)
        {
            if (double.IsNaN(d) || d < 0.0) return (short)0;
            double t = System.Math.Truncate(d);
            if (t > 65535.0) return unchecked((short)-1);
            return (short)(long)t;
        }
        public static int FromDouble32(double d)
        {
            if (double.IsNaN(d) || d < 0.0) return 0;
            double t = System.Math.Truncate(d);
            if (t > 4294967295.0) return -1;
            return (int)(long)t;
        }
        public static long FromDouble64(double d)
        {
            if (double.IsNaN(d) || d < 0.0) return 0L;
            double t = System.Math.Truncate(d);
            if (t >= 18446744073709551616.0) return -1L;
            unchecked
            {
                if (t >= 9223372036854775808.0) return (long)(t - 18446744073709551616.0);
                return (long)t;
            }
        }
        public static sbyte FromSingle8(float f) { return FromDouble8((double)f); }
        public static short FromSingle16(float f) { return FromDouble16((double)f); }
        public static int FromSingle32(float f) { return FromDouble32((double)f); }
        public static long FromSingle64(float f) { return FromDouble64((double)f); }

        // ================================================================
        // ToString helpers
        // ================================================================
        private static System.Globalization.CultureInfo Invariant
        {
            get { return System.Globalization.CultureInfo.InvariantCulture; }
        }
        public static string Plain8(sbyte v) { return ((int)v & 0xFF).ToString(Invariant); }
        public static string Plain16(short v) { return ((int)v & 0xFFFF).ToString(Invariant); }
        public static string Plain32(int v) { return ((long)v & 0xFFFFFFFFL).ToString(Invariant); }
        public static string Plain64(long v) { return ToDecimal64(v).ToString(Invariant); }
        // bits 传无符号典范位模式（8/16/32 位先零扩展为 long；64 位直接传）。
        public static string FormatHex(long bits, string format)
        {
            bool upper = format[0] == 'X';
            int precision = 0;
            for (int i = 1; i < format.Length; i++)
            {
                char c = format[i];
                if (c < '0' || c > '9') throw new System.FormatException("Invalid format string.");
                precision = precision * 10 + (c - '0');
            }
            int used;
            if (bits == 0)
            {
                used = 1;
            }
            else
            {
                used = 0;
                long tmp = bits;
                while (tmp != 0)
                {
                    used++;
                    tmp = tmp >>> 4;
                }
            }
            int len = used > precision ? used : precision;
            char[] chars = new char[len];
            string map = upper ? "0123456789ABCDEF" : "0123456789abcdef";
            for (int i = 0; i < len; i++)
            {
                int shift = 4 * (len - 1 - i);
                int nibble = shift >= 64 ? 0 : (int)(bits >>> shift) & 0xF;
                chars[i] = map[nibble];
            }
            return new string(chars);
        }

        // ================================================================
        // Parse / TryParse core（net10 探针行为，见设计 §6.6）
        // ================================================================
        private const System.Globalization.NumberStyles HexAllowed =
            System.Globalization.NumberStyles.AllowLeadingWhite
            | System.Globalization.NumberStyles.AllowTrailingWhite
            | System.Globalization.NumberStyles.AllowHexSpecifier;
        private const System.Globalization.NumberStyles BinAllowed =
            System.Globalization.NumberStyles.AllowLeadingWhite
            | System.Globalization.NumberStyles.AllowTrailingWhite
            | System.Globalization.NumberStyles.AllowBinarySpecifier;
        private const System.Globalization.NumberStyles DecAllowed =
            System.Globalization.NumberStyles.AllowLeadingWhite
            | System.Globalization.NumberStyles.AllowTrailingWhite
            | System.Globalization.NumberStyles.AllowLeadingSign
            | System.Globalization.NumberStyles.AllowTrailingSign
            | System.Globalization.NumberStyles.AllowParentheses
            | System.Globalization.NumberStyles.AllowDecimalPoint
            | System.Globalization.NumberStyles.AllowThousands
            | System.Globalization.NumberStyles.AllowExponent
            | System.Globalization.NumberStyles.AllowCurrencySymbol;

        public static bool TryParseUnsigned(string s, System.Globalization.NumberStyles style,
            System.IFormatProvider provider, decimal maxValue, out decimal result)
        {
            try
            {
                result = ParseUnsigned(s, style, provider, maxValue);
                return true;
            }
            catch (System.FormatException)
            {
                result = 0m;
                return false;
            }
            catch (System.OverflowException)
            {
                result = 0m;
                return false;
            }
        }

        public static decimal ParseUnsigned(string s, System.Globalization.NumberStyles style,
            System.IFormatProvider provider, decimal maxValue)
        {
            if (s == null) throw new System.ArgumentNullException("s");
            System.Globalization.NumberFormatInfo nfi = System.Globalization.NumberFormatInfo.GetInstance(provider);
            int mode = ValidateStyle(style);
            string t = TrimEnds(s, style);
            if (t.Length == 0) throw new System.FormatException("Input string was not in a correct format.");
            if (mode != 0)
            {
                return ParseRadix(t, mode == 1 ? 16 : 2, maxValue);
            }

            if ((style & System.Globalization.NumberStyles.AllowCurrencySymbol) != 0
                && nfi.CurrencySymbol.Length > 0
                && t.StartsWith(nfi.CurrencySymbol, System.StringComparison.Ordinal))
            {
                t = t.Substring(nfi.CurrencySymbol.Length);
            }
            bool negative = false;
            if ((style & System.Globalization.NumberStyles.AllowParentheses) != 0
                && t.Length >= 2 && t[0] == '(' && t[t.Length - 1] == ')')
            {
                t = t.Substring(1, t.Length - 2);
                negative = true;
            }
            bool leadSign = false;
            bool trailSign = false;
            if ((style & System.Globalization.NumberStyles.AllowLeadingSign) != 0
                && t.Length > 0 && IsSignChar(t[0], nfi))
            {
                if (t[0] == NegativeSignChar(nfi)) negative = true;
                t = t.Substring(1);
                leadSign = true;
            }
            if ((style & System.Globalization.NumberStyles.AllowTrailingSign) != 0
                && t.Length > 0 && IsSignChar(t[t.Length - 1], nfi))
            {
                if (t[t.Length - 1] == NegativeSignChar(nfi)) negative = true;
                t = t.Substring(0, t.Length - 1);
                trailSign = true;
            }
            if (leadSign && trailSign) throw new System.FormatException("Input string was not in a correct format.");
            if (t.Length == 0) throw new System.FormatException("Input string was not in a correct format.");

            int exponent = 0;
            int expPos = IndexOfExponentMarker(t);
            if (expPos >= 0)
            {
                if ((style & System.Globalization.NumberStyles.AllowExponent) == 0)
                    throw new System.FormatException("Input string was not in a correct format.");
                string expText = t.Substring(expPos + 1);
                t = t.Substring(0, expPos);
                exponent = ParseExponentDigits(expText);
            }

            string groupSep = nfi.NumberGroupSeparator;
            string decimalSep = nfi.NumberDecimalSeparator;
            int dot = t.IndexOf(decimalSep, System.StringComparison.Ordinal);
            if (dot >= 0)
            {
                if ((style & System.Globalization.NumberStyles.AllowDecimalPoint) == 0)
                    throw new System.FormatException("Input string was not in a correct format.");
                if (t.IndexOf(decimalSep, dot + decimalSep.Length, System.StringComparison.Ordinal) >= 0)
                    throw new System.FormatException("Input string was not in a correct format.");
            }
            string intPart = t;
            string fracPart = string.Empty;
            if (dot >= 0)
            {
                intPart = t.Substring(0, dot);
                fracPart = t.Substring(dot + decimalSep.Length);
            }
            if (groupSep.Length > 0 && intPart.IndexOf(groupSep, System.StringComparison.Ordinal) >= 0)
            {
                if ((style & System.Globalization.NumberStyles.AllowThousands) == 0)
                    throw new System.FormatException("Input string was not in a correct format.");
                if (intPart.StartsWith(groupSep, System.StringComparison.Ordinal)
                    || intPart.EndsWith(groupSep, System.StringComparison.Ordinal))
                    throw new System.FormatException("Input string was not in a correct format.");
                intPart = intPart.Replace(groupSep, string.Empty);
            }
            if (fracPart.IndexOf(groupSep, System.StringComparison.Ordinal) >= 0)
                throw new System.FormatException("Input string was not in a correct format.");

            // 先累加完整尾数（含小数位，10 进幂精确），再施加指数；
            // 最终统一做整数/范围校验。这样 "1.5e2" = 150 与原生一致，
            // 而 "1.5"（无指数）仍由末尾 Truncate 校验抛 Overflow。
            bool anyDigit = false;
            decimal acc = 0m;
            foreach (char c in intPart)
            {
                int digit = NativeDigit(c, nfi);
                if (digit < 0) throw new System.FormatException("Input string was not in a correct format.");
                acc = acc * 10m + digit;
                anyDigit = true;
            }
            if (!anyDigit) throw new System.FormatException("Input string was not in a correct format.");
            decimal fracScale = 1m;
            foreach (char c in fracPart)
            {
                int digit = NativeDigit(c, nfi);
                if (digit < 0) throw new System.FormatException("Input string was not in a correct format.");
                fracScale *= 10m;
                acc += digit / fracScale;
            }

            if (exponent != 0) acc = ApplyExponent(acc, exponent);
            if (negative) acc = -acc;
            if (acc != System.Math.Truncate(acc)) throw new System.OverflowException("Value was either too large or too small.");
            if (acc < 0m || acc > maxValue) throw new System.OverflowException("Value was either too large or too small.");
            return acc;
        }

        private static int ValidateStyle(System.Globalization.NumberStyles style)
        {
            if ((style & System.Globalization.NumberStyles.AllowHexSpecifier) != 0)
            {
                if ((style & ~HexAllowed) != 0)
                    throw new System.ArgumentException("The number style is not valid for this operation.");
                return 1;
            }
            if ((style & System.Globalization.NumberStyles.AllowBinarySpecifier) != 0)
            {
                if ((style & ~BinAllowed) != 0)
                    throw new System.ArgumentException("The number style is not valid for this operation.");
                return 2;
            }
            if ((style & ~DecAllowed) != 0)
                throw new System.ArgumentException("The number style is not valid for this operation.");
            return 0;
        }

        private static string TrimEnds(string s, System.Globalization.NumberStyles style)
        {
            int start = 0;
            int end = s.Length;
            if ((style & System.Globalization.NumberStyles.AllowLeadingWhite) != 0)
            {
                while (start < end && char.IsWhiteSpace(s[start])) start++;
            }
            if ((style & System.Globalization.NumberStyles.AllowTrailingWhite) != 0)
            {
                while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
            }
            return s.Substring(start, end - start);
        }

        private static decimal ParseRadix(string t, int radix, decimal maxValue)
        {
            decimal acc = 0m;
            bool any = false;
            foreach (char c in t)
            {
                int digit;
                if (radix == 16)
                {
                    if (c >= '0' && c <= '9') digit = c - '0';
                    else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                    else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                    else throw new System.FormatException("Input string was not in a correct format.");
                }
                else
                {
                    if (c == '0' || c == '1') digit = c - '0';
                    else throw new System.FormatException("Input string was not in a correct format.");
                }
                acc = acc * radix + digit;
                any = true;
            }
            if (!any) throw new System.FormatException("Input string was not in a correct format.");
            if (acc > maxValue) throw new System.OverflowException("Value was either too large or too small.");
            return acc;
        }

        private static bool IsSignChar(char c, System.Globalization.NumberFormatInfo nfi)
        {
            return (nfi.PositiveSign.Length == 1 && c == nfi.PositiveSign[0])
                || (nfi.NegativeSign.Length == 1 && c == nfi.NegativeSign[0]);
        }
        private static char NegativeSignChar(System.Globalization.NumberFormatInfo nfi)
        {
            return nfi.NegativeSign.Length == 1 ? nfi.NegativeSign[0] : '-';
        }
        private static int IndexOfExponentMarker(string t)
        {
            int pos = -1;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == 'e' || t[i] == 'E')
                {
                    if (pos >= 0) return -1;
                    pos = i;
                }
            }
            return pos;
        }
        private static int ParseExponentDigits(string text)
        {
            if (text.Length == 0 || text.Length > 10)
                throw new System.FormatException("Input string was not in a correct format.");
            int i = 0;
            bool neg = false;
            if (text[0] == '+' || text[0] == '-')
            {
                neg = text[0] == '-';
                i = 1;
            }
            if (i >= text.Length)
                throw new System.FormatException("Input string was not in a correct format.");
            long value = 0;
            for (; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                    throw new System.FormatException("Input string was not in a correct format.");
                value = value * 10 + (text[i] - '0');
                if (value > 1000000)
                    throw new System.FormatException("Input string was not in a correct format.");
            }
            return neg ? -(int)value : (int)value;
        }
        private static decimal ApplyExponent(decimal acc, int exponent)
        {
            if (exponent > 0)
            {
                for (int i = 0; i < exponent; i++)
                {
                    acc *= 10m;
                    if (acc > 18446744073709551615m)
                        throw new System.OverflowException("Value was either too large or too small.");
                }
            }
            else
            {
                for (int i = 0; i < -exponent; i++)
                {
                    acc /= 10m;
                }
            }
            return acc;
        }
        private static int NativeDigit(char c, System.Globalization.NumberFormatInfo nfi)
        {
            if (c >= '0' && c <= '9') return c - '0';
            string[] nativeDigits = nfi.NativeDigits;
            for (int i = 0; i < 10; i++)
            {
                if (nativeDigits[i].Length == 1 && nativeDigits[i][0] == c) return i;
            }
            return -1;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~Cs2jUnsigned.UnsignedBitsTests" -v q`
Expected: PASS 全部 7 个测试。若个别探针行为断言失败，以 `dotnet` 实测为准调整实现（不得放宽断言去迁就实现）。

- [ ] **Step 5: Commit**

`git add src/Cs2jUnsigned/UnsignedBits.cs tests/CSharpToJava.Tests/Cs2jUnsigned/UnsignedBitsTests.cs`
`git commit -m "feat(unsigned): add self-contained UnsignedBits helper"`

---

## Task 3: UByte

**Files:**
- Create: `src/Cs2jUnsigned/UByte.cs`
- Test: `tests/CSharpToJava.Tests/Cs2jUnsigned/UByteParityTests.cs`

- [ ] **Step 1: 写失败测试**（完整内容）

```csharp
using dotnet.System;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class UByteParityTests
{
    [Fact]
    public void ConstantsAndStorage()
    {
        Assert.Equal(0, (int)UByte.MinValue);
        Assert.Equal(255, (int)UByte.MaxValue);
        Assert.Equal(200, (int)new UByte((sbyte)200));
        Assert.Equal(0, (int)new UByte(256));
        Assert.Equal(1, (int)new UByte(257L));
    }

    [Fact]
    public void ArithmeticCompare_ParityWithNativeByte()
    {
        var rnd = new System.Random(7311);
        for (int i = 0; i < 3000; i++)
        {
            int x = rnd.Next();
            int y = rnd.Next();
            int n = rnd.Next(-70, 80);
            byte a = (byte)x;
            byte b = (byte)y;
            UByte ua = new UByte(x);
            UByte ub = new UByte(y);
            Assert.Equal((int)unchecked((byte)(a + b)), (int)(ua + ub));
            Assert.Equal((int)unchecked((byte)(a - b)), (int)(ua - ub));
            Assert.Equal((int)unchecked((byte)(a * b)), (int)(ua * b));
            if (b != 0)
            {
                Assert.Equal((int)(a / b), (int)(ua / ub));
                Assert.Equal((int)(a % b), (int)(ua % ub));
            }
            Assert.Equal((int)(a & b), (int)(ua & ub));
            Assert.Equal((int)(a | b), (int)(ua | ub));
            Assert.Equal((int)(a ^ b), (int)(ua ^ ub));
            // byte 的 ~ 会提升为 int，oracle 侧必须显式截回 byte 再比较。
            Assert.Equal((int)unchecked((byte)~a), (int)~ua);
            Assert.Equal((int)unchecked((byte)(a << n)), (int)(ua << n));
            Assert.Equal((int)(a >> n), (int)(ua >> n));
            Assert.Equal((int)(a >>> n), (int)(ua >>> n));
            Assert.Equal(a == b, ua == ub);
            Assert.Equal(a != b, ua != ub);
            Assert.Equal(a < b, ua < ub);
            Assert.Equal(a <= b, ua <= ub);
            Assert.Equal(a > b, ua > ub);
            Assert.Equal(a >= b, ua >= ub);
            Assert.Equal(a.GetHashCode(), ua.GetHashCode());
            Assert.Equal(a.CompareTo(b), ua.CompareTo(ub));
            Assert.Equal(a.Equals((object)b), ua.Equals((object)ub));
        }
        Assert.Throws<System.DivideByZeroException>(() => new UByte(1) / new UByte(0));
        Assert.Equal(1, new UByte(1).CompareTo((object)null));
        Assert.Throws<System.ArgumentException>(() => new UByte(1).CompareTo((object)1));
        Assert.False(new UByte(1).Equals((object)null));
    }

    [Fact]
    public void SignedAndCrossConversions_Parity()
    {
        var rnd = new System.Random(7312);
        for (int i = 0; i < 3000; i++)
        {
            int x = rnd.Next();
            byte a = (byte)x;
            UByte ua = new UByte(x);
            sbyte sb = (sbyte)x;
            short sh = (short)x;
            int iv = x;
            long lv = x - 100000;
            Assert.Equal((sbyte)a, (sbyte)ua);
            Assert.Equal((short)a, (short)ua);
            Assert.Equal((int)a, (int)ua);
            Assert.Equal((long)a, (long)ua);
            Assert.Equal((int)(byte)sb, (int)(UByte)sb);
            Assert.Equal((int)(byte)sh, (int)(UByte)sh);
            Assert.Equal((int)(byte)iv, (int)(UByte)iv);
            Assert.Equal((int)(byte)lv, (int)(UByte)lv);
            Assert.Equal((int)(ushort)a, (int)(UShort)ua);
            Assert.Equal((long)(uint)a, (long)(UInt)ua);
            Assert.Equal((long)(ulong)a, (long)(ULong)ua);
        }
        Assert.Equal(0, (int)(UByte)new UShort(256));
        Assert.Equal(1, (int)(UByte)new UInt(257));
        Assert.Equal(2, (int)(UByte)new ULong(258));
    }

    [Fact]
    public void FloatAndDecimalConversions()
    {
        Assert.Equal((int)(byte)200.9, (int)(UByte)200.9);
        Assert.Equal((int)(byte)255.0, (int)(UByte)255.0);
        Assert.Equal(0, (int)(UByte)double.NaN);
        Assert.Equal(0, (int)(UByte)(-1.0));
        Assert.Equal(255, (int)(UByte)double.PositiveInfinity);
        Assert.Equal(200, (int)(UByte)200.9f);
        Assert.Equal((double)(byte)200, new UByte(200).ToDouble());
        Assert.Equal((float)(byte)200, new UByte(200).ToSingle());
        Assert.Equal((decimal)(byte)200, new UByte(200).ToDecimal());
        Assert.Equal((int)(byte)5m, (int)(UByte)5m);
        Assert.Throws<System.OverflowException>(() => (UByte)(-1m));
        Assert.Throws<System.OverflowException>(() => (UByte)256m);
        Assert.Throws<System.OverflowException>(() => (UByte)1.5m);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~Cs2jUnsigned.UByteParityTests" -v q`
Expected: 编译失败（UByte 不存在）。

- [ ] **Step 3: 实现 `src/Cs2jUnsigned/UByte.cs`（完整内容）**

```csharp
namespace dotnet.System
{
    public readonly struct UByte
        : System.IEquatable<UByte>, System.IComparable<UByte>, System.IComparable, System.IFormattable
    {
        private readonly sbyte _v;

        public static readonly UByte MinValue = new UByte((sbyte)0);
        public static readonly UByte MaxValue = new UByte(unchecked((sbyte)-1));

        public UByte(sbyte value) { unchecked { _v = (sbyte)((int)value & 0xFF); } }
        public UByte(int value) { unchecked { _v = (sbyte)(value & 0xFF); } }
        public UByte(long value) { unchecked { _v = (sbyte)(value & 0xFFL); } }

        internal static UByte FromBits(sbyte bits) { return new UByte(bits); }
        internal sbyte Bits { get { return _v; } }

        // ---- arithmetic / bitwise ----
        public static UByte operator +(UByte a, UByte b) { return FromBits(UnsignedBits.Add8(a._v, b._v)); }
        public static UByte operator -(UByte a, UByte b) { return FromBits(UnsignedBits.Sub8(a._v, b._v)); }
        public static UByte operator *(UByte a, UByte b) { return FromBits(UnsignedBits.Mul8(a._v, b._v)); }
        public static UByte operator /(UByte a, UByte b) { return FromBits(UnsignedBits.Div8(a._v, b._v)); }
        public static UByte operator %(UByte a, UByte b) { return FromBits(UnsignedBits.Rem8(a._v, b._v)); }
        public static UByte operator &(UByte a, UByte b) { return FromBits(UnsignedBits.And8(a._v, b._v)); }
        public static UByte operator |(UByte a, UByte b) { return FromBits(UnsignedBits.Or8(a._v, b._v)); }
        public static UByte operator ^(UByte a, UByte b) { return FromBits(UnsignedBits.Xor8(a._v, b._v)); }
        public static UByte operator ~(UByte a) { return FromBits(UnsignedBits.Not8(a._v)); }
        public static UByte operator <<(UByte a, int n) { return FromBits(UnsignedBits.Shl8(a._v, n)); }
        public static UByte operator >>(UByte a, int n) { return FromBits(UnsignedBits.Shr8(a._v, n)); }
        public static UByte operator >>>(UByte a, int n) { return FromBits(UnsignedBits.Shr8(a._v, n)); }

        // ---- equality / comparison ----
        public bool Equals(UByte other) { return _v == other._v; }
        public override bool Equals(object obj) { return obj is UByte other && Equals(other); }
        public override int GetHashCode() { return (int)_v & 0xFF; }
        public int CompareTo(UByte other) { return UnsignedBits.Cmp8(_v, other._v); }
        public int CompareTo(object obj)
        {
            if (obj == null) return 1;
            if (obj is UByte other) return CompareTo(other);
            throw new System.ArgumentException("Object must be of type dotnet.System.UByte.");
        }
        public static bool operator ==(UByte a, UByte b) { return a._v == b._v; }
        public static bool operator !=(UByte a, UByte b) { return a._v != b._v; }
        public static bool operator <(UByte a, UByte b) { return UnsignedBits.Cmp8(a._v, b._v) < 0; }
        public static bool operator <=(UByte a, UByte b) { return UnsignedBits.Cmp8(a._v, b._v) <= 0; }
        public static bool operator >(UByte a, UByte b) { return UnsignedBits.Cmp8(a._v, b._v) > 0; }
        public static bool operator >=(UByte a, UByte b) { return UnsignedBits.Cmp8(a._v, b._v) >= 0; }

        // ---- conversions: signed primitives ----
        public static explicit operator UByte(sbyte v) { return new UByte(v); }
        public static explicit operator UByte(short v) { return new UByte(v); }
        public static explicit operator UByte(int v) { return new UByte(v); }
        public static explicit operator UByte(long v) { return new UByte(v); }
        public static explicit operator sbyte(UByte v) { return v._v; }
        public static explicit operator short(UByte v) { return (short)((int)v._v & 0xFF); }
        public static explicit operator int(UByte v) { return (int)v._v & 0xFF; }
        public static explicit operator long(UByte v) { return (long)((int)v._v & 0xFF); }

        // ---- conversions: cross unsigned structs (narrowing defined here) ----
        public static explicit operator UByte(UShort v) { return FromBits(UnsignedBits.Narrow16To8(v.Bits)); }
        public static explicit operator UByte(UInt v) { return FromBits(UnsignedBits.Narrow32To8(v.Bits)); }
        public static explicit operator UByte(ULong v) { return FromBits(UnsignedBits.Narrow64To8(v.Bits)); }

        // ---- conversions: floating / decimal ----
        public static explicit operator UByte(double v) { return FromBits(UnsignedBits.FromDouble8(v)); }
        public static explicit operator UByte(float v) { return FromBits(UnsignedBits.FromSingle8(v)); }
        public static explicit operator UByte(decimal v) { return FromBits(UnsignedBits.FromDecimal8(v)); }
        public static explicit operator double(UByte v) { return v.ToDouble(); }
        public static explicit operator float(UByte v) { return v.ToSingle(); }
        public static explicit operator decimal(UByte v) { return v.ToDecimal(); }
        public double ToDouble() { return UnsignedBits.ToDouble8(_v); }
        public float ToSingle() { return UnsignedBits.ToSingle8(_v); }
        public decimal ToDecimal() { return UnsignedBits.ToDecimal8(_v); }

        // ---- parse / format ----
        public static UByte Parse(string s)
        {
            return Parse(s, System.Globalization.NumberStyles.Integer, null);
        }
        public static UByte Parse(string s, System.Globalization.NumberStyles style)
        {
            return Parse(s, style, null);
        }
        public static UByte Parse(string s, System.IFormatProvider provider)
        {
            return Parse(s, System.Globalization.NumberStyles.Integer, provider);
        }
        public static UByte Parse(string s, System.Globalization.NumberStyles style, System.IFormatProvider provider)
        {
            decimal d = UnsignedBits.ParseUnsigned(s, style, provider, 255m);
            return new UByte((int)d);
        }
        public static bool TryParse(string s, out UByte result)
        {
            return TryParse(s, System.Globalization.NumberStyles.Integer, null, out result);
        }
        public static bool TryParse(string s, System.Globalization.NumberStyles style,
            System.IFormatProvider provider, out UByte result)
        {
            bool ok = UnsignedBits.TryParseUnsigned(s, style, provider, 255m, out decimal d);
            result = ok ? new UByte((int)d) : default;
            return ok;
        }
        public override string ToString() { return UnsignedBits.Plain8(_v); }
        public string ToString(string format) { return ToString(format, null); }
        public string ToString(string format, System.IFormatProvider provider)
        {
            if (format == null || format.Length == 0) return UnsignedBits.Plain8(_v);
            char spec = format[0];
            if ((spec == 'X' || spec == 'x') && TailDigits(format))
            {
                return UnsignedBits.FormatHex((long)((int)_v & 0xFF), format);
            }
            return ((int)_v & 0xFF).ToString(format, provider ?? System.Globalization.CultureInfo.InvariantCulture);
        }
        string System.IFormattable.ToString(string format, System.IFormatProvider formatProvider)
        {
            return ToString(format, formatProvider);
        }
        private static bool TailDigits(string format)
        {
            for (int i = 1; i < format.Length; i++)
            {
                if (format[i] < '0' || format[i] > '9') return false;
            }
            return true;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**

`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~Cs2jUnsigned.UByteParityTests" -v q`
Expected: PASS 4 个测试。

- [ ] **Step 5: Commit**

`git add src/Cs2jUnsigned/UByte.cs tests/CSharpToJava.Tests/Cs2jUnsigned/UByteParityTests.cs`
`git commit -m "feat(unsigned): add dotnet.System.UByte readonly struct"`

---

## Task 4: UShort

**Files:**
- Create: `src/Cs2jUnsigned/UShort.cs`
- Test: `tests/CSharpToJava.Tests/Cs2jUnsigned/UShortParityTests.cs`

- [ ] **Step 1: 写失败测试**（完整内容）

```csharp
using dotnet.System;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class UShortParityTests
{
    [Fact]
    public void ConstantsAndStorage()
    {
        Assert.Equal(0, (int)UShort.MinValue);
        Assert.Equal(65535, (int)UShort.MaxValue);
        Assert.Equal(unchecked((short)-1), new UShort(65535).Bits);
        Assert.Equal(0, (int)new UShort(65536));
    }

    [Fact]
    public void ArithmeticCompare_ParityWithNativeUInt16()
    {
        var rnd = new System.Random(7411);
        for (int i = 0; i < 3000; i++)
        {
            int x = rnd.Next();
            int y = rnd.Next();
            int n = rnd.Next(-70, 80);
            ushort a = (ushort)x;
            ushort b = (ushort)y;
            UShort ua = new UShort(x);
            UShort ub = new UShort(y);
            Assert.Equal((int)unchecked((ushort)(a + b)), (int)(ua + ub));
            Assert.Equal((int)unchecked((ushort)(a - b)), (int)(ua - ub));
            Assert.Equal((int)unchecked((ushort)(a * b)), (int)(ua * b));
            if (b != 0)
            {
                Assert.Equal((int)(a / b), (int)(ua / ub));
                Assert.Equal((int)(a % b), (int)(ua % ub));
            }
            Assert.Equal((int)(a & b), (int)(ua & ub));
            Assert.Equal((int)(a | b), (int)(ua | ub));
            Assert.Equal((int)(a ^ b), (int)(ua ^ ub));
            // ushort 的 ~ 会提升为 int，oracle 侧必须显式截回 ushort 再比较。
            Assert.Equal((int)unchecked((ushort)~a), (int)~ua);
            Assert.Equal((int)unchecked((ushort)(a << n)), (int)(ua << n));
            Assert.Equal((int)(a >> n), (int)(ua >> n));
            Assert.Equal((int)(a >>> n), (int)(ua >>> n));
            Assert.Equal(a == b, ua == ub);
            Assert.Equal(a < b, ua < ub);
            Assert.Equal(a.GetHashCode(), ua.GetHashCode());
            Assert.Equal(a.CompareTo(b), ua.CompareTo(ub));
        }
        Assert.Throws<System.DivideByZeroException>(() => new UShort(1) / new UShort(0));
    }

    [Fact]
    public void SignedAndCrossConversions_Parity()
    {
        var rnd = new System.Random(7412);
        for (int i = 0; i < 3000; i++)
        {
            int x = rnd.Next();
            ushort a = (ushort)x;
            UShort ua = new UShort(x);
            Assert.Equal((sbyte)a, (sbyte)ua);
            Assert.Equal((short)a, (short)ua);
            Assert.Equal((int)a, (int)ua);
            Assert.Equal((long)a, (long)ua);
            Assert.Equal((int)(ushort)(sbyte)x, (int)(UShort)(sbyte)x);
            Assert.Equal((int)(ushort)(short)x, (int)(UShort)(short)x);
            Assert.Equal((int)(ushort)x, (int)(UShort)x);
            Assert.Equal((int)(ushort)(long)(x - 90000), (int)(UShort)(long)(x - 90000));
            Assert.Equal((int)(byte)a, (int)(UByte)ua);
            Assert.Equal((long)(uint)a, (long)(UInt)ua);
            Assert.Equal((long)(ulong)a, (long)(ULong)ua);
        }
        Assert.Equal(0, (int)(UShort)new UByte(0));
        Assert.Equal(255, (int)(UShort)new UByte(255));
        Assert.Equal(0, (int)(UShort)new UInt(65536));
    }

    [Fact]
    public void FloatAndDecimalConversions()
    {
        Assert.Equal((int)(ushort)60000.9, (int)(UShort)60000.9);
        Assert.Equal(0, (int)(UShort)double.NaN);
        Assert.Equal(65535, (int)(UShort)double.PositiveInfinity);
        Assert.Equal((double)(ushort)60000, new UShort(60000).ToDouble());
        Assert.Equal((decimal)(ushort)60000, new UShort(60000).ToDecimal());
        Assert.Equal((int)(ushort)5m, (int)(UShort)5m);
        Assert.Throws<System.OverflowException>(() => (UShort)65536m);
        Assert.Throws<System.OverflowException>(() => (UShort)1.5m);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**：同上 filter `UShortParityTests`，预期编译失败。

- [ ] **Step 3: 实现 `src/Cs2jUnsigned/UShort.cs`（完整内容）**

```csharp
namespace dotnet.System
{
    public readonly struct UShort
        : System.IEquatable<UShort>, System.IComparable<UShort>, System.IComparable, System.IFormattable
    {
        private readonly short _v;

        public static readonly UShort MinValue = new UShort((short)0);
        public static readonly UShort MaxValue = new UShort(unchecked((short)-1));

        public UShort(short value) { unchecked { _v = (short)((int)value & 0xFFFF); } }
        public UShort(int value) { unchecked { _v = (short)(value & 0xFFFF); } }
        public UShort(long value) { unchecked { _v = (short)(value & 0xFFFFL); } }

        internal static UShort FromBits(short bits) { return new UShort(bits); }
        internal short Bits { get { return _v; } }

        public static UShort operator +(UShort a, UShort b) { return FromBits(UnsignedBits.Add16(a._v, b._v)); }
        public static UShort operator -(UShort a, UShort b) { return FromBits(UnsignedBits.Sub16(a._v, b._v)); }
        public static UShort operator *(UShort a, UShort b) { return FromBits(UnsignedBits.Mul16(a._v, b._v)); }
        public static UShort operator /(UShort a, UShort b) { return FromBits(UnsignedBits.Div16(a._v, b._v)); }
        public static UShort operator %(UShort a, UShort b) { return FromBits(UnsignedBits.Rem16(a._v, b._v)); }
        public static UShort operator &(UShort a, UShort b) { return FromBits(UnsignedBits.And16(a._v, b._v)); }
        public static UShort operator |(UShort a, UShort b) { return FromBits(UnsignedBits.Or16(a._v, b._v)); }
        public static UShort operator ^(UShort a, UShort b) { return FromBits(UnsignedBits.Xor16(a._v, b._v)); }
        public static UShort operator ~(UShort a) { return FromBits(UnsignedBits.Not16(a._v)); }
        public static UShort operator <<(UShort a, int n) { return FromBits(UnsignedBits.Shl16(a._v, n)); }
        public static UShort operator >>(UShort a, int n) { return FromBits(UnsignedBits.Shr16(a._v, n)); }
        public static UShort operator >>>(UShort a, int n) { return FromBits(UnsignedBits.Shr16(a._v, n)); }

        public bool Equals(UShort other) { return _v == other._v; }
        public override bool Equals(object obj) { return obj is UShort other && Equals(other); }
        public override int GetHashCode() { return (int)_v & 0xFFFF; }
        public int CompareTo(UShort other) { return UnsignedBits.Cmp16(_v, other._v); }
        public int CompareTo(object obj)
        {
            if (obj == null) return 1;
            if (obj is UShort other) return CompareTo(other);
            throw new System.ArgumentException("Object must be of type dotnet.System.UShort.");
        }
        public static bool operator ==(UShort a, UShort b) { return a._v == b._v; }
        public static bool operator !=(UShort a, UShort b) { return a._v != b._v; }
        public static bool operator <(UShort a, UShort b) { return UnsignedBits.Cmp16(a._v, b._v) < 0; }
        public static bool operator <=(UShort a, UShort b) { return UnsignedBits.Cmp16(a._v, b._v) <= 0; }
        public static bool operator >(UShort a, UShort b) { return UnsignedBits.Cmp16(a._v, b._v) > 0; }
        public static bool operator >=(UShort a, UShort b) { return UnsignedBits.Cmp16(a._v, b._v) >= 0; }

        public static explicit operator UShort(sbyte v) { return new UShort((short)v); }
        public static explicit operator UShort(short v) { return new UShort(v); }
        public static explicit operator UShort(int v) { return new UShort(v); }
        public static explicit operator UShort(long v) { return new UShort(v); }
        public static explicit operator sbyte(UShort v) { return (sbyte)v._v; }
        public static explicit operator short(UShort v) { return v._v; }
        public static explicit operator int(UShort v) { return (int)v._v & 0xFFFF; }
        public static explicit operator long(UShort v) { return (long)((int)v._v & 0xFFFF); }

        public static implicit operator UShort(UByte v) { return FromBits(UnsignedBits.Extend8To16(v.Bits)); }
        public static explicit operator UShort(UInt v) { return FromBits(UnsignedBits.Narrow32To16(v.Bits)); }
        public static explicit operator UShort(ULong v) { return FromBits(UnsignedBits.Narrow64To16(v.Bits)); }

        public static explicit operator UShort(double v) { return FromBits(UnsignedBits.FromDouble16(v)); }
        public static explicit operator UShort(float v) { return FromBits(UnsignedBits.FromSingle16(v)); }
        public static explicit operator UShort(decimal v) { return FromBits(UnsignedBits.FromDecimal16(v)); }
        public static explicit operator double(UShort v) { return v.ToDouble(); }
        public static explicit operator float(UShort v) { return v.ToSingle(); }
        public static explicit operator decimal(UShort v) { return v.ToDecimal(); }
        public double ToDouble() { return UnsignedBits.ToDouble16(_v); }
        public float ToSingle() { return UnsignedBits.ToSingle16(_v); }
        public decimal ToDecimal() { return UnsignedBits.ToDecimal16(_v); }

        public static UShort Parse(string s)
        {
            return Parse(s, System.Globalization.NumberStyles.Integer, null);
        }
        public static UShort Parse(string s, System.Globalization.NumberStyles style)
        {
            return Parse(s, style, null);
        }
        public static UShort Parse(string s, System.IFormatProvider provider)
        {
            return Parse(s, System.Globalization.NumberStyles.Integer, provider);
        }
        public static UShort Parse(string s, System.Globalization.NumberStyles style, System.IFormatProvider provider)
        {
            decimal d = UnsignedBits.ParseUnsigned(s, style, provider, 65535m);
            return new UShort((int)d);
        }
        public static bool TryParse(string s, out UShort result)
        {
            return TryParse(s, System.Globalization.NumberStyles.Integer, null, out result);
        }
        public static bool TryParse(string s, System.Globalization.NumberStyles style,
            System.IFormatProvider provider, out UShort result)
        {
            bool ok = UnsignedBits.TryParseUnsigned(s, style, provider, 65535m, out decimal d);
            result = ok ? new UShort((int)d) : default;
            return ok;
        }
        public override string ToString() { return UnsignedBits.Plain16(_v); }
        public string ToString(string format) { return ToString(format, null); }
        public string ToString(string format, System.IFormatProvider provider)
        {
            if (format == null || format.Length == 0) return UnsignedBits.Plain16(_v);
            char spec = format[0];
            if ((spec == 'X' || spec == 'x') && TailDigits(format))
            {
                return UnsignedBits.FormatHex((long)((int)_v & 0xFFFF), format);
            }
            return ((int)_v & 0xFFFF).ToString(format, provider ?? System.Globalization.CultureInfo.InvariantCulture);
        }
        string System.IFormattable.ToString(string format, System.IFormatProvider formatProvider)
        {
            return ToString(format, formatProvider);
        }
        private static bool TailDigits(string format)
        {
            for (int i = 1; i < format.Length; i++)
            {
                if (format[i] < '0' || format[i] > '9') return false;
            }
            return true;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**：`--filter "FullyQualifiedName~Cs2jUnsigned.UShortParityTests"`，预期 PASS 4 个。

- [ ] **Step 5: Commit**

`git add src/Cs2jUnsigned/UShort.cs tests/CSharpToJava.Tests/Cs2jUnsigned/UShortParityTests.cs`
`git commit -m "feat(unsigned): add dotnet.System.UShort readonly struct"`

---

## Task 5: UInt

**Files:**
- Create: `src/Cs2jUnsigned/UInt.cs`
- Test: `tests/CSharpToJava.Tests/Cs2jUnsigned/UIntParityTests.cs`

- [ ] **Step 1: 写失败测试**（完整内容）

```csharp
using dotnet.System;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class UIntParityTests
{
    [Fact]
    public void ConstantsAndStorage()
    {
        Assert.Equal(0L, (long)UInt.MinValue);
        Assert.Equal(4294967295L, (long)UInt.MaxValue);
        Assert.Equal(-1, new UInt(4294967295L).Bits);
        Assert.Equal(0, new UInt(4294967296L).Bits);
    }

    [Fact]
    public void ArithmeticCompare_ParityWithNativeUInt32()
    {
        var rnd = new System.Random(5311);
        for (int i = 0; i < 4000; i++)
        {
            int x = rnd.Next();
            int y = rnd.Next();
            int n = rnd.Next(-70, 80);
            uint a = (uint)x;
            uint b = (uint)y;
            UInt ua = new UInt(x);
            UInt ub = new UInt(y);
            Assert.Equal((long)unchecked(a + b), (long)(ua + ub));
            Assert.Equal((long)unchecked(a - b), (long)(ua - ub));
            Assert.Equal((long)unchecked(a * b), (long)(ua * b));
            if (b != 0)
            {
                Assert.Equal((long)(a / b), (long)(ua / ub));
                Assert.Equal((long)(a % b), (long)(ua % ub));
            }
            Assert.Equal((long)(a & b), (long)(ua & ub));
            Assert.Equal((long)(a | b), (long)(ua | ub));
            Assert.Equal((long)(a ^ b), (long)(ua ^ ub));
            Assert.Equal((long)~a, (long)~ua);
            Assert.Equal((long)(a << n), (long)(ua << n));
            Assert.Equal((long)(a >> n), (long)(ua >> n));
            Assert.Equal((long)(a >>> n), (long)(ua >>> n));
            Assert.Equal(a == b, ua == ub);
            Assert.Equal(a < b, ua < ub);
            Assert.Equal(a.GetHashCode(), ua.GetHashCode());
            Assert.Equal(a.CompareTo(b), ua.CompareTo(ub));
        }
        Assert.Throws<System.DivideByZeroException>(() => new UInt(1) / new UInt(0));
        Assert.Equal(2147483648L, (long)(new UInt(unchecked((int)0x80000000)) / new UInt(1)));
    }

    [Fact]
    public void SignedAndCrossConversions_Parity()
    {
        var rnd = new System.Random(5312);
        for (int i = 0; i < 4000; i++)
        {
            int x = rnd.Next();
            uint a = (uint)x;
            UInt ua = new UInt(x);
            Assert.Equal((sbyte)a, (sbyte)ua);
            Assert.Equal((short)a, (short)ua);
            Assert.Equal((int)a, (int)ua);
            Assert.Equal((long)a, (long)ua);
            Assert.Equal((uint)(sbyte)x, (uint)(UInt)(sbyte)x);
            Assert.Equal((uint)(short)x, (uint)(UInt)(short)x);
            Assert.Equal((uint)x, (uint)(UInt)x);
            long lv = (long)x - 3000000000L;
            Assert.Equal((uint)lv, (uint)(UInt)lv);
            Assert.Equal((uint)(byte)x, (uint)(UInt)new UByte(x));
            Assert.Equal((uint)(ushort)x, (uint)(UInt)new UShort(x));
            Assert.Equal((int)(byte)a, (int)(UByte)ua);
            Assert.Equal((int)(ushort)a, (int)(UShort)ua);
            Assert.Equal((ulong)a, (ulong)(ULong)ua);
            Assert.Equal((uint)(ulong)(a + 4294967296UL), (uint)(UInt)new ULong((long)(a + 4294967296UL)));
        }
    }

    [Fact]
    public void FloatAndDecimalConversions()
    {
        Assert.Equal((long)(uint)3000000000.9, (long)(UInt)3000000000.9);
        Assert.Equal(0L, (long)(UInt)double.NaN);
        Assert.Equal(4294967295L, (long)(UInt)double.PositiveInfinity);
        Assert.Equal((double)(uint)3000000000, new UInt(3000000000).ToDouble());
        Assert.Equal((float)(uint)3000000000, new UInt(3000000000).ToSingle());
        Assert.Equal((decimal)(uint)3000000000, new UInt(3000000000).ToDecimal());
        Assert.Equal((long)(uint)5m, (long)(UInt)5m);
        Assert.Throws<System.OverflowException>(() => (UInt)4294967296m);
        Assert.Throws<System.OverflowException>(() => (UInt)1.5m);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**：filter `UIntParityTests`，预期编译失败。

- [ ] **Step 3: 实现 `src/Cs2jUnsigned/UInt.cs`（完整内容）**

```csharp
namespace dotnet.System
{
    public readonly struct UInt
        : System.IEquatable<UInt>, System.IComparable<UInt>, System.IComparable, System.IFormattable
    {
        private readonly int _v;

        public static readonly UInt MinValue = new UInt(0);
        public static readonly UInt MaxValue = new UInt(-1);

        public UInt(int value) { _v = value; }
        public UInt(long value) { unchecked { _v = (int)value; } }

        internal static UInt FromBits(int bits) { return new UInt(bits); }
        internal int Bits { get { return _v; } }

        public static UInt operator +(UInt a, UInt b) { return FromBits(UnsignedBits.Add32(a._v, b._v)); }
        public static UInt operator -(UInt a, UInt b) { return FromBits(UnsignedBits.Sub32(a._v, b._v)); }
        public static UInt operator *(UInt a, UInt b) { return FromBits(UnsignedBits.Mul32(a._v, b._v)); }
        public static UInt operator /(UInt a, UInt b) { return FromBits(UnsignedBits.Divide32(a._v, b._v)); }
        public static UInt operator %(UInt a, UInt b) { return FromBits(UnsignedBits.Remainder32(a._v, b._v)); }
        public static UInt operator &(UInt a, UInt b) { return FromBits(UnsignedBits.And32(a._v, b._v)); }
        public static UInt operator |(UInt a, UInt b) { return FromBits(UnsignedBits.Or32(a._v, b._v)); }
        public static UInt operator ^(UInt a, UInt b) { return FromBits(UnsignedBits.Xor32(a._v, b._v)); }
        public static UInt operator ~(UInt a) { return FromBits(UnsignedBits.Not32(a._v)); }
        public static UInt operator <<(UInt a, int n) { return FromBits(UnsignedBits.Shl32(a._v, n)); }
        public static UInt operator >>(UInt a, int n) { return FromBits(UnsignedBits.Shr32(a._v, n)); }
        public static UInt operator >>>(UInt a, int n) { return FromBits(UnsignedBits.Shr32(a._v, n)); }

        public bool Equals(UInt other) { return _v == other._v; }
        public override bool Equals(object obj) { return obj is UInt other && Equals(other); }
        public override int GetHashCode() { return _v; }
        public int CompareTo(UInt other) { return UnsignedBits.Cmp32(_v, other._v); }
        public int CompareTo(object obj)
        {
            if (obj == null) return 1;
            if (obj is UInt other) return CompareTo(other);
            throw new System.ArgumentException("Object must be of type dotnet.System.UInt.");
        }
        public static bool operator ==(UInt a, UInt b) { return a._v == b._v; }
        public static bool operator !=(UInt a, UInt b) { return a._v != b._v; }
        public static bool operator <(UInt a, UInt b) { return UnsignedBits.Cmp32(a._v, b._v) < 0; }
        public static bool operator <=(UInt a, UInt b) { return UnsignedBits.Cmp32(a._v, b._v) <= 0; }
        public static bool operator >(UInt a, UInt b) { return UnsignedBits.Cmp32(a._v, b._v) > 0; }
        public static bool operator >=(UInt a, UInt b) { return UnsignedBits.Cmp32(a._v, b._v) >= 0; }

        public static explicit operator UInt(sbyte v) { return new UInt((int)v); }
        public static explicit operator UInt(short v) { return new UInt((int)v); }
        public static explicit operator UInt(int v) { return new UInt(v); }
        public static explicit operator UInt(long v) { return new UInt(v); }
        public static explicit operator sbyte(UInt v) { return (sbyte)v._v; }
        public static explicit operator short(UInt v) { return (short)v._v; }
        public static explicit operator int(UInt v) { return v._v; }
        public static explicit operator long(UInt v) { return (long)v._v & 0xFFFFFFFFL; }

        public static implicit operator UInt(UByte v) { return FromBits(UnsignedBits.Extend8To32(v.Bits)); }
        public static implicit operator UInt(UShort v) { return FromBits(UnsignedBits.Extend16To32(v.Bits)); }
        public static explicit operator UInt(ULong v) { return FromBits(UnsignedBits.Narrow64To32(v.Bits)); }

        public static explicit operator UInt(double v) { return FromBits(UnsignedBits.FromDouble32(v)); }
        public static explicit operator UInt(float v) { return FromBits(UnsignedBits.FromSingle32(v)); }
        public static explicit operator UInt(decimal v) { return FromBits(UnsignedBits.FromDecimal32(v)); }
        public static explicit operator double(UInt v) { return v.ToDouble(); }
        public static explicit operator float(UInt v) { return v.ToSingle(); }
        public static explicit operator decimal(UInt v) { return v.ToDecimal(); }
        public double ToDouble() { return UnsignedBits.ToDouble32(_v); }
        public float ToSingle() { return UnsignedBits.ToSingle32(_v); }
        public decimal ToDecimal() { return UnsignedBits.ToDecimal32(_v); }

        public static UInt Parse(string s)
        {
            return Parse(s, System.Globalization.NumberStyles.Integer, null);
        }
        public static UInt Parse(string s, System.Globalization.NumberStyles style)
        {
            return Parse(s, style, null);
        }
        public static UInt Parse(string s, System.IFormatProvider provider)
        {
            return Parse(s, System.Globalization.NumberStyles.Integer, provider);
        }
        public static UInt Parse(string s, System.Globalization.NumberStyles style, System.IFormatProvider provider)
        {
            decimal d = UnsignedBits.ParseUnsigned(s, style, provider, 4294967295m);
            return new UInt((long)d);
        }
        public static bool TryParse(string s, out UInt result)
        {
            return TryParse(s, System.Globalization.NumberStyles.Integer, null, out result);
        }
        public static bool TryParse(string s, System.Globalization.NumberStyles style,
            System.IFormatProvider provider, out UInt result)
        {
            bool ok = UnsignedBits.TryParseUnsigned(s, style, provider, 4294967295m, out decimal d);
            result = ok ? new UInt((long)d) : default;
            return ok;
        }
        public override string ToString() { return UnsignedBits.Plain32(_v); }
        public string ToString(string format) { return ToString(format, null); }
        public string ToString(string format, System.IFormatProvider provider)
        {
            if (format == null || format.Length == 0) return UnsignedBits.Plain32(_v);
            char spec = format[0];
            if ((spec == 'X' || spec == 'x') && TailDigits(format))
            {
                return UnsignedBits.FormatHex((long)_v & 0xFFFFFFFFL, format);
            }
            return ((long)_v & 0xFFFFFFFFL).ToString(format, provider ?? System.Globalization.CultureInfo.InvariantCulture);
        }
        string System.IFormattable.ToString(string format, System.IFormatProvider formatProvider)
        {
            return ToString(format, formatProvider);
        }
        private static bool TailDigits(string format)
        {
            for (int i = 1; i < format.Length; i++)
            {
                if (format[i] < '0' || format[i] > '9') return false;
            }
            return true;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**：filter `UIntParityTests`，预期 PASS 4 个。

- [ ] **Step 5: Commit**

`git add src/Cs2jUnsigned/UInt.cs tests/CSharpToJava.Tests/Cs2jUnsigned/UIntParityTests.cs`
`git commit -m "feat(unsigned): add dotnet.System.UInt readonly struct"`

---

## Task 6: ULong

**Files:**
- Create: `src/Cs2jUnsigned/ULong.cs`
- Test: `tests/CSharpToJava.Tests/Cs2jUnsigned/ULongParityTests.cs`

- [ ] **Step 1: 写失败测试**（完整内容）

```csharp
using dotnet.System;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class ULongParityTests
{
    [Fact]
    public void ConstantsAndStorage()
    {
        Assert.Equal(0L, (long)ULong.MinValue);
        Assert.Equal(-1L, (long)ULong.MaxValue);
        Assert.Equal(18446744073709551615UL, (ulong)(long)ULong.MaxValue);
    }

    [Fact]
    public void ArithmeticCompare_ParityWithNativeUInt64()
    {
        var rnd = new System.Random(6311);
        for (int i = 0; i < 5000; i++)
        {
            // 用两个随机 int 拼成完整 64 位位模式（含高位置 1），不能只取非负高半。
            long x = unchecked((long)(((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next()));
            long y = unchecked((long)(((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next()));
            int n = rnd.Next(-130, 140);
            ulong a = (ulong)x;
            ulong b = (ulong)y;
            ULong ua = new ULong(x);
            ULong ub = new ULong(y);
            Assert.Equal(unchecked(a + b), (ulong)(long)(ua + ub));
            Assert.Equal(unchecked(a - b), (ulong)(long)(ua - ub));
            Assert.Equal(unchecked(a * b), (ulong)(long)(ua * ub));
            Assert.Equal(a / b, (ulong)(long)(ua / ub));
            Assert.Equal(a % b, (ulong)(long)(ua % ub));
            Assert.Equal(a & b, (ulong)(long)(ua & ub));
            Assert.Equal(a | b, (ulong)(long)(ua | ub));
            Assert.Equal(a ^ b, (ulong)(long)(ua ^ ub));
            Assert.Equal(~a, (ulong)(long)~ua);
            Assert.Equal(a << n, (ulong)(long)(ua << n));
            Assert.Equal(a >> n, (ulong)(long)(ua >> n));
            Assert.Equal(a >>> n, (ulong)(long)(ua >>> n));
            Assert.Equal(a == b, ua == ub);
            Assert.Equal(a < b, ua < ub);
            Assert.Equal(a.GetHashCode(), ua.GetHashCode());
            Assert.Equal(a.CompareTo(b), ua.CompareTo(ub));
        }
        Assert.Throws<System.DivideByZeroException>(() => new ULong(1) / new ULong(0));
        Assert.Equal(ulong.MaxValue, (ulong)(long)(new ULong(-1L) / new ULong(-1L)));
        Assert.Equal(0UL, (ulong)(long)(new ULong(-2L) / new ULong(-1L)));
        Assert.Equal(9223372036854775807UL, (ulong)(long)(new ULong(-1L) / new ULong(2L)));
    }

    [Fact]
    public void SignedAndCrossConversions_Parity()
    {
        var rnd = new System.Random(6312);
        for (int i = 0; i < 5000; i++)
        {
            long x = unchecked((long)(((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next()));
            ulong a = (ulong)x;
            ULong ua = new ULong(x);
            Assert.Equal((sbyte)a, (sbyte)ua);
            Assert.Equal((short)a, (short)ua);
            Assert.Equal((int)a, (int)ua);
            Assert.Equal((long)a, (long)ua);
            Assert.Equal((ulong)(sbyte)x, (ulong)(ULong)(sbyte)x);
            Assert.Equal((ulong)(short)x, (ulong)(ULong)(short)x);
            Assert.Equal((ulong)(int)x, (ulong)(ULong)(int)x);
            Assert.Equal((ulong)x, (ulong)(ULong)x);
            Assert.Equal((ulong)(byte)x, (ulong)(ULong)new UByte(x));
            Assert.Equal((ulong)(ushort)x, (ulong)(ULong)new UShort(x));
            Assert.Equal((ulong)(uint)x, (ulong)(ULong)new UInt(x));
            Assert.Equal((uint)a, (uint)(UInt)ua);
        }
    }

    [Fact]
    public void FloatDecimalAndBoundaryConversions()
    {
        Assert.Equal(18446744073709551615.0.ToString("R"), new ULong(-1L).ToDouble().ToString("R"));
        Assert.Equal(9223372036854775809.0.ToString("R"), new ULong(unchecked((long)0x8000000000000001)).ToDouble().ToString("R"));
        Assert.Equal(18446744073709551615m, new ULong(-1L).ToDecimal());
        Assert.Equal(9223372036854775808m, new ULong(unchecked((long)0x8000000000000000)).ToDecimal());
        Assert.Equal(0L, (long)(ULong)double.NaN);
        Assert.Equal(-1L, (long)(ULong)double.PositiveInfinity);
        Assert.Equal(unchecked((long)0x8000000000000000), (long)(ULong)9223372036854775808.0);
        Assert.Equal((ulong)5m, (ulong)(ULong)5m);
        Assert.Throws<System.OverflowException>(() => (ULong)(-1m));
        Assert.Throws<System.OverflowException>(() => (ULong)18446744073709551616m);
        Assert.Throws<System.OverflowException>(() => (ULong)1.5m);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**：filter `ULongParityTests`，预期编译失败。

- [ ] **Step 3: 实现 `src/Cs2jUnsigned/ULong.cs`（完整内容）**

```csharp
namespace dotnet.System
{
    public readonly struct ULong
        : System.IEquatable<ULong>, System.IComparable<ULong>, System.IComparable, System.IFormattable
    {
        private readonly long _v;

        public static readonly ULong MinValue = new ULong(0L);
        public static readonly ULong MaxValue = new ULong(-1L);

        public ULong(long value) { _v = value; }

        internal static ULong FromBits(long bits) { return new ULong(bits); }
        internal long Bits { get { return _v; } }

        public static ULong operator +(ULong a, ULong b) { return FromBits(UnsignedBits.Add64(a._v, b._v)); }
        public static ULong operator -(ULong a, ULong b) { return FromBits(UnsignedBits.Sub64(a._v, b._v)); }
        public static ULong operator *(ULong a, ULong b) { return FromBits(UnsignedBits.Mul64(a._v, b._v)); }
        public static ULong operator /(ULong a, ULong b) { return FromBits(UnsignedBits.Divide64(a._v, b._v)); }
        public static ULong operator %(ULong a, ULong b) { return FromBits(UnsignedBits.Remainder64(a._v, b._v)); }
        public static ULong operator &(ULong a, ULong b) { return FromBits(UnsignedBits.And64(a._v, b._v)); }
        public static ULong operator |(ULong a, ULong b) { return FromBits(UnsignedBits.Or64(a._v, b._v)); }
        public static ULong operator ^(ULong a, ULong b) { return FromBits(UnsignedBits.Xor64(a._v, b._v)); }
        public static ULong operator ~(ULong a) { return FromBits(UnsignedBits.Not64(a._v)); }
        public static ULong operator <<(ULong a, int n) { return FromBits(UnsignedBits.Shl64(a._v, n)); }
        public static ULong operator >>(ULong a, int n) { return FromBits(UnsignedBits.Shr64(a._v, n)); }
        public static ULong operator >>>(ULong a, int n) { return FromBits(UnsignedBits.Shr64(a._v, n)); }

        public bool Equals(ULong other) { return _v == other._v; }
        public override bool Equals(object obj) { return obj is ULong other && Equals(other); }
        public override int GetHashCode() { return unchecked((int)_v ^ (int)(_v >>> 32)); }
        public int CompareTo(ULong other) { return UnsignedBits.Cmp64(_v, other._v); }
        public int CompareTo(object obj)
        {
            if (obj == null) return 1;
            if (obj is ULong other) return CompareTo(other);
            throw new System.ArgumentException("Object must be of type dotnet.System.ULong.");
        }
        public static bool operator ==(ULong a, ULong b) { return a._v == b._v; }
        public static bool operator !=(ULong a, ULong b) { return a._v != b._v; }
        public static bool operator <(ULong a, ULong b) { return UnsignedBits.Cmp64(a._v, b._v) < 0; }
        public static bool operator <=(ULong a, ULong b) { return UnsignedBits.Cmp64(a._v, b._v) <= 0; }
        public static bool operator >(ULong a, ULong b) { return UnsignedBits.Cmp64(a._v, b._v) > 0; }
        public static bool operator >=(ULong a, ULong b) { return UnsignedBits.Cmp64(a._v, b._v) >= 0; }

        public static explicit operator ULong(sbyte v) { return new ULong((long)v); }
        public static explicit operator ULong(short v) { return new ULong((long)v); }
        public static explicit operator ULong(int v) { return new ULong((long)v); }
        public static explicit operator ULong(long v) { return new ULong(v); }
        public static explicit operator sbyte(ULong v) { return (sbyte)v._v; }
        public static explicit operator short(ULong v) { return (short)v._v; }
        public static explicit operator int(ULong v) { return (int)v._v; }
        public static explicit operator long(ULong v) { return v._v; }

        public static implicit operator ULong(UByte v) { return FromBits(UnsignedBits.Extend8To64(v.Bits)); }
        public static implicit operator ULong(UShort v) { return FromBits(UnsignedBits.Extend16To64(v.Bits)); }
        public static implicit operator ULong(UInt v) { return FromBits(UnsignedBits.Extend32To64(v.Bits)); }

        public static explicit operator ULong(double v) { return FromBits(UnsignedBits.FromDouble64(v)); }
        public static explicit operator ULong(float v) { return FromBits(UnsignedBits.FromSingle64(v)); }
        public static explicit operator ULong(decimal v) { return FromBits(UnsignedBits.FromDecimal64(v)); }
        public static explicit operator double(ULong v) { return v.ToDouble(); }
        public static explicit operator float(ULong v) { return v.ToSingle(); }
        public static explicit operator decimal(ULong v) { return v.ToDecimal(); }
        public double ToDouble() { return UnsignedBits.ToDouble64(_v); }
        public float ToSingle() { return UnsignedBits.ToSingle64(_v); }
        public decimal ToDecimal() { return UnsignedBits.ToDecimal64(_v); }

        public static ULong Parse(string s)
        {
            return Parse(s, System.Globalization.NumberStyles.Integer, null);
        }
        public static ULong Parse(string s, System.Globalization.NumberStyles style)
        {
            return Parse(s, style, null);
        }
        public static ULong Parse(string s, System.IFormatProvider provider)
        {
            return Parse(s, System.Globalization.NumberStyles.Integer, provider);
        }
        public static ULong Parse(string s, System.Globalization.NumberStyles style, System.IFormatProvider provider)
        {
            decimal d = UnsignedBits.ParseUnsigned(s, style, provider, 18446744073709551615m);
            return new ULong(UnsignedBits.FromDecimal64(d));
        }
        public static bool TryParse(string s, out ULong result)
        {
            return TryParse(s, System.Globalization.NumberStyles.Integer, null, out result);
        }
        public static bool TryParse(string s, System.Globalization.NumberStyles style,
            System.IFormatProvider provider, out ULong result)
        {
            bool ok = UnsignedBits.TryParseUnsigned(s, style, provider, 18446744073709551615m, out decimal d);
            result = ok ? new ULong(UnsignedBits.FromDecimal64(d)) : default;
            return ok;
        }
        public override string ToString() { return UnsignedBits.Plain64(_v); }
        public string ToString(string format) { return ToString(format, null); }
        public string ToString(string format, System.IFormatProvider provider)
        {
            if (format == null || format.Length == 0) return UnsignedBits.Plain64(_v);
            char spec = format[0];
            if ((spec == 'X' || spec == 'x') && TailDigits(format))
            {
                return UnsignedBits.FormatHex(_v, format);
            }
            return UnsignedBits.ToDecimal64(_v)
                .ToString(format, provider ?? System.Globalization.CultureInfo.InvariantCulture);
        }
        string System.IFormattable.ToString(string format, System.IFormatProvider formatProvider)
        {
            return ToString(format, formatProvider);
        }
        private static bool TailDigits(string format)
        {
            for (int i = 1; i < format.Length; i++)
            {
                if (format[i] < '0' || format[i] > '9') return false;
            }
            return true;
        }
    }
}
```

- [ ] **Step 4: 运行测试确认通过**：filter `ULongParityTests`，预期 PASS 4 个。

- [ ] **Step 5: Commit**

`git add src/Cs2jUnsigned/ULong.cs tests/CSharpToJava.Tests/Cs2jUnsigned/ULongParityTests.cs`
`git commit -m "feat(unsigned): add dotnet.System.ULong readonly struct"`

---

## Task 7: Parse/TryParse/ToString 四类型横向对照

**Files:**
- Test: `tests/CSharpToJava.Tests/Cs2jUnsigned/ParseFormatParityTests.cs`

- [ ] **Step 1: 写失败测试**（完整内容）

```csharp
using dotnet.System;
using St = System.Globalization.NumberStyles;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class ParseFormatParityTests
{
    private static readonly System.IFormatProvider Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    // 只放四类型公共合法集；越界串由 OverflowAndMalformed_ThrowSameExceptionTypes 覆盖。
    [Theory]
    [InlineData("0")]
    [InlineData("123")]
    [InlineData("255")]
    [InlineData(" 42 ")]
    public void Parse_Default_MatchesNative(string s)
    {
        Assert.Equal((int)byte.Parse(s), (int)UByte.Parse(s));
        Assert.Equal((int)ushort.Parse(s), (int)UShort.Parse(s));
        Assert.Equal((long)uint.Parse(s), (long)UInt.Parse(s));
        Assert.Equal(ulong.Parse(s), (ulong)(long)ULong.Parse(s));
    }

    [Fact]
    public void TryParse_MatchesNative()
    {
        foreach (string s in new[] { "0", "200", "65535", "4294967295", "18446744073709551615", "x", "" })
        {
            bool nb = byte.TryParse(s, out byte eb);
            bool ab = UByte.TryParse(s, out UByte abv);
            Assert.Equal(nb, ab);
            if (nb) Assert.Equal((int)eb, (int)abv);
            bool nu = ulong.TryParse(s, out ulong eu);
            bool au = ULong.TryParse(s, out ULong auv);
            Assert.Equal(nu, au);
            if (nu) Assert.Equal(eu, (ulong)(long)auv);
        }
        UByte tmp;
        Assert.False(UByte.TryParse("999", out tmp));
        Assert.Equal(default, tmp);
    }

    [Fact]
    public void OverflowAndMalformed_ThrowSameExceptionTypes()
    {
        Assert.Throws<System.OverflowException>(() => UByte.Parse("256"));
        Assert.Throws<System.OverflowException>(() => UShort.Parse("65536"));
        Assert.Throws<System.OverflowException>(() => UInt.Parse("4294967296"));
        Assert.Throws<System.OverflowException>(() => ULong.Parse("18446744073709551616"));
        Assert.Throws<System.FormatException>(() => UInt.Parse(""));
        Assert.Throws<System.FormatException>(() => UInt.Parse("   "));
        Assert.Throws<System.FormatException>(() => UInt.Parse("12a"));
        Assert.Throws<System.FormatException>(() => UInt.Parse("1+0"));
        Assert.Throws<System.FormatException>(() => UInt.Parse("1+"));
        Assert.Throws<System.ArgumentNullException>(() => UInt.Parse(null));
        Assert.Throws<System.OverflowException>(() => byte.Parse("-1", St.Integer, Inv));
        Assert.Throws<System.OverflowException>(() => UByte.Parse("-1", St.Integer, Inv));
        Assert.Throws<System.OverflowException>(() => ULong.Parse("(1)", St.AllowParentheses | St.AllowLeadingSign, Inv));
        Assert.Equal(0, (int)UByte.Parse("(0)", St.AllowParentheses, Inv));
        Assert.Equal(0, (int)UByte.Parse("0-", St.AllowTrailingSign, Inv));
        Assert.Throws<System.FormatException>(() => UInt.Parse("+1-", St.AllowLeadingSign | St.AllowTrailingSign, Inv));
    }

    [Fact]
    public void Styles_HexBinaryExponentGroups_MatchNative()
    {
        Assert.Equal((int)byte.Parse("FF", St.HexNumber, Inv), (int)UByte.Parse("FF", St.HexNumber, Inv));
        Assert.False(UByte.TryParse("0xFF", St.HexNumber, Inv, out _));
        Assert.False(UByte.TryParse("1ff", St.HexNumber, Inv, out _));
        Assert.Equal(10, (int)UByte.Parse("1010", St.AllowBinarySpecifier, Inv));
        Assert.False(UByte.TryParse("0b1010", St.AllowBinarySpecifier, Inv, out _));
        Assert.False(UByte.TryParse("100000000", St.AllowBinarySpecifier, Inv, out _));
        Assert.Equal(100L, (long)UInt.Parse("1e2", St.Integer | St.AllowExponent, Inv));
        Assert.Equal(100L, (long)UInt.Parse("1,00", St.Integer | St.AllowThousands, Inv));
        Assert.Equal(1000L, (long)UInt.Parse("1,,000", St.Integer | St.AllowThousands, Inv));
        Assert.Throws<System.FormatException>(() => UInt.Parse(",100", St.Integer | St.AllowThousands, Inv));
        Assert.Equal(1L, (long)UInt.Parse("1.0", St.Integer | St.AllowDecimalPoint, Inv));
        Assert.Throws<System.OverflowException>(() => UInt.Parse("1.5", St.Integer | St.AllowDecimalPoint, Inv));
        Assert.Throws<System.ArgumentException>(() => UInt.Parse("1", St.HexNumber | St.AllowThousands, Inv));
        Assert.Throws<System.ArgumentException>(() => UByte.Parse("1", St.AllowBinarySpecifier | St.AllowLeadingSign, Inv));
        Assert.Throws<System.ArgumentException>(() => UByte.TryParse("1", St.HexNumber | St.AllowThousands, Inv, out _));
    }

    [Fact]
    public void ToString_Formats_MatchNative()
    {
        byte b = 200; ushort s = 40000; uint i = 3000000000; ulong l = ulong.MaxValue;
        UByte ub = new UByte(200); UShort us = new UShort(40000);
        UInt ui = new UInt(3000000000); ULong ul = new ULong(-1L);
        foreach (string fmt in new[] { null, "", "D", "D3", "X", "X2", "X4", "N0", "G" })
        {
            Assert.Equal(b.ToString(fmt, Inv), ub.ToString(fmt, Inv));
            Assert.Equal(s.ToString(fmt, Inv), us.ToString(fmt, Inv));
            Assert.Equal(i.ToString(fmt, Inv), ui.ToString(fmt, Inv));
            Assert.Equal(l.ToString(fmt, Inv), ul.ToString(fmt, Inv));
        }
        Assert.Equal(1UL.ToString("X20", Inv), new ULong(1L).ToString("X20", Inv));
        Assert.Equal("00000000000000000001", new ULong(1L).ToString("X20", Inv));
        Assert.Equal("ffffffffffffffff", ul.ToString("x"));
        Assert.Equal(18446744073709551615m.ToString("N0", Inv), ul.ToString("N0", Inv));
        Assert.Equal("18446744073709551615", ul.ToString());
    }
}
```

注意：`Parse_Default_MatchesNative` 的 InlineData 已裁剪为四类型公共合法集（`"0"`/`"123"`/`"255"`/`" 42 "`）——若 Theory 数据含 `"65535"` 等短类型越界串，原生侧表达式会直接抛异常使该用例失败（xUnit 不会比较两侧各自抛出的异常）；越界行为由 `OverflowAndMalformed_ThrowSameExceptionTypes` 覆盖。`St` 是文件顶部的 `using St = System.Globalization.NumberStyles;` 别名（枚举类型不能赋给静态字段）。

- [ ] **Step 2: 运行测试确认失败/暴露差异**

`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj --filter "FullyQualifiedName~Cs2jUnsigned.ParseFormatParityTests" -v q`

- [ ] **Step 3: 按失败修正实现**

若有断言失败，只允许修改 `UnsignedBits.cs` 的解析/格式化分支（或四个 struct 的 ToString 分发），使行为与原生一致；不得改测试期望值（除 Step 1 已注明的 Theory 数据裁剪）。典型可能修正点：指数大小写、分隔符多字符匹配、`NumberFormatInfo.GetInstance(null)` 回退。每次修改后重跑同一 filter，直到全绿。

- [ ] **Step 4: 全量回归**

`dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -v q`
Expected: 既有测试无回归 + Cs2jUnsigned 全部通过。

- [ ] **Step 5: Commit**

`git add tests/CSharpToJava.Tests/Cs2jUnsigned/ParseFormatParityTests.cs src/Cs2jUnsigned`
`git commit -m "test(unsigned): add cross-type parse/format parity coverage"`（若仅测试改动，去掉 src 路径）

---

## Task 8: 约束扫描与整体验收

**Files:** 无新增；只读检查。

- [ ] **Step 1: 禁用符号静态扫描**

PowerShell：

```powershell
Select-String -Path src/Cs2jUnsigned/*.cs -Pattern '\b(byte|ushort|uint|ulong)\b','System\.(Byte|UInt16|UInt32|UInt64)\b'
```

Expected: 无任何输出（退出码无匹配）。特别确认注释/字符串中也无命中。

- [ ] **Step 2: 零依赖确认**

`Select-String -Path src/Cs2jUnsigned/Cs2jUnsigned.csproj -Pattern 'ProjectReference|PackageReference|Using Include'`
Expected: 无输出。

- [ ] **Step 3: 确认 Cs2jUnsignedMath 未被改动**

`git status --short src/Cs2jUnsignedMath`
Expected: 无输出。

- [ ] **Step 4: 全解决方案构建**

`dotnet build CSharpToJavaConverter.slnx -v q`
Expected: 0 error。

- [ ] **Step 5: 全量测试**

`dotnet test CSharpToJavaConverter.slnx -v q`（若 slnx 不支持 test，则逐项目跑 tests 下三个测试工程）
Expected: 全绿；既有测试零回归。

- [ ] **Step 6: 最终提交（若有残留改动）**

`git status --short`
仅当存在与本任务相关的未提交修改时：`git add src/Cs2jUnsigned tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj CSharpToJavaConverter.slnx` 然后 `git commit -m "chore(unsigned): finalize Cs2jUnsigned"`。工作区中与本任务无关的既有改动（如 `src/CSharpToJava.Core/Transformers/...`、`.find_lines.ps1`、`.orig_id*.cs`）**不得**纳入提交。

---

## 验收对照（规格 §9）

1. slnx 构建零错误 → Task 8 Step 4。
2. 全量测试通过、无回归 → Task 7 Step 4 + Task 8 Step 5。
3. 禁用符号 grep 零命中 → Task 8 Step 1。
4. csproj 零引用、Cs2jUnsignedMath 无改动 → Task 8 Step 2/3。
5. 四类型均为 public readonly struct、字段 private readonly → 代码审查（Task 3-6 的实现已固定）。
6. net10 oracle 对照全部一致 → Task 2-7 的 parity 测试。

---

## 实施更正记录（Task 3–8 落地时发现，均已验证）

1. **库文件 BCL 引用必须全部 `global::System.` 限定**：在 `namespace dotnet.System` 内裸写 `System.IEquatable<>` 会解析成 `dotnet.System.*` 报 CS0234。四个 struct 的实现均已用 `global::System.` 前缀（含显式接口实现 `string global::System.IFormattable.ToString(...)`）。
2. **parity 测试操作数**：`ua * b`（自定义 struct × 原生无符号类型）无法隐式提升，统一改为 `ua * ub`；oracle 侧仍为原生 `a * b`。
3. **常量 `(sbyte)200` 需 `unchecked((sbyte)200)`**（CS0221）。
4. **`(ulong)(ULong)x` 转换二义性**（CS0457，double/float 两条用户定义转换路径），测试中统一写为 `(ulong)(long)(ULong)x`。
5. **CompareTo 原生语义差异**：`Byte`/`UInt16.CompareTo` 返回实际差值（范围在 int 内），`UInt32`/`UInt64` 返回 -1/0/1。`UnsignedBits.Cmp8`/`Cmp16` 改为返回差值，`Cmp32`/`Cmp64` 保持符号值；`UnsignedBitsTests` 同步更新为 100、65534。
6. **Task 6 边界用例期望修正**：`MaxValue / MaxValue = 1`（计划原写 MaxValue），随机除法循环加 `b != 0` 防护。
7. **ULong 的 D 格式**：非 hex 路径经 `decimal.ToString`，而 decimal 不支持整型专属的 `D` 说明符；ULong.ToString 对 `D[n]` 改为自行补零分发。
8. **关键字扫描含注释**：库源码注释也不得出现 `ulong` 等字面 token（Step 1 扫描为纯文本匹配）。
