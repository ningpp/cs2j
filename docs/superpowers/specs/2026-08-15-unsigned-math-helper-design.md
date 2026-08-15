# unsigned-type 预处理改为静态 UnsignedMath 工具类

日期：2026-08-15

## 1. 背景与目标

`unsigned-type` 是 C#→C# 前置转换，目的是把 C# 项目里
`byte`/`ushort`/`uint`/`ulong` 相关的类型、运算、比较、移位和转换收敛成
统一、可编译、语义等价的 C#，从而让后续 cs2j 主转换器不再面对大量
unsigned、边界和混合类型分支，只需通过补充 `config\TypeMappings.json`
映射调用点即可正确转换。

现状（扩展方法方案）的问题：

- 调用是接收者风格 `a.Add(b)`，`TypeMappings.json` 的
  `(type, method, signature) → javaMethod` 机制难以直接映射；
- 辅助文件按项目复制多份，Java 侧会产生重复类；
- 辅助文件内部仍大量使用 unsigned 关键字和扩展方法语法。

目标：

- 把涉及 unsigned 类型的运算/比较/移位/转换/混合运算全部改写为
  `Cs2jUnsigned.UnsignedMath.XXX(...)` 全限定静态调用；
- 混合有符号/无符号运算必须显式插入归一化调用，不依赖隐式转换；
- 辅助文件物理上只生成一份，各项目通过 `Compile Include`/`Link` 引用；
- 中间 C# 必须可编译、可运行且与原代码语义一致；
- 本阶段不产出 Java、不改主转换器。

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
- 一元 `~`、`-`（仅 uint，C# 结果为 long）
- 前/后置 `++` / `--`
- 复合赋值 `+= -= *= /= %= &= |= ^= <<= >>=`
- 显式强制转换（源或目标为上述 unsigned 类型）
- 混合有符号/无符号运算（如 `ushort_var + int_var`、
  `uint_var + int_var`、`byte_var + uint_var`、`uint_var + ulong_var`）

不处理：

- `int`/`long`/`short`/`sbyte`/`char` 自身（非 unsigned）的原生运算；
- 常量表达式（const 字段/局部变量、枚举值、特性参数、默认参数、
  case 标签、自动属性初始化器）保持原生运算符，避免 CS0133；
- `float`/`double`/`decimal`；
- 字符串、数组、集合等与 unsigned 无关的语法。

## 3. 生成物

### 3.1 辅助文件

- 文件名：`_Cs2jUnsignedMath.cs`
- 内容：

```csharp
namespace Cs2jUnsigned;

internal static class UnsignedMath
{
    // 全部方法见 §4；实现以 int/long、位掩码和原生 int/long 运算为主；
    // 个别无符号 64 位运算（DivideUInt64/RemainderUInt64/RightShiftUInt64）
    // 可在支撑文件内部使用 System.UInt64/BigInteger，这些内部构造不进入
    // Java 输出（见 §3.3 支撑文件约定）。
}
```

- 物理位置：`.cs2j-unsigned-src` 根目录，只生成一份。
- 每个实际编译被改写源文件的项目，在其 csproj 中追加：

```xml
<Compile Include="..\_Cs2jUnsignedMath.cs" Link="_Cs2jUnsignedMath.cs" />
```

（相对路径按项目目录层级计算。）这样每个程序集编译自己的
`internal` 副本，避免跨程序集依赖（包括 `System.Private.Uri`
这类框架程序集名在运行时无法被本地版本遮蔽的问题）。

### 3.2 调用形态

- 不再注入 `using Cs2jUnsigned;`；
- 所有改写点输出全限定静态调用：
  `Cs2jUnsigned.UnsignedMath.Method(operand...)`；
- 调用点可编译：uint/ulong 操作数进入 int/long 参数时，重写器插入
  位保持的显式 C# 转换 `(int)` / `(long)`（Java 侧对应 no-op 转换）。

### 3.3 支撑文件约定

`_Cs2j*` 前缀文件是转换支撑文件。后续主转换器应跳过其类定义转换，
只通过 `TypeMappings.json` 映射调用点；本阶段不改主转换器，仅在
文档中记录该契约（见 §7）。

## 4. 辅助方法目录

约定：`int` 表示 32 位位模式，`long` 表示 64 位位模式；
`UInt32` 域方法把 `int` 参数按无符号 32 位解释；
`UInt64` 域方法把 `long` 参数按无符号 64 位解释。

### 4.1 int 域（byte/ushort 提升后的结果，以及与 int 混合）

| 方法 | C# 签名 | 实现要点 |
| --- | --- | --- |
| Add | `int Add(int a, int b)` | `a + b` |
| Subtract | `int Subtract(int a, int b)` | `a - b` |
| Multiply | `int Multiply(int a, int b)` | `a * b` |
| Divide | `int Divide(int a, int b)` | `a / b` |
| Remainder | `int Remainder(int a, int b)` | `a % b` |
| And | `int And(int a, int b)` | `a & b` |
| Or | `int Or(int a, int b)` | `a \| b` |
| Xor | `int Xor(int a, int b)` | `a ^ b` |
| LeftShift | `int LeftShift(int a, int n)` | `a << (n & 31)` |
| RightShift | `int RightShift(int a, int n)` | `a >> (n & 31)` |
| Compare | `int Compare(int a, int b)` | -1/0/1 |

### 4.2 UInt32 域

| 方法 | C# 签名 | 实现要点 |
| --- | --- | --- |
| AddUInt32 | `int AddUInt32(int a, int b)` | `a + b`（回绕） |
| SubtractUInt32 | `int SubtractUInt32(int a, int b)` | `a - b`（回绕） |
| MultiplyUInt32 | `int MultiplyUInt32(int a, int b)` | `a * b`（回绕） |
| DivideUInt32 | `int DivideUInt32(int a, int b)` | `(int)((a & 0xFFFFFFFFL) / (b & 0xFFFFFFFFL))` |
| RemainderUInt32 | `int RemainderUInt32(int a, int b)` | `(int)((a & 0xFFFFFFFFL) % (b & 0xFFFFFFFFL))` |
| AndUInt32 | `int AndUInt32(int a, int b)` | `a & b` |
| OrUInt32 | `int OrUInt32(int a, int b)` | `a \| b` |
| XorUInt32 | `int XorUInt32(int a, int b)` | `a ^ b` |
| NotUInt32 | `int NotUInt32(int a)` | `~a` |
| LeftShiftUInt32 | `int LeftShiftUInt32(int a, int n)` | `a << (n & 31)` |
| RightShiftUInt32 | `int RightShiftUInt32(int a, int n)` | `(int)((a & 0xFFFFFFFFL) >> (n & 31))`（逻辑右移） |
| CompareUInt32 | `int CompareUInt32(int a, int b)` | 无符号比较 -1/0/1 |

### 4.3 UInt64 域

| 方法 | C# 签名 | 实现要点 |
| --- | --- | --- |
| AddUInt64 | `long AddUInt64(long a, long b)` | `a + b`（回绕） |
| SubtractUInt64 | `long SubtractUInt64(long a, long b)` | `a - b`（回绕） |
| MultiplyUInt64 | `long MultiplyUInt64(long a, long b)` | `a * b`（回绕） |
| DivideUInt64 | `long DivideUInt64(long a, long b)` | 无符号 64 位除法（见下） |
| RemainderUInt64 | `long RemainderUInt64(long a, long b)` | 无符号 64 位取余（见下） |
| AndUInt64 | `long AndUInt64(long a, long b)` | `a & b` |
| OrUInt64 | `long OrUInt64(long a, long b)` | `a \| b` |
| XorUInt64 | `long XorUInt64(long a, long b)` | `a ^ b` |
| NotUInt64 | `long NotUInt64(long a)` | `~a` |
| LeftShiftUInt64 | `long LeftShiftUInt64(long a, int n)` | `a << (n & 63)` |
| RightShiftUInt64 | `long RightShiftUInt64(long a, int n)` | 逻辑右移（见下） |
| CompareUInt64 | `long CompareUInt64(long a, long b)` | 无符号比较 -1/0/1 |

`DivideUInt64`/`RemainderUInt64`/`RightShiftUInt64` 的实现可以
在支撑文件内部使用 `System.UInt64` 或 `System.Numerics.BigInteger`
保证 C# 侧正确性；这些内部构造不会进入 Java 输出（支撑文件约定），
Java 侧调用点由 TypeMappings 映射到 `Long.divideUnsigned` 等原生方法。

### 4.4 转换方法

只保留 Java 无法用原生转换等价表达的宽度转换：

| 方法 | C# 签名 | 语义 |
| --- | --- | --- |
| ToByte | `int ToByte(int v)` | `v & 0xFF` |
| ToByte | `int ToByte(long v)` | `(int)v & 0xFF` |
| ToUInt16 | `int ToUInt16(int v)` | `v & 0xFFFF` |
| ToUInt16 | `int ToUInt16(long v)` | `(int)v & 0xFFFF` |
| ToInt64UInt32 | `long ToInt64UInt32(int v)` | `v & 0xFFFFFFFFL`（uint→long 无符号扩展） |
| ToInt32UInt64 | `int ToInt32UInt64(long v)` | `(int)v`（ulong→int 取低 32 位） |

位保持的转换（int↔uint、long↔ulong、`(uint)long`、`(ulong)int`
等）保持原生 C# 转换，Java 侧是 no-op 或标准截断，不进入辅助类。

## 5. 重写规则

### 5.1 C# 整数提升基准

重写器按 Roslyn 语义模型得到的“操作数类型 + 提升后结果类型”归类：

- `byte`/`ushort` 与 `sbyte`/`short`/`char`/`int` 混用 → 结果 `int`
  （int 域）；
- `uint` 与 `sbyte`/`short`/`int` 混用 → 结果 `long`
  （long 域，`ToInt64UInt32` 归一化 uint 侧）；
- `uint` 与 `byte`/`ushort`/`char`/`uint` 混用 → 结果 `uint`
  （UInt32 域）；
- 任一操作数为 `ulong` → 结果 `ulong`（UInt64 域）。

### 5.2 同型/提升后为 int 的运算

示例（`b1,b2` 为 byte，`s` 为 ushort，`i` 为 int）：

```csharp
b1 + b2       → UnsignedMath.Add(b1, b2)
s + i         → UnsignedMath.Add(s, i)
b1 - i        → UnsignedMath.Subtract(b1, i)
s << 3        → UnsignedMath.LeftShift(s, 3)
b1 == b2      → UnsignedMath.Compare(b1, b2) == 0
s <= i        → UnsignedMath.Compare(s, i) <= 0
```

byte/ushort→int 的提升在 C# 与 Java 两侧一致，调用点直接传参即可。

### 5.3 UInt32 / UInt64 域

示例（`u,u1,u2` 为 uint，`ul,ul1,ul2` 为 ulong）：

```csharp
u1 + u2       → UnsignedMath.AddUInt32((int)u1, (int)u2)
u1 / u2       → UnsignedMath.DivideUInt32((int)u1, (int)u2)
u1 < u2       → UnsignedMath.CompareUInt32((int)u1, (int)u2) < 0
u >> 4        → UnsignedMath.RightShiftUInt32((int)u, 4)
~u            → UnsignedMath.NotUInt32((int)u)
ul1 + ul2     → UnsignedMath.AddUInt64((long)ul1, (long)ul2)
ul1 == ul2    → UnsignedMath.CompareUInt64((long)ul1, (long)ul2) == 0
```

`(int)u`/`(long)ul` 是位保持转换，Java 侧是 no-op。

### 5.4 混合运算（必须显式插入归一化）

示例（`s` 为 ushort，`i` 为 int，`l` 为 long，`u` 为 uint，`ul` 为 ulong）：

```csharp
u + i         → UnsignedMath.Add(UnsignedMath.ToInt64UInt32((int)u), i)      // 结果 long
u + l         → UnsignedMath.Add(UnsignedMath.ToInt64UInt32((int)u), l)      // 结果 long
s + u         → UnsignedMath.AddUInt32(s, (int)u)                            // 结果 uint
b + u         → UnsignedMath.AddUInt32(b, (int)u)                            // 结果 uint
u + ul        → UnsignedMath.AddUInt64(
                    UnsignedMath.ToInt64UInt32((int)u), (long)ul)             // 结果 ulong
ul + i        → UnsignedMath.AddUInt64((long)ul, i)                          // 结果 ulong
u == i        → UnsignedMath.Compare(UnsignedMath.ToInt64UInt32((int)u), i) == 0
u < ul        → UnsignedMath.CompareUInt64(
                    UnsignedMath.ToInt64UInt32((int)u), (long)ul) < 0
```

规则：只要 C# 发生了 Java 无法直接镜像的提升（uint→long 无符号扩展、
byte/ushort→uint、任一操作数进入 ulong 域等），重写器必须显式插入
对应的转换方法调用，禁止依赖隐式转换把语义留给主转换器推断。

### 5.5 一元与自增自减

```csharp
-u            → -UnsignedMath.ToInt64UInt32((int)u)      // 结果 long；长整型取负是 Java 原生
b++           → b = UnsignedMath.ToByte(UnsignedMath.Add(b, 1))
s--           → s = UnsignedMath.ToUInt16(UnsignedMath.Subtract(s, 1))
u++           → u = (uint)UnsignedMath.AddUInt32((int)u, 1)
ul++          → ul = (ulong)UnsignedMath.AddUInt64((long)ul, 1)
```

后置自增/自减继续沿用现有“先求值、必要时临时变量保存旧值”的模式
（`UnsignedPostfixRewriter` 骨架保留，调用改为静态方法）。

### 5.6 复合赋值

统一改写为“读左值 → 归一化运算 → 写回（必要时截断）”：

```csharp
b += 1        → b = UnsignedMath.ToByte(UnsignedMath.Add(b, 1))
u += i        → u = (uint)UnsignedMath.Add(
                    UnsignedMath.ToInt64UInt32((int)u), i)   // C# 结果是 long
u <<= 4       → u = (uint)UnsignedMath.LeftShiftUInt32((int)u, 4)
ul /= ul2     → ul = (ulong)UnsignedMath.DivideUInt64((long)ul, (long)ul2)
```

仅当左值是局部变量/参数/字段（无副作用、求值一次）时改写；属性、
索引器保持原生，避免 getter/receiver 二次求值（沿用现有约束）。

### 5.7 强制转换

- 目标为 `byte`/`ushort`：`ToByte(...)` / `ToUInt16(...)`；
- `uint → long`：`ToInt64UInt32(...)`；
- `ulong → int`：`ToInt32UInt64(...)`；
- 位保持转换（int↔uint、long↔ulong）保持原生；
- 常量表达式上下文的强制转换保持原生。

### 5.8 不重写的场景

- 纯 `int`/`long` 运算；
- 常量表达式（const 初始化、枚举值、特性参数、默认参数、case 标签、
  自动属性初始化器）内的运算符/转换；
- checked 上下文（沿用现有抑制逻辑，保持溢出行为）；
- 属性/索引器复合赋值与自增自减（沿用现有抑制逻辑）。

## 6. 预处理管线改动

- `UnsignedExtensionsFileContent` → `UnsignedMathFileContent`：
  `FileName = "_Cs2jUnsignedMath.cs"`，内容为 `internal static class
  UnsignedMath`（方法目录见 §4）。
- `UnsignedTypeSyntaxRewriter`：
  - 删除 `using Cs2jUnsigned;` 注入；
  - 二元/一元/复合赋值/自增自减/强制转换改为输出
    `Cs2jUnsigned.UnsignedMath.*` 全限定静态调用；
  - 按 §5 的归类表处理混合运算，显式插入转换调用；
  - `UsedExtensions` 语义改为 `UsedUnsignedMath`（触发辅助文件生成）。
- `ProjectUnsignedTypePreprocessor`：
  - 只写一份根目录 `_Cs2jUnsignedMath.cs`；
  - 对每个实际编译被改写源文件的项目追加
    `<Compile Include="..\_Cs2jUnsignedMath.cs" Link="_Cs2jUnsignedMath.cs" />`
    （不再往项目目录复制本地副本）；
  - 保留“关闭默认 glob / 显式 Compile Include”项目的兼容处理。
- CLI 输出统计行保持 `unsigned-type: ... operators rewritten` 格式。

## 7. TypeMappings 契约（后续补充，不在本阶段实现）

调用点与 TypeMappings 条目的对应示例：

```json
{
  "type": "Cs2jUnsigned.UnsignedMath",
  "method": "AddUInt32",
  "signature": "(System.Int32,System.Int32)",
  "javaMethod": "addUInt32",
  "imports": ["io.github.ningpp.compat.UnsignedMath"]
}
```

契约要点：

- 每个辅助方法一个条目，`type` 固定为 `Cs2jUnsigned.UnsignedMath`；
- `signature` 使用辅助方法自身的 C# 签名，避免同名重载歧义；
- Java 侧 `UnsignedMath` 工具类由 compat 库提供，调用点只依赖
  `javaMethod` + `imports`；
- 主转换器跳过 `_Cs2j*` 支撑文件的类定义转换（后续小改动，
  不在本规格实现范围内）。

## 8. 验证

### 8.1 单元/项目测试

- 更新 `UnsignedTypeRewriterUnitTests`：
  - 断言输出为 `Cs2jUnsigned.UnsignedMath.Add(...)` 等全限定调用；
  - 断言不再注入 `using Cs2jUnsigned;`；
  - 断言辅助文件为 `internal static class UnsignedMath`；
  - 新增混合运算用例：`ushort+int`、`uint+int`、`byte+uint`、
    `uint+ulong`、`uint==int`、`ulong>>n`、`(byte)uint`、`(long)uint`、
    `(int)ulong`、复合赋值、前后置自增自减；
  - 保留常量表达式不重写、checked 上下文不重写、指针解引用/负值
    转型括号等既有回归。
- 更新 `UnsignedTypeProjectTests`：
  - 根目录只有一份 `_Cs2jUnsignedMath.cs`；
  - 各编译项目 csproj 含 `Compile Include="..\_Cs2jUnsignedMath.cs"`
    + `Link`；
  - 保留显式 Compile Include / 跨目录编译项目的回归用例。
- `UnsignedTypeSemanticsTests`：编译并运行原始与改写后代码，断言运行
  结果一致（覆盖混合运算与边界值）。

### 8.2 端到端

- 重新转换 `D:\csharp-sdk`，产物在 `D:\unsigned-81519`；
- 在 `.cs2j-unsigned-src\csharp-sdk.sln` 上执行
  `dotnet test -c Release`，必须零失败；
- 仓库自身 `dotnet test -c Release` 全绿。

## 9. 不在范围内

- 不产出 Java 代码；
- 不修改主转换器（`CSharpToJava.Core` 的 Java 生成逻辑）；
- 不修改 `config\TypeMappings.json`（条目留待后续转换验证时补充）；
- 不处理 `short`/`sbyte`/`char` 自身的原生运算。

## 10. 参考

- `prompts/unsigned-type-rewriter-prompt.md`
- `docs/superpowers/plans/2026-08-15-unsigned-type-rewriter.md`
- `config/TypeMappings.json` 的 `methodMappings` 格式
