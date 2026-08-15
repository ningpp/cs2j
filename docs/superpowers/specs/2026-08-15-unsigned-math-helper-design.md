# unsigned-type 预处理改为静态 UnsignedMath 工具类

日期：2026-08-15（修订版 2，吸收评审意见）

## 1. 背景与目标

`unsigned-type` 是 C#→C# 前置转换，目的是把 C# 项目里
`byte`/`ushort`/`uint`/`ulong` 相关的类型、运算、比较、移位和转换收敛成
统一、可编译、语义等价的 C#，从而让后续 cs2j 主转换器不再面对大量
unsigned、边界和混合类型分支，只需通过补充 `config\TypeMappings.json`
映射调用点即可正确转换。

现状（扩展方法方案）的问题：

- 调用是接收者风格 `a.Add(b)`，`TypeMappings.json` 的
  `(type, method, javaMethod)` 机制难以直接映射；
- 辅助文件按项目复制多份，Java 侧会产生重复类；
- 辅助文件内部仍大量使用 unsigned 关键字和扩展方法语法。

目标：

- 把涉及 unsigned 类型的运算/比较/移位/转换/混合运算全部改写为
  `Cs2jUnsigned.UnsignedMath.XXX(...)` 全限定静态调用；
- 混合有符号/无符号运算必须显式插入归一化调用，不依赖隐式转换；
- 辅助文件物理上只生成一份，各项目通过 `Compile Include`/`Link` 引用；
- 中间 C# 必须可编译、可运行且与原代码语义一致；
- 本阶段不产出 Java、不改主转换器、不改 `config\TypeMappings.json`。

## 2. 范围

处理类型：

- `byte`（`System.Byte`）
- `ushort`（`System.UInt16`）
- `uint`（`System.UInt32`）
- `ulong`（`System.UInt64`）

处理语法：

- 二元算术 `+ - * / %`
- 二元位运算 `& | ^`
- 比较 `== != < <= > >=`
- 移位 `<< >>`
- 一元 `~`；`-` 仅限 uint（C# 结果为 long），ulong 取负本身是 C# 编译错误
- 前/后置 `++` / `--`
- 复合赋值 `+= -= *= /= %= &= |= ^= <<= >>=`
- 显式强制转换（源或目标为上述 unsigned 类型，具体见 §5.7）
- 混合有符号/无符号运算（如 `ushort_var + int_var`、
  `uint_var + int_var`、`byte_var + uint_var`、`uint_var + ulong_var`）

明确不处理并记录为已知偏差：

- `int`/`long`/`short`/`sbyte`/`char` 自身（非 unsigned）的原生运算；
- 常量表达式（const 字段/局部变量、枚举值、特性参数、默认参数、
  case 标签、自动属性初始化器）保持原生运算符，避免 CS0133；
- `float`/`double`/`decimal` → unsigned 的反向转换（`(uint)double` 等）
  保持原生；Java 侧对超出表示范围的值存在饱和/截断差异，留待后续
  TypeMappings/主转换器专项处理；
- 源为 unsigned、目标为枚举（`(MyEnum)u`）的转换保持原生，由主转换器
  既有的枚举底层转换逻辑处理；
- 项目级 `CheckForOverflowUnderflow=true` 的精确溢出语义（见 §10）。

## 3. 前置条件：Java 存储映射

本设计的 int 域直传、位模式参数等全部依赖以下 Java 侧存储映射
（属于后续 TypeMappings 修订的明确前置条件，不在本阶段实现）：

| C# 类型 | Java 存储 | 语义 |
| --- | --- | --- |
| `System.Byte` | `int` | 0..255（零扩展） |
| `System.UInt16` | `int` | 0..65535（零扩展） |
| `System.UInt32` | `int` | 32 位位模式 |
| `System.UInt64` | `long` | 64 位位模式 |

现状核对（`config\TypeMappings.json`）：

- `System.Byte → int` 已正确；
- `System.UInt32 → int`、`System.UInt64 → long` 已正确；
- `System.UInt16 → short` 是**错误映射**（40000 会变成负数），
  后续 TypeMappings 修订时必须改为 `int`。

在 `System.UInt16 → int` 修正之前，int 域直传（§5.2）不成立；
本规格把该修正列为完成后续 Java 转换的前置条件。

## 4. 生成物

### 4.1 辅助文件

- 文件名：`_Cs2jUnsignedMath.cs`
- 内容骨架（使用块式 namespace，兼容 C# 8+ 项目）：

```csharp
namespace Cs2jUnsigned
{
    internal static class UnsignedMath
    {
        // 全部方法见 §5；实现以 int/long、位掩码和原生 int/long 运算为主，
        // 并统一包在 unchecked(...) 中；个别无符号 64 位运算
        // （DivideUInt64/RemainderUInt64/RightShiftUInt64、浮点/十进制转换）
        // 可在支撑文件内部使用 System.UInt64/BigInteger，这些内部构造不进入
        // Java 输出（见 §4.3 支撑文件约定）。
    }
}
```

- 物理位置：`.cs2j-unsigned-src` 根目录，只生成一份。
- 每个实际编译被改写源文件的项目，在其 csproj 中追加：

```xml
<Compile Include="..\_Cs2jUnsignedMath.cs" Link="_Cs2jUnsignedMath.cs" />
```

（相对路径按项目目录层级计算。）每个程序集编译同一物理文件的
`internal` 副本，避免跨程序集依赖。

### 4.2 调用形态

- 不再注入 `using Cs2jUnsigned;`；
- 所有改写点输出全限定静态调用：
  `Cs2jUnsigned.UnsignedMath.Method(operand...)`；
- uint/ulong 操作数进入 int/long 参数时，重写器插入位保持的显式
  C# 转换 `(int)` / `(long)`，并包裹在 `unchecked(...)` 中
  （`unchecked((int)u)`）；Java 侧对应 no-op 转换。

### 4.3 支撑文件约定

`_Cs2j*` 前缀文件是转换支撑文件。后续主转换器应跳过其类定义转换，
只通过 `TypeMappings.json` 映射调用点；本阶段不改主转换器，仅在
文档中记录该契约（见 §9）。

## 5. 辅助方法目录

命名规则：**每个方法名全局唯一、参数个数固定**。这样
`TypeMappings.json` 按 `(type, method)` 即可唯一命中，不需要依赖
`signature` 消歧（现有注册表 `signature` 只按参数个数解析，无法区分
同参数个数的不同重载，见 §9）。

约定：`int` 表示 32 位位模式，`long` 表示 64 位位模式；
`UInt32` 域方法把 `int` 参数按无符号 32 位解释；
`UInt64` 域方法把 `long` 参数按无符号 64 位解释。

### 5.1 Int32 域（byte/ushort 提升后的结果，以及与 int 混合）

| 方法 | C# 签名 | 实现要点 |
| --- | --- | --- |
| AddInt32 | `int AddInt32(int a, int b)` | `unchecked(a + b)` |
| SubtractInt32 | `int SubtractInt32(int a, int b)` | `unchecked(a - b)` |
| MultiplyInt32 | `int MultiplyInt32(int a, int b)` | `unchecked(a * b)` |
| DivideInt32 | `int DivideInt32(int a, int b)` | `a / b` |
| RemainderInt32 | `int RemainderInt32(int a, int b)` | `a % b` |
| AndInt32 | `int AndInt32(int a, int b)` | `a & b` |
| OrInt32 | `int OrInt32(int a, int b)` | `a \| b` |
| XorInt32 | `int XorInt32(int a, int b)` | `a ^ b` |
| NotInt32 | `int NotInt32(int a)` | `~a` |
| LeftShiftInt32 | `int LeftShiftInt32(int a, int n)` | `a << (n & 31)` |
| RightShiftInt32 | `int RightShiftInt32(int a, int n)` | `a >> (n & 31)` |
| CompareInt32 | `int CompareInt32(int a, int b)` | -1/0/1 |

说明：`byte`/`ushort` 的提升运算在 Java 侧同样可用原生 int 运算表达，
这里仍全部改写，是刻意为之：让“涉及 unsigned 操作数的运算一律走
静态调用”成为唯一规则，主转换器不需要判断哪些提升该保留。

### 5.2 Int64 域（混合运算提升为 long 的结果，有符号）

当混合运算提升结果为有符号 long 时（`uint` 与 `int`/`long`，
`byte`/`ushort` 与 `long` 等；`byte`/`ushort` 与 `int` 的结果是 int，
走 Int32 域），走本域：

| 方法 | C# 签名 | 实现要点 |
| --- | --- | --- |
| AddInt64 | `long AddInt64(long a, long b)` | `unchecked(a + b)` |
| SubtractInt64 | `long SubtractInt64(long a, long b)` | `unchecked(a - b)` |
| MultiplyInt64 | `long MultiplyInt64(long a, long b)` | `unchecked(a * b)` |
| DivideInt64 | `long DivideInt64(long a, long b)` | `a / b` |
| RemainderInt64 | `long RemainderInt64(long a, long b)` | `a % b` |
| AndInt64 | `long AndInt64(long a, long b)` | `a & b` |
| OrInt64 | `long OrInt64(long a, long b)` | `a \| b` |
| XorInt64 | `long XorInt64(long a, long b)` | `a ^ b` |
| CompareInt64 | `int CompareInt64(long a, long b)` | -1/0/1 |

### 5.3 UInt32 域

| 方法 | C# 签名 | 实现要点 |
| --- | --- | --- |
| AddUInt32 | `int AddUInt32(int a, int b)` | `unchecked(a + b)`（回绕） |
| SubtractUInt32 | `int SubtractUInt32(int a, int b)` | `unchecked(a - b)`（回绕） |
| MultiplyUInt32 | `int MultiplyUInt32(int a, int b)` | `unchecked(a * b)`（回绕） |
| DivideUInt32 | `int DivideUInt32(int a, int b)` | `(int)((a & 0xFFFFFFFFL) / (b & 0xFFFFFFFFL))` |
| RemainderUInt32 | `int RemainderUInt32(int a, int b)` | `(int)((a & 0xFFFFFFFFL) % (b & 0xFFFFFFFFL))` |
| AndUInt32 | `int AndUInt32(int a, int b)` | `a & b` |
| OrUInt32 | `int OrUInt32(int a, int b)` | `a \| b` |
| XorUInt32 | `int XorUInt32(int a, int b)` | `a ^ b` |
| NotUInt32 | `int NotUInt32(int a)` | `~a` |
| LeftShiftUInt32 | `int LeftShiftUInt32(int a, int n)` | `a << (n & 31)` |
| RightShiftUInt32 | `int RightShiftUInt32(int a, int n)` | `(int)((a & 0xFFFFFFFFL) >> (n & 31))`（逻辑右移） |
| CompareUInt32 | `int CompareUInt32(int a, int b)` | 无符号比较 -1/0/1 |

### 5.4 UInt64 域

| 方法 | C# 签名 | 实现要点 |
| --- | --- | --- |
| AddUInt64 | `long AddUInt64(long a, long b)` | `unchecked(a + b)`（回绕） |
| SubtractUInt64 | `long SubtractUInt64(long a, long b)` | `unchecked(a - b)`（回绕） |
| MultiplyUInt64 | `long MultiplyUInt64(long a, long b)` | `unchecked(a * b)`（回绕） |
| DivideUInt64 | `long DivideUInt64(long a, long b)` | 无符号 64 位除法（见下） |
| RemainderUInt64 | `long RemainderUInt64(long a, long b)` | 无符号 64 位取余（见下） |
| AndUInt64 | `long AndUInt64(long a, long b)` | `a & b` |
| OrUInt64 | `long OrUInt64(long a, long b)` | `a \| b` |
| XorUInt64 | `long XorUInt64(long a, long b)` | `a ^ b` |
| NotUInt64 | `long NotUInt64(long a)` | `~a` |
| LeftShiftUInt64 | `long LeftShiftUInt64(long a, int n)` | `a << (n & 63)` |
| RightShiftUInt64 | `long RightShiftUInt64(long a, int n)` | 逻辑右移（见下） |
| CompareUInt64 | `int CompareUInt64(long a, long b)` | 无符号比较 -1/0/1 |

`DivideUInt64`/`RemainderUInt64`/`RightShiftUInt64` 的实现可以在
支撑文件内部使用 `System.UInt64` 或 `System.Numerics.BigInteger`
保证 C# 侧正确性；这些内部构造不进入 Java 输出（支撑文件约定），
Java 侧调用点由 TypeMappings 映射到 `Long.divideUnsigned` 等原生方法。

### 5.5 转换方法

只保留 Java 无法用原生转换等价表达的转换；位保持转换
（int↔uint、long↔ulong、`(uint)long`、`(ulong)int` 等）保持原生
C# 转换，Java 侧是 no-op 或标准截断。

| 方法 | C# 签名 | 语义 |
| --- | --- | --- |
| ToByteInt32 | `System.Byte ToByteInt32(int v)` | `unchecked((byte)(v & 0xFF))` |
| ToByteInt64 | `System.Byte ToByteInt64(long v)` | `unchecked((byte)((int)v & 0xFF))` |
| ToUInt16Int32 | `System.UInt16 ToUInt16Int32(int v)` | `unchecked((ushort)(v & 0xFFFF))` |
| ToUInt16Int64 | `System.UInt16 ToUInt16Int64(long v)` | `unchecked((ushort)((int)v & 0xFFFF))` |
| ToInt64UInt32 | `long ToInt64UInt32(int v)` | `v & 0xFFFFFFFFL`（uint→long 无符号扩展） |
| ToUInt64UInt32 | `System.UInt64 ToUInt64UInt32(int v)` | `unchecked((ulong)(v & 0xFFFFFFFFL))`（uint→ulong 无符号扩展） |
| ToInt32UInt64 | `int ToInt32UInt64(long v)` | `(int)v`（ulong→int 取低 32 位） |
| ToDoubleUInt32 | `double ToDoubleUInt32(int v)` | `(double)(v & 0xFFFFFFFFL)` |
| ToDoubleUInt64 | `double ToDoubleUInt64(long v)` | 按无符号值转 double（内部用 ulong/BigInteger） |
| ToSingleUInt32 | `float ToSingleUInt32(int v)` | `(float)(v & 0xFFFFFFFFL)` |
| ToSingleUInt64 | `float ToSingleUInt64(long v)` | 按无符号值转 float（内部用 ulong/BigInteger） |
| ToDecimalUInt32 | `decimal ToDecimalUInt32(int v)` | `(decimal)(v & 0xFFFFFFFFL)` |
| ToDecimalUInt64 | `decimal ToDecimalUInt64(long v)` | 按无符号值转 decimal（内部用 ulong/BigInteger） |

源为 unsigned、目标为 `char` 的转换（`(char)u`）保持原生：C# 与 Java
都是“截断到低 16 位并按 0..65535 解释”，语义一致。

## 6. 重写规则

### 6.1 分类依据

重写器以 Roslyn 语义模型给出的**操作数实际类型（含常量隐式转换后的
ConvertedType）与运算结果类型**为准进行分类，不按源码字面形态猜测。
这自动覆盖常量操作数：

- `u + 5`：字面量 5 常量隐式转换为 uint → UInt32 域；
- `u + 5L`：5L 是 long → Int64 域；
- `u + (-1)`：-1 是 int → Int64 域；
- `ul + 5`：5 常量隐式转换为 ulong → UInt64 域；
- `ul + i`（i 为非常量 int）：C# 本身是 CS0019，不会出现在合法输入中。

### 6.2 整数提升归类表

二进制运算（算术/位运算/比较）按以下规则归类：

| 操作数组合 | C# 结果 | 重写域 |
| --- | --- | --- |
| byte/ushort 与 byte/ushort/sbyte/short/char/int | int | Int32 |
| byte/ushort 与 long | long | Int64 |
| uint 与 sbyte/short/int | long | Int64 |
| uint 与 long | long | Int64 |
| uint 与 byte/ushort/char/uint | uint | UInt32 |
| ulong 与 byte/ushort/char/uint/ulong | ulong | UInt64 |
| ulong 与 int/long/short/sbyte | 非法（CS0019），除非常量可隐式转换 | 按 ConvertedType 归类 |
| int 与 int / long 与 long（纯有符号） | 不重写 | 原生 |

移位运算按“提升后的左操作数类型”归类（移位不参与算术提升）：

- byte/ushort 左操作数 → Int32 域；
- uint → UInt32 域；
- ulong → UInt64 域；
- int/long → 原生，不重写。

### 6.3 同型/提升后为 int 的运算

示例（`b1,b2` 为 byte，`s` 为 ushort，`i` 为 int）：

```csharp
b1 + b2       → UnsignedMath.AddInt32(b1, b2)
s + i         → UnsignedMath.AddInt32(s, i)
b1 - i        → UnsignedMath.SubtractInt32(b1, i)
s << 3        → UnsignedMath.LeftShiftInt32(s, 3)
~b1           → UnsignedMath.NotInt32(b1)
b1 == b2      → UnsignedMath.CompareInt32(b1, b2) == 0
s <= i        → UnsignedMath.CompareInt32(s, i) <= 0
```

byte/ushort→int 的提升在 C# 与 Java 两侧一致，调用点直接传参即可；
该直传成立的前提是 §3 的存储映射（`System.UInt16 → int`）。

### 6.4 UInt32 / UInt64 域

示例（`u,u1,u2` 为 uint，`ul,ul1,ul2` 为 ulong）：

```csharp
u1 + u2       → UnsignedMath.AddUInt32(unchecked((int)u1), unchecked((int)u2))
u1 / u2       → UnsignedMath.DivideUInt32(unchecked((int)u1), unchecked((int)u2))
u1 < u2       → UnsignedMath.CompareUInt32(unchecked((int)u1), unchecked((int)u2)) < 0
u >> 4        → UnsignedMath.RightShiftUInt32(unchecked((int)u), 4)
~u            → UnsignedMath.NotUInt32(unchecked((int)u))
ul1 + ul2     → UnsignedMath.AddUInt64(unchecked((long)ul1), unchecked((long)ul2))
ul1 == ul2    → UnsignedMath.CompareUInt64(unchecked((long)ul1), unchecked((long)ul2)) == 0
```

`(int)u`/`(long)ul` 是位保持转换，Java 侧是 no-op；统一包裹
`unchecked(...)` 避免项目级 checked 设置引入新异常（见 §10）。

### 6.5 混合运算（必须显式插入归一化）

示例（`s` 为 ushort，`i` 为 int，`l` 为 long，`u` 为 uint，`ul` 为 ulong）：

```csharp
u + i         → UnsignedMath.AddInt64(
                    UnsignedMath.ToInt64UInt32(unchecked((int)u)), i)         // 结果 long
u + l         → UnsignedMath.AddInt64(
                    UnsignedMath.ToInt64UInt32(unchecked((int)u)), l)         // 结果 long
b + l         → UnsignedMath.AddInt64(b, l)                                  // 结果 long
s - l         → UnsignedMath.SubtractInt64(s, l)                             // 结果 long
s + u         → UnsignedMath.AddUInt32(s, unchecked((int)u))                 // 结果 uint
b + u         → UnsignedMath.AddUInt32(b, unchecked((int)u))                 // 结果 uint
u + ul        → UnsignedMath.AddUInt64(
                    UnsignedMath.ToInt64UInt32(unchecked((int)u)),
                    unchecked((long)ul))                                      // 结果 ulong
ul + u        → UnsignedMath.AddUInt64(
                    unchecked((long)ul),
                    UnsignedMath.ToInt64UInt32(unchecked((int)u)))            // 结果 ulong
ul + b        → UnsignedMath.AddUInt64(unchecked((long)ul), b)               // 结果 ulong
u == i        → UnsignedMath.CompareInt64(
                    UnsignedMath.ToInt64UInt32(unchecked((int)u)), i) == 0
u < ul        → UnsignedMath.CompareUInt64(
                    UnsignedMath.ToInt64UInt32(unchecked((int)u)),
                    unchecked((long)ul)) < 0
```

规则：只要 C# 发生了 Java 无法直接镜像的提升（uint→long 无符号扩展、
byte/ushort→uint、任一操作数进入 ulong 域等），重写器必须显式插入
对应的转换方法调用，禁止依赖隐式转换把语义留给主转换器推断。

非法组合说明：`ul + i`、`ul + l`、`ul + s`（s 为 sbyte/short）在
C# 是编译错误，重写器不需要处理；`ul += i` 这类复合赋值因 C#
复合赋值特殊规则**是合法**的，见 §6.7。

### 6.6 一元与自增自减

```csharp
-u            → -UnsignedMath.ToInt64UInt32(unchecked((int)u))   // 结果 long；long 取负是 Java 原生
b++           → b = UnsignedMath.ToByteInt32(UnsignedMath.AddInt32(b, 1))
s--           → s = UnsignedMath.ToUInt16Int32(UnsignedMath.SubtractInt32(s, 1))
u++           → u = unchecked((uint)UnsignedMath.AddUInt32(
                    unchecked((int)u), 1))
ul++          → ul = unchecked((ulong)UnsignedMath.AddUInt64(
                    unchecked((long)ul), 1))
```

后置自增/自减继续沿用现有“先求值、必要时临时变量保存旧值”的模式
（`UnsignedPostfixRewriter` 骨架保留，调用改为静态方法）。

### 6.7 复合赋值

统一改写为“读左值 → 归一化运算 → 写回（必要时截断）”：

```csharp
b += 1        → b = UnsignedMath.ToByteInt32(UnsignedMath.AddInt32(b, 1))
s += i        → s = UnsignedMath.ToUInt16Int32(UnsignedMath.AddInt32(s, i))
u += 5        → u = unchecked((uint)UnsignedMath.AddUInt32(
                    unchecked((int)u), 5))                              // 常量走 UInt32 域
ul += 5ul     → ul = unchecked((ulong)UnsignedMath.AddUInt64(
                    unchecked((long)ul), 5ul))  // 注意：ulong += int 在 C# 中本身是
                                                // CS0034 编译错误，预处理不会遇到
u <<= 4       → u = unchecked((uint)UnsignedMath.LeftShiftUInt32(
                    unchecked((int)u), 4))
ul /= ul2     → ul = unchecked((ulong)UnsignedMath.DivideUInt64(
                    unchecked((long)ul), unchecked((long)ul2)))
```

仅当左值是局部变量/参数/字段（无副作用、求值一次）时改写；属性、
索引器保持原生，避免 getter/receiver 二次求值（沿用现有约束）。

注意：C# 复合赋值要求右操作数可隐式转换到左类型，否则是编译错误
（实测 `uint += int`、`uint += long`、`ulong += int`、`byte += int`
均报 CS0266/CS0034）。因此复合赋值的域与左类型一致（byte/ushort →
Int32、uint → UInt32、ulong → UInt64），不存在“混合提升为 long 的
复合赋值”输入。

### 6.8 强制转换

- 目标为 `byte`/`ushort`：`ToByteInt32(...)` / `ToByteInt64(...)` /
  `ToUInt16Int32(...)` / `ToUInt16Int64(...)`（按源宽度选择）；
- `uint → long`：`ToInt64UInt32(...)`；
- `ulong → int`：`ToInt32UInt64(...)`；
- unsigned → float/double/decimal：`ToSingleUInt32` 等（§5.5）；
- 位保持转换（int↔uint、long↔ulong）保持原生，必要时包裹
  `unchecked(...)`；
- `(char)`、`(MyEnum)` 目标保持原生（§2）；
- 常量表达式上下文的强制转换保持原生。

### 6.9 不重写的场景

- 纯 `int`/`long` 运算；
- 常量表达式（const 初始化、枚举值、特性参数、默认参数、case 标签、
  自动属性初始化器）内的运算符/转换；
- 语法级 checked 上下文（沿用现有抑制逻辑，保持溢出行为）；
- 属性/索引器复合赋值与自增自减（沿用现有抑制逻辑）；
- float/double/decimal → unsigned 的反向转换。

## 7. 预处理管线改动

- `UnsignedExtensionsFileContent` → `UnsignedMathFileContent`：
  `FileName = "_Cs2jUnsignedMath.cs"`，内容为块式 namespace 的
  `internal static class UnsignedMath`（方法目录见 §5）。
- `UnsignedTypeSyntaxRewriter`：
  - 删除 `using Cs2jUnsigned;` 注入；
  - 二元/一元/复合赋值/自增自减/强制转换改为输出
    `Cs2jUnsigned.UnsignedMath.*` 全限定静态调用；
  - 按 §6 的归类表处理混合运算，显式插入转换调用；
  - 插入的位保持转换统一包裹 `unchecked(...)`；
  - `UsedExtensions` 语义改为 `UsedUnsignedMath`（触发辅助文件生成）。
- `ProjectUnsignedTypePreprocessor`：
  - 只写一份根目录 `_Cs2jUnsignedMath.cs`；
  - 对每个实际编译被改写源文件的项目追加
    `<Compile Include="..\_Cs2jUnsignedMath.cs" Link="_Cs2jUnsignedMath.cs" />`
    （不再往项目目录复制本地副本）；
  - 追加前先移除该项目中已存在的 `_Cs2jUnsignedMath.cs` 条目，避免重复；
  - 当项目目录就是辅助文件所在根目录、且 SDK 默认 glob 已包含该文件时，
    不再追加显式 Compile Include，避免重复编译；
  - 保留“关闭默认 glob / 显式 Compile Include”项目的兼容处理。
- **重复运行/升级**：预处理每次运行都会先删除并重建
  `.cs2j-unsigned-src` 中间目录（现有行为），因此旧方案的
  `_Cs2jUnsignedExtensions.cs` 本地副本及其 Compile Include 条目
  不可能残留；实现不得改为“增量复用已有中间目录”。
- CLI 输出统计行保持 `unsigned-type: ... operators rewritten` 格式。

## 8. 验证

### 8.1 单元/项目测试

- 更新 `UnsignedTypeRewriterUnitTests`：
  - 断言输出为 `Cs2jUnsigned.UnsignedMath.AddInt32(...)` 等全限定调用；
  - 断言不再注入 `using Cs2jUnsigned;`；
  - 断言辅助文件为 `internal static class UnsignedMath`（块式 namespace）；
  - 新增用例：`ushort+int`、`uint+int`、`byte+long`、`ushort-long`、
    `byte+uint`、`uint+ulong`、`ulong+uint`、`ulong+byte`、
    `uint==int`、`ulong>>n`、`~byte`、常量操作数（`u+5`、`u+5L`、
    `ul+5`、`u+(-1)`）、`ul+=i`、`(byte)uint`、`(long)uint`、
    `(int)ulong`、`(double)uint`、`(char)uint` 保持原生、
    复合赋值、前后置自增自减；
  - 断言插入的位保持转换带 `unchecked(...)`；
  - 保留常量表达式不重写、语法级 checked 不重写、指针解引用/负值
    转型括号等既有回归。
- 更新 `UnsignedTypeProjectTests`：
  - 根目录只有一份 `_Cs2jUnsignedMath.cs`；
  - 各编译项目 csproj 含 `Compile Include="..\_Cs2jUnsignedMath.cs"`
    + `Link`，且不存在重复条目；
  - 根目录即项目目录时（SDK 默认 glob）不追加显式条目；
  - 重复运行同一输出目录不产生孤儿 `_Cs2jUnsignedExtensions.cs`；
  - 保留显式 Compile Include / 跨目录编译项目的回归用例。
- `UnsignedTypeSemanticsTests`：编译并运行原始与改写后代码，断言运行
  结果一致（覆盖混合运算、常量操作数与边界值）。

### 8.2 端到端

- 用 `unsigned-type` verb 重新转换目标 SDK 项目，产物放入指定输出目录；
- 在中间源解决方案上执行 `dotnet test -c Release`，必须零失败；
- 仓库自身 `dotnet test -c Release` 全绿。

## 9. TypeMappings 契约（后续补充，不在本阶段实现）

现有注册表事实（`TypeMappingRegistry.cs`）：

- `MethodMappingEntry` 只有 `type`/`method`/`javaMethod`/`signature`
  四个字段，**没有 `imports` 字段**；
- `signature` 按**整型参数个数**解析（`int.TryParse`），不是类型签名，
  无法区分同参数个数的不同重载。

因此本设计规定：

- 辅助方法**方法名全局唯一、参数个数固定**，调用点按
  `(type="Cs2jUnsigned.UnsignedMath", method=<唯一名>)` 即可唯一命中，
  不依赖 `signature`；
- TypeMappings 条目示例（本阶段不写入配置）：

```json
{
  "type": "Cs2jUnsigned.UnsignedMath",
  "method": "AddUInt32",
  "javaMethod": "io.github.ningpp.compat.UnsignedMath.addUInt32"
}
```

- `javaMethod` 写成全限定名，避免依赖不存在的 `imports` 字段；
  若后续希望用短名 + import，需要先扩展 `MethodMappingEntry` 支持
  imports（属于后续主转换器工作，不在本阶段）；
- 主转换器跳过 `_Cs2j*` 支撑文件的类定义转换（后续小改动，
  不在本规格实现范围内）；
- 后续修订 `config\TypeMappings.json` 时，必须同时把
  `System.UInt16` 映射从 `short` 改为 `int`（§3 前置条件）。

## 10. checked / 溢出语义

要求：

- 辅助类内部所有算术统一写在 `unchecked(...)` 中；
- 重写器插入的位保持转换统一写成 `unchecked((int)u)`、
  `unchecked((long)ul)`、`unchecked((uint)...)` 等形式；
- 语法级 `checked { }` / `checked(...)` 上下文继续沿用现有抑制逻辑，
  不改写其中的运算/转换。

已知偏差（显式记录）：

- 项目级 `CheckForOverflowUnderflow=true` 时，原代码中 uint/ulong
  算术溢出会抛 `OverflowException`；预处理输出中的辅助调用在
  `unchecked` 下不抛。该精确语义不在本设计范围内；
- 上述要求保证不会出现“原代码不抛、预处理后反而抛”的新增异常。

## 11. 不在范围内

- 不产出 Java 代码；
- 不修改主转换器（`CSharpToJava.Core` 的 Java 生成逻辑）；
- 不修改 `config\TypeMappings.json`（条目与 `System.UInt16` 映射修正
  留待后续转换验证时进行）；
- 不处理 `short`/`sbyte`/`char` 自身的原生运算；
- 不实现项目级 checked 溢出语义。

## 12. 参考

- `prompts/unsigned-type-rewriter-prompt.md`
- `docs/superpowers/plans/2026-08-15-unsigned-type-rewriter.md`
- `config/TypeMappings.json` 的 `typeMappings`/`methodMappings` 格式
- `src/CSharpToJava.TypeMapping/TypeMappingRegistry.cs`
