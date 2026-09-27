# Cs2jUnsigned 设计：有符号模拟的无符号值类型（dotnet.System.UByte/UShort/UInt/ULong）

日期：2026-09-27
状态：已批准（设计评审通过）

## 1. 背景与目标

cs2j 是 C# → Java 转译器。Java 没有无符号整型原语。`src/Cs2jUnsignedMath/`（`Cs2jUnsigned.UnsignedMath`）只提供**纯函数算法原语**（按有符号位模式承载无符号值），不定义值类型。

本项目 `Cs2jUnsigned` 在其概念之上提供四个**完整值类型**，命名空间为 `dotnet.System`：

| C# 类型（命名空间 `dotnet.System`） | 等价 .NET 类型 | 取值范围 |
|---|---|---|
| `UByte` | `System.Byte` | 0 .. 255 |
| `UShort` | `System.UInt16` | 0 .. 65535 |
| `UInt` | `System.UInt32` | 0 .. 4294967295 |
| `ULong` | `System.UInt64` | 0 .. 18446744073709551615 |

四个类型在语义、API、常量与转换行为上分别与对应 .NET 无符号类型等价，并且全部属于转译器可处理的 C# 子集（最终转译为 Java `dotnet.system` 包下不可变值对象）。

### 1.1 交付范围（本次）

- 新建 `src/Cs2jUnsigned/` 类库：4 个 `readonly struct` + 1 个 internal 静态助手 + csproj。
- 登记进 `CSharpToJavaConverter.slnx`。
- 在 `tests/CSharpToJava.Tests/` 增加 ProjectReference 与完整语义等价测试。
- `dotnet build` 解决方案零错误；新测试与既有测试全部通过。

### 1.2 明确不在本次范围

- CLI 的 EmbeddedResource 嵌入与释放（`UnsignedMathSupportFiles` 式分发）。
- 预处理自动注入项目引用、转换器跳过策略、`config/TypeMappings.json` 变更。
- Java 侧 `dotnet.system.UByte/UShort/UInt/ULong` 手写 compat 类。
- 对 `src/Cs2jUnsignedMath/` 的任何修改（见约束 4）。

## 2. 强制约束

### 约束 1：禁止任何无符号关键字与类型

四个 struct 与助手的实现源码中**不得**出现：

- 关键字：`byte`、`ushort`、`uint`、`ulong`；
- 全名：`System.Byte`、`System.UInt16`、`System.UInt32`、`System.UInt64`；
- 上述类型的可空形式、`typeof(...)` 引用、cast 引用。

允许：`sbyte`/`System.SByte`、`short`/`System.Int16`、`int`/`System.Int32`、`long`/`System.Int64`，以及 `System.Decimal`、`string`、`System.Globalization.*` 等。

验收用 grep 对 `src/Cs2jUnsigned/**/*.cs` 做静态扫描确认零命中（注释与字符串中也避免出现这些 token，以防扫描误伤）。

### 约束 2：存储与算术一律基于有符号原语

| 值类型 | 存储字段（固定基类型） | 无符号取值恢复 | 回写 |
|---|---|---|---|
| `UByte` | `sbyte _v` | `(int)_v & 0xFF` | `(sbyte)(x & 0xFF)` |
| `UShort` | `short _v` | `(int)_v & 0xFFFF` | `(short)(x & 0xFFFF)` |
| `UInt` | `int _v` | `(long)_v & 0xFFFFFFFFL` | 直接赋 int |
| `ULong` | `long _v` | 64 位无符号解释 | 直接赋 long |

- 所有溢出语义 `unchecked`，与 .NET 无符号环绕一致。
- 位运算直接对存储字段做 `& | ^ ~`。
- `sbyte`/`short` 算术自动提升为 `int`：在 int 域完成计算后按掩码截断回写。
- 比较/除模：UInt 升 `long` 掩码；ULong 用符号位判定 + 折半修正（JDK `Long.divideUnsigned` 同款算法，见 §6.3）。

### 约束 3：一律 public readonly struct

- 四个类型声明为 `public readonly struct`；`_v` 为 `private readonly`。
- 所有实例方法不得修改 `_v`，只返回新实例/新值；运算符与转换无副作用。
- 实现 `System.IEquatable<T>`、`System.IComparable<T>`、`System.IComparable`、`System.IFormattable`（全部全名书写）。

### 约束 4：项目完全自包含，禁止依赖 Cs2jUnsignedMath

`Cs2jUnsigned.csproj` 不含任何 `ProjectReference` / `PackageReference`。

原任务中"复用 `UnsignedMath.CompareUInt32/DivideUInt64/ToStringUInt32` 等"的要求，改为在本项目 internal 助手内做**算法等价自包含重写**（算法相同，不共享代码、不添加引用）。`Cs2jUnsignedMath` 项目与文件保持不动。

## 3. 项目布局

```
src/Cs2jUnsigned/
├── Cs2jUnsigned.csproj
├── UnsignedBits.cs   # internal static class UnsignedBits，namespace dotnet.System
├── UByte.cs          # public readonly struct UByte，namespace dotnet.System
├── UShort.cs
├── UInt.cs
└── ULong.cs
```

csproj 要点（与 `Cs2jUnsignedMath.csproj` 风格一致，保证"可转换 C# 子集"）：

- `TargetFramework=net10.0`；`LangVersion=11`（需要无符号右移 `>>>`，且不使用更新语法）；
- `Nullable=disable`；`ImplicitUsings=disable`；
- 无任何引用；
- 源码零 `using` 指令，所有非语言内建类型写全名。

`CSharpToJavaConverter.slnx` 在 `/src/` 文件夹下增加本项目条目（新 GUID）。

## 4. 存储、常量与构造

### 4.1 常量

struct 不能声明自身类型的 `const`，故使用静态只读字段（值与原生常量一致）：

```csharp
public static readonly UByte MinValue = new UByte((sbyte)0);
public static readonly UByte MaxValue = new UByte((sbyte)-1); // 位模式全 1
```

- `UByte.MaxValue` = 255；`UShort.MaxValue` = 65535；`UInt.MaxValue` = 4294967295；`ULong.MaxValue` = 18446744073709551615。
- 四个 `MinValue` 均为 0。

### 4.2 构造函数（按位截断，均不抛异常）

| 类型 | 构造函数 | 语义 |
|---|---|---|
| UByte | `(sbyte)`、`(int)`、`(long)` | 取低 8 位：`(sbyte)(v & 0xFF)` |
| UShort | `(short)`、`(int)`、`(long)` | 取低 16 位：`(short)(v & 0xFFFF)` |
| UInt | `(int)`、`(long)` | 取低 32 位 |
| ULong | `(long)` | 位模式直传（long 即 64 位模式） |

注意 `new UByte((sbyte)200)`：sbyte 存位模式 `11001000`，`(int)_v & 0xFF == 200`。

另提供 `internal` 位模式访问（如 `internal sbyte Bits` / `internal static UByte FromBits(sbyte)`），供 struct 间转换与助手使用，不公开。

## 5. 转换矩阵

转换语义严格复刻原生 C# cast 链。统一约定：**struct 与有符号原语之间的用户可见转换一律 `explicit`**（即便原生某些方向为隐式，如 byte→int；任务对 UInt 已做此决策，四个类型保持一致）。struct 之间向宽为 `implicit`（零扩展），向窄为 `explicit`（截断）。

### 5.1 四 struct 之间

| 源 → 目标 | 方式 | 实现 |
|---|---|---|
| UByte → UShort/UInt/ULong | implicit | 零扩展（掩码后重解释） |
| UShort → UInt/ULong | implicit | 零扩展 |
| UInt → ULong | implicit | `(long)_v & 0xFFFFFFFFL` 后存 long |
| UShort → UByte | explicit | 低 8 位截断 |
| UInt → UByte/UShort | explicit | 低 8/16 位截断 |
| ULong → UByte/UShort/UInt | explicit | 低 8/16/32 位截断 |

### 5.2 与有符号原语（均 explicit，语义 = 对应原生 cast）

- **同宽收窄/截断**：`(int)UInt` 取 `_v`；`(sbyte)UByte`、`(short)UShort`、`(long)ULong` 取位模式。
- **无符号 → 更宽有符号（零扩展）**：`(long)UInt = (long)_v & 0xFFFFFFFFL`；`(int)UByte = (int)_v & 0xFF`；`(int)UShort = (int)_v & 0xFFFF`；`(long)UShort/UByte` 同零扩展。
- **有符号 → 同宽/更窄无符号**：按位截断，如 `(UInt)negativeInt = new UInt(negativeInt)`。
- **有符号 → 更宽无符号（符号扩展后重解释）**：`(ULong)(-1) = new ULong(-1L)` = MaxValue；`(ULong)intVal = new ULong((long)intVal)`（负数经符号扩展得到高 32 位全 1）。`(UInt)shortVal` 同理：负 short → 高 16 位全 1。
- 各类型同时提供覆盖 `sbyte/short/int/long` 的显式算子（目标类型为 T），以及从 T 到 `sbyte/short/int/long` 的显式算子，保证与原生 cast 面一致；非法组合（概念上不存在的）不额外发明。

### 5.3 浮点

四类型均提供实例方法 `ToDouble()`、`ToSingle()`、`ToDecimal()`（见 §6.4）。另提供从 `double`/`float`/`decimal` 的 `explicit` 转换算子（等价默认 unchecked 上下文下的原生 cast：向零截断后按位宽截断；NaN/无穷/越界在 C# 中为未指定值，net10 CLR 实测为 0——以 oracle 测试锁定并复刻为 0，**不**抛异常；抛 `OverflowException` 是 `checked`/`System.Convert.ToXxx` 的行为，本项目不提供）。

## 6. UnsignedBits 助手（internal static，全部只用有符号原语）

### 6.1 8/16 位 int 域

- 运算：`unchecked((a & mask) op (b & mask)) & mask)`（mask = 0xFF / 0xFFFF）；`+ - *` 环绕；`& | ^` 直接；`~` 后掩码。
- 移位（关键）：`sbyte`/`short` 操作数按 C# 规则**先提升为 int**，因此移位计数必须 `& 31`（不是 `& 7`/`& 15`），移位完成后再按 8/16 位截断。例：`(byte)1 << 8` 在 int 域得 256，截断为 0；`(byte)1 << 31` 截断为 128。`>>>`/`>>` 均为逻辑移位（掩码值已非负，二者结果相同）。
- 除模：掩码后均为非负 int，直接 `/`、`%`；除零自然抛 `System.DivideByZeroException`。
- 比较：掩码后直接比较。

### 6.2 32 位

- 环绕算术：`unchecked(a + b)` 等；除模：`(int)(((long)a & 0xFFFFFFFFL) / ((long)b & 0xFFFFFFFFL))`。
- 逻辑右移：`a >>> (n & 31)`；左移 `a << (n & 31)`；`>>` 同样映射为 `>>>`（无符号类型上二者语义相同）。
- 比较：升 long 掩码后比较。

### 6.3 64 位

- 环绕算术：`unchecked`；位运算直接；移位计数 `& 63`；右移 `>>>`。
- 比较：相等先判；`a>=0 && b<0 → -1`；`a<0 && b>=0 → 1`；同号按有符号比较。
- 除模（b=0 时 `a/b` 自然抛异常）：
  - `b < 0`：商为 `Compare(a,b) < 0 ? 0 : 1`；
  - `a >= 0`：`a / b`；
  - 否则 `q = ((a >>> 1) / b) * 2; r = a - q*b; return q + (Compare(r,b) >= 0 ? 1 : 0);`
  - 取模：`a - Divide(a,b) * b`。

### 6.4 与 decimal / 浮点的典范转换

- 无符号 64 位 → decimal：`(decimal)(v >>> 1) * 2m + (decimal)(v & 1L)`（decimal 28 位精度对 ≤ 20 位整数精确）。32/16/8 位先用各自零扩展。
- decimal → 64 位模式：`d >= 9223372036854775808m ? (long)(d - 18446744073709551616m) : (long)d`（`unchecked` 上下文中）；窄位宽再截断。
- ToDouble/ToSingle：默认经 decimal 由运行时舍入；**若对照测试发现与原生 ulong→double 的双舍入差异**，改用 JDK 风格公式：`(double)(v >>> 1) * 2.0 + (double)(v & 1L)`（32 位用 `(double)(v & 0xFFFFFFFFL)` 精确）。以 net10 原生行为为最终准绳。
- float/double → T：默认 unchecked cast 语义——`System.Math.Truncate` 向零取整；NaN/无穷/越界在 net10 CLR 下为 0（oracle 锁定），不抛异常。

### 6.5 ToString

- 无格式/默认 provider：把值零扩展到非负表示（8/16/32 → int 或 long；64 → decimal），调 `ToString(System.Globalization.CultureInfo.InvariantCulture)`（无参 `ToString()` 等价不变文化的十进制）。
- 标准数字格式 D/F/N/E/G/C/P/R 与自定义格式：委托非负典范表示（long 或 decimal）的 `ToString(format, provider ?? InvariantCulture)`。
- **十六进制定制路径**（`X`/`x`）：不能委托 long/decimal（-1 的 X 会输出 16 个 F）。助手按 `>>>` 每次取 4 位，映射 0-9a-f，按位宽生成 2/4/8/16 个 nibble，精度说明符为最小位数（前导零填充）。大小写跟随 `X`/`x`。

### 6.6 Parse / TryParse

四类型共享按"位宽参数化"的实现：

- 公共 API：`Parse(string)`、`Parse(string, System.Globalization.NumberStyles)`、`Parse(string, System.IFormatProvider)`、`Parse(string, System.Globalization.NumberStyles, System.IFormatProvider)`；`TryParse(string, out T)`、`TryParse(string, System.Globalization.NumberStyles, System.IFormatProvider, out T)`。
- TryParse 返回 false 代替抛异常（null 入参仍按原生抛 `System.ArgumentNullException`）。
- NumberStyles 校验：不支持的组合/未定义位（含 `AllowExponent`、`AllowCurrencySymbol`）抛 `System.ArgumentException`；各样式（Integer/HexNumber/Number/AllowBinarySpecifier 等）的允许字符按原生规则。
- 词法处理：首尾空白（依据 styles）、前/后置符号与括号、千分位（取 provider 的 `NumberFormatInfo.NumberGroupSeparator/NumberGroupSizes` 做分组校验）、`AllowDecimalPoint` 时允许小数点但小数必须为零、`HexNumber` 十六进制（net10 允许可选 `0x` 前缀的行为以 oracle 锁定）、`AllowBinarySpecifier` 二进制（`0b` 前缀）、`NativeDigits` 归一化。
- 数值累积用 `decimal` 精确累加（按进制加权），解析完成后做无符号范围校验（越界：Parse 抛 `System.OverflowException`，TryParse 返回 false）。
- **凡 net10 行为存在不确定性（0x 前缀、provider 为 null 的回退、分组严格度、负号位置），一律以 net10 原生 `System.Byte/UInt16/UInt32/UInt64.Parse` 为 oracle 写对照测试锁定**，再据结果微调实现。

## 7. 各 struct 的成员面（四类型对称）

以 UInt 为范例，其余按位宽裁剪：

1. 常量：`MinValue`、`MaxValue`。
2. 构造：见 §4.2。
3. 解析/格式化：见 §6.6、§6.5（含 `System.IFormattable.ToString(string, IFormatProvider)` 显式实现）。
4. 转换：见 §5（含 `ToDouble/ToSingle/ToDecimal`）。
5. 相等/比较：
   - `bool Equals(T other)`；`override bool Equals(object obj)`；`override int GetHashCode()`；
   - `int CompareTo(T other)`；`int CompareTo(object obj)`（null 返回 1，类型不符抛 `System.ArgumentException`，与原生值类型一致）；
   - 接口：`System.IEquatable<T>`、`System.IComparable<T>`、`System.IComparable`；
   - 算子：`== != < <= > >=`。
   - GetHashCode：UByte/UShort 为掩码非负值；UInt 为 `_v`；ULong 为 `unchecked((int)_v ^ (int)(_v >>> 32))`（与原生 ulong 一致）。
6. 算术/位算子：`+ - * / % & | ^ ~ << >> >>>`，全部 `unchecked`。
7. `override string ToString()`。

明确排除：

- `System.IConvertible`：其方法签名（`ToByte()`→`System.Byte`、`ToUInt16()`→`System.UInt16` 等）直接引用约束 1 禁用类型，无法在不违约的前提下实现。
- `INumber<TSelf>` 等静态抽象接口（超出可转译子集）。
- `TryFormat(System.Span<...>)`、`ISpanParsable<T>`（Span 不在可转译子集）。

## 8. 测试策略

测试项目可自由使用原生 `byte/ushort/uint/ulong` 作为 oracle。新增目录 `tests/CSharpToJava.Tests/Cs2jUnsigned/`：

1. **算术等价**（`ArithmeticParityTests.cs`）：边界值 + 10k 确定性伪随机值（固定种子，覆盖跨符号位点），逐类型对照原生：`+ - * / %` 环绕、除零异常类型、`& | ^ ~`、`<< >> >>>`（含越界/跨位宽移位计数，重点锁定 `(byte)1 << 8 == 0`、`(byte)1 << 31 == 128` 等 int 提升语义）。
2. **比较与相等**（`ComparisonParityTests.cs`）：全序、`CompareTo(object)`、null/错误类型、GetHashCode 与原生一致。
3. **转换矩阵**（`ConversionParityTests.cs`）：四 struct 互转（隐式/显式）、与 sbyte/short/int/long 的显式转换、与 float/double/decimal 的转换（含向零截断、NaN/无穷/越界在 unchecked cast 下得 0），逐值对照原生 cast。
4. **Parse/TryParse**（`ParseParityTests.cs`）：往返；畸形串；越界（`"256"`、`"65536"`、`"4294967296"`、`"18446744073709551616"`）；负号与括号；空白；Hex（含 `0xFF`/`FF`/`X` 样式）与 Binary（`0b`）；千分位与自定义 provider；异常类型（null、非法 NumberStyles、FormatException、OverflowException）。
5. **ToString**（`ToStringParityTests.cs`）：默认、`D/N/X2/X4/X8/X16` 等格式矩阵，不变文化与自定义 provider。
6. **ULong 专项**（`ULongEdgeTests.cs`）：2^63 边界；除数高位置 1（商 0/1）；MaxValue/MinValue 字符串往返；ToDouble/ToSingle/ToDecimal 边界（2^53、2^63、MaxValue）。

## 9. 验收标准

1. `dotnet build CSharpToJavaConverter.slnx` 零错误、零警告新增。
2. `dotnet test`（至少 CSharpToJava.Tests）全部通过，既有测试无回归。
3. grep 静态扫描：`src/Cs2jUnsigned/**/*.cs` 不出现被禁关键字/类型名（含注释）。
4. csproj 零引用；`src/Cs2jUnsignedMath/` 无任何改动（git 层面验证）。
5. 四个类型均为 `public readonly struct`，字段为 `private readonly`。
6. 全部对照测试在 net10 上与原生类型行为一致。
