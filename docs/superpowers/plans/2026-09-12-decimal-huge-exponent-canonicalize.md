# Fix Plan: Decimal.canonicalize 大指数退化为 O(10^|exponent|)

## 问题描述

`system-text-json-tests` 全量套件（7904 个用例）中最慢的用例是
`dotnet.text.json.Tests.JsonDocumentTests.readTooPreciseDouble`，surefire 实测 **56.039s**，
占全部用例累计耗时（92.9s）的 60%。同一份测试在 C# 侧（`D:\csharpjson`，
`dotnet test --filter ReadTooPreciseDouble`）只需 **18ms**，相差约 3100 倍。

用例本体只是一个边界测试，输入 `"    1e+100000002"`：

```csharp
using (JsonDocument doc = JsonDocument.Parse("    1e+100000002"))
{
    JsonElement root = doc.RootElement;
    Assert.False(root.TryGetDecimal(out decimal decimalVal));   // ← 第一次
    ...
    Assert.Throws<FormatException>(() => root.GetDecimal());     // ← 第二次
}
```

期望行为是「decimal 装不下这个数，转换失败」。C# 用 0.1ms 得出这个结论，Java 却要 40s。

## 根因分析

### 调用链

```
JsonElement.tryGetDecimal
  → JsonDocument.tryGetValue_erasure_12                     JsonDocument.java:494
    → io.github.ningpp.compat.Utf8Parser.tryParseDecimal    Utf8Parser.java:327
        Decimal.parse 失败（NumberStyles.Number 不允许指数）
        但文本含 'e' → Decimal.parseExponent                Utf8Parser.java:341
          → new Decimal(new BigDecimal("1e+100000002"), true)   Decimal.java:240
              // unscaled = 1, scale = -100000002
              → canonicalize(value)                          Decimal.java:549
                  if (normalized.scale() < 0)
                      normalized = normalized.setScale(0);   ← 元凶
              → validateRange(...)                           → 抛 overflow，前面 40s 全白算
```

`BigDecimal.setScale(0)` 在 scale 为负时必须把 `1 × 10^100000002` 真的算出来：
JDK 对这种量级的 10 的幂没有缓存（`BigDecimal` 只缓存小指数），只能走
`BigInteger.TEN.pow(100000002)` 反复平方。实测该 BigInteger 的
`unscaledValue.bitLength = 332,192,817`（≈ 39.6 MiB），耗时 **40.5s**。
用例里 `TryGetDecimal` 和 `GetDecimal` 各走一次，于是 2 × 40s。

镜像分支同样有问题：`1e-100000002` 的 scale = +100000002 > 28 → `setScale(28, HALF_EVEN)`
要先构造 10^99,999,974 才能把它舍入。实测 **42.8s**，最终结果却是 0。

### 阶梯实测（`Utf8Parser.tryParseDecimal`，单次调用）

| 输入 | Java 修复前 | 结果 |
|------|-------------|------|
| `1e+28` | 8.4 ms | true |
| `1e+29` | 0.039 ms | false |
| `1e+200` | 0.268 ms | false |
| `1e+100000002` | **40,389 ms** | false |
| `1e-100000002` | **42,761 ms** | true（0E-28） |

代价随 `10^|exponent|` 的位数增长 —— 指数只要够大（不论正负）就会触发，
不是这一个用例的特例。

### 与 C# 的语义对照（.NET 10 实测，同一组输入）

| 输入 | C# 结果 / 耗时 | Java 修复前 结果 / 耗时 |
|------|----------------|--------------------------|
| `1e+28` | true / 0.0007 ms | true / 8.4 ms |
| `1e+29` | false / 0.0002 ms | false / 0.039 ms |
| `7.9228162514264337593543950335e+28` | true / 0.0003 ms（= decimal.MaxValue） | true（修复后） |
| `7.9228162514264337593543950336e+28` | false / 0.0004 ms | false |
| `1e+100000002` | **false / 0.0002 ms** | **false / 40,389 ms** |
| `1e-100000002` | **true / 0.0003 ms**（0E-28） | **true / 42,761 ms** |
| `5e-29` / `6e-29` | true / 0.0002 ms（0E-28 / 1E-28） | 同左 |

**结论：判定结果逐条一致，只有代价错了。** compat 层的语义抄对了，
问题只在「用无界 BigDecimal 建模定长 decimal」时多了一步急切归一化。

### 为什么 C# 是 O(1)（`E:\runtime-release-5.0\src\libraries` 源码）

| 步骤 | 位置 | 关键点 |
|------|------|--------|
| JsonDocument 取 decimal | `System.Text.Json\...\Document\JsonDocument.cs:626-639` | `Utf8Parser.TryParse(segment, out decimal tmp, out int bytesConsumed, standardFormat)` |
| 解析选项 | `System.Private.CoreLib\...\Utf8Parser.Decimal.cs:28-53` | `'e'` → `AllowExponent`；数字缓冲是 `stackalloc byte[DecimalNumberBufferLength]` = **固定 31 字节**（`Number.NumberBuffer.cs:13`） |
| 指数处理 | `Utf8Parser.Number.cs:261-314` | 指数按 `uint` 解析后**饱和折叠进 `number.Scale`（int）**：溢出时直接 `= int.MaxValue` / `int.MinValue` |
| **决定性判断** | `Number.Parsing.cs:1574-1575` | `if (e > DecimalPrecision) return false;`，`DecimalPrecision = 29`（`Number.Formatting.cs:244`） |
| 反向分支 | `Number.Parsing.cs:1662-1675` | `if (e > 0) return false;` / `if (e <= -DecimalPrecision) value = new decimal(0,0,0,sign, 28); return true;` |

C# `decimal` 是 96 位尾数 + 1 字节 scale，`TryNumberToDecimal` 里所有 ×10 的循环
都被 `low64 >= ulong.MaxValue / 10`、`high < uint.MaxValue / 10` 这类溢出条件封顶，
最多跑 ~29/58 次就退出 —— 「乘 10^k」的工作量被尾数宽度**物理封顶**，
永远不会生成「10^100000002」这个对象。compat 层用 `BigDecimal` 就没有这个上限。

## 修复方案

两处 O(1) 前置判断，都只跳过「已经注定」的计算，不改变任何可观测结果：

```java
// 1) 归一化之前的溢出预判（对应 .NET 的 e > DecimalPrecision）
private static void checkIntegerDigits(BigDecimal value) {
    if (value.signum() == 0) {
        return;                                        // 零在任何 scale 下都可表示
    }
    long integerDigits = (long) value.precision() - value.scale();
    if (integerDigits > MAX_INTEGER_DIGITS) {          // 29
        throw new ArithmeticException("Decimal overflow.");
    }
}

// 2) scale > 28 时先判断"必然舍入为 0"（对应 .NET 的 e <= -DecimalPrecision）
if (normalized.scale() > MAX_SCALE) {
    long integerDigits = (long) normalized.precision() - normalized.scale();
    if (integerDigits <= -(MAX_SCALE + 1)) {           // |value| < 10^-29
        return new BigDecimal(BigInteger.ZERO, MAX_SCALE);   // 0E-28
    }
    normalized = normalized.setScale(MAX_SCALE, RoundingMode.HALF_EVEN);
}
```

### 为什么语义不变

- `precision() - scale()` 是「小数点左边的位数」，且 `|value| < 10^(precision - scale)`。
  该值 > 29 时 `|value| ≥ 10^29 > decimal.MaxValue`，旧路径经 `canonicalize` 后
  必然在 `validateRange()` 抛 `ArithmeticException("Decimal overflow.")` ——
  新代码抛的是**同一个类型、同一句消息**，只是提前 40s。
- `precision() - scale() ≤ -29` 时 `|value| < 10^-29 < 0.5×10^-28`，舍入到 28 位小数
  必然是 0；旧路径 `setScale(28, HALF_EVEN)` 产出的也正是 `0E-28`。
  边界取严格不等号：`5e-29`（= 精确 tie，HALF_EVEN 进 0）与 `6e-29`（进 1E-28）
  的 `precision - scale` 都是 -28，不走捷径，仍由原路径决定。
- 判断放在 `validate == true` 的路径上（`new Decimal(value, false)` 只被
  `ZERO/ONE/MINUS_ONE/MAX_VALUE/MIN_VALUE` 五个常量使用，scale 均 ≥ 0），
  因此连「跳过校验」的内部用法也不受影响。
- 修掉捷径后负 scale 分支的展开量被 `checkIntegerDigits` 限制在 10^28 以内，
  不再存在无界展开。

### 边界情况：零 + 极端指数（自查发现的坑）

`BigDecimal.precision()` 对 0 返回 1，所以单纯用「整数位数 > 29」判定会把
`0E+100000002`（unscaled = 0、scale = -100000002）误判成溢出 —— 而零在任何 scale
下都可表示。.NET 实测对 `0e+100000002` 返回 **true + 0**、对 `0e-100000002` 返回
true + `0E-28`（`TryNumberToDecimal` 一开头发现 digit buffer 为空就走零分支，
根本不看 scale）。所以预判对 `signum() == 0` 直接放行，这也恰好让 Java 与 .NET 一致：

| 输入 | C# / .NET 10 | Java（本修复） |
|------|--------------|----------------|
| `0e+100000002` | true，`0` | true，`0` |
| `0e+29` / `0e+30` | true，`0` | true，`0` |
| `0e-100000002` | true，`0.0000000000000000000000000000` | true，同值 |

JDK 的 `BigDecimal.setScale()` 对零本身就有 fast path
（实测 `new BigDecimal("0E+100000002").setScale(0)` 为 0.005ms），放行不会触发大数展开。

## 单元测试

按 `Agent.md` 规则 4（测试先行），先写测试、用负向对照确认它们确实抓得住这个 bug，
再落实现（对照结果见下）。

| 文件 | 用例 | 覆盖 |
|------|------|------|
| `DecimalTest`（补充 4 个） | `parseExponent_boundariesMatchCSharpDecimalRange` | `1e+28` 接受 / `1e+29`、`1e+30` 拒绝 / `MaxValue` 与 `MaxValue+1` 边界 / `valueOf(BigDecimal)` 入口 |
| | `parseExponent_hugeNegativeExponentUnderflowsToZero` | `1e±100000002` → `0E-28`；`1e-29`、`5e-29` → 0；`6e-29` → `1E-28` |
| | `parseExponent_zeroWithExtremeExponentStaysZero` | 零 + 极端正/负指数不得被判成溢出 |
| | `parseExponent_hugeExponentsResolveInConstantTime` | 复杂度回归：9 个极端指数 + 1000 位整数，整批 < 5s |
| `Utf8ParserDecimalTest`（新增 5 个） | `tryParseDecimal_reportsBytesConsumedForRepresentableValues` | 真实调用点语义：`bytesConsumed` 供 `JsonDocument` 做 `segment.Length == bytesConsumed` 校验 |
| | `tryParseDecimal_rejectsPositiveExponentsBeyondDecimalRange` | `1e+29` / `MaxValue+1` / `-1e+100000002` → false |
| | `tryParseDecimal_underflowsHugeNegativeExponentsToZero` | `±1e-100000002` → true 且值为 `0.0000000000000000000000000000` |
| | `tryParseDecimal_zeroSurvivesExtremeExponents` | `0e±100000002` → true，值为 0 / `0E-28` |
| | `tryParseDecimal_hugeExponentsResolveInConstantTime` | 复杂度回归：8 个极端指数整批 < 5s |

所有期望值都取自 .NET 10 实测（`Utf8Parser.TryParse(utf8, out decimal, out int, 'e')`），
不是从 Java 现有行为反推的。

超时断言用非抢占式 `assertTimeout`：一旦有人重新引入 O(10^n)，
测试会在跑完（约 4 分钟）后失败，而不是留下一个无法中断的 BigInteger 计算线程。

### 负向对照（确认测试真的抓得住）

把 `Decimal.java` 用 `git stash` 临时还原成修复前的实现，只跑复杂度回归用例：

```
$ mvn -o test -Dtest='Utf8ParserDecimalTest#tryParseDecimal_hugeExponentsResolveInConstantTime'
[ERROR] Tests run: 1, Failures: 1, Errors: 0, Skipped: 0, Time elapsed: 171.9 s <<< FAILURE!
[ERROR]   Utf8ParserDecimalTest.tryParseDecimal_hugeExponentsResolveInConstantTime:78
          execution exceeded timeout of 5000 ms by 166855 ms
```

修复前该批次耗时 **171.9s**（6 个输入里有 4 个各花约 40s），修复后 **3ms**。
对照完成后 `git stash pop` 恢复实现（`git status` 确认 `M Decimal.java` 已回来）。

## 验证步骤与结果

1. `mvn -o test -Dtest=DecimalTest,Utf8ParserDecimalTest`
   → **23 个用例全部通过**（`DecimalTest` 18 个 0.058s、`Utf8ParserDecimalTest` 5 个 0.005s）。
2. `mvn -o install` → **1711 个测试全部通过**（修复前 1702 + 本次新增 9），
   jar 已安装到 `d:\mvnrepo\io\github\ningpp\csharptojava-compat\1.0-SNAPSHOT\`。
3. `system-text-json-tests` 复跑目标用例
   （`mvn -o -f system-text-json-tests/pom.xml test -Dtest=JsonDocumentTests#readTooPreciseDouble`）：

   | | 修复前 | 修复后（单跑） | 修复后（全量套件内） |
   |---|---|---|---|
   | `readTooPreciseDouble`（surefire XML `time`） | 56.039 s | 0.057 s | **0.003 s** |
   | `JsonDocumentTests` 整类 | 56.431 s | 0.102 s | 0.747 s |

4. `system-text-json-tests` 全量套件（7904 用例）与 2026-09-12 12:47 的 surefire 基线对照：

   | | 修复前（12:47 基线） | 修复后 |
   |---|---|---|
   | 结果 | 7904 run, 435 F, 460 E, 16 S | 7904 run, **435 F, 460 E, 16 S** |
   | 用例累计耗时 | 92.9 s | **68.4 s** |
   | 最慢用例 | `readTooPreciseDouble` 56.039 s | `veryLongInputString(int)[2]` 21.6 s |

   **新增失败 0 个，回归 0 个。** 套件里既有的 895 个失败/错误分布在
   `CollectionTests`、`PropertyVisibilityTests` 等与 decimal 无关的类中，属于转换既有缺口。

5. 与本次改动无关的热点交叉验证：全量套件里 `ValueTests.veryLongInputString[2]/[1]`
   （21.6s / 14.6s，各自分配约 2GB 字符串）与 `Utf8JsonWriterTests.writeNumbers[*]`
   在当前机器状态下比 12:47 基线慢约 2×。为排除是本次改动引入，做了 old/new jar 对照
   （`git stash` 掉修复 → `mvn -o install -DskipTests` → 跑测试 → 恢复）：

   | | OLD jar | NEW jar |
   |---|---|---|
   | `ValueTests` 整类 | 32.916 s（40 tests，6 F / 1 E） | 32.819 s（40 tests，**6 F / 1 E**） |
   | `veryLongInputString[2]` | 19.923 s | 19.900 s |
   | `veryLongInputString[1]` | 12.642 s | 12.561 s |
   | `writeNumbers` 4 个参数 | 0.494 ~ 0.551 s | 0.516 ~ 0.601 s |

   两侧在噪声范围内一致，差异来自机器状态而非本次改动（这两个热点也都不经过
   `Decimal` 的构造路径）。本次改动只影响 decimal 归一化，收益是把
   `readTooPreciseDouble` 从 56.0s 降到 0.003s。

## 遗留（本次未处理）

1. `Utf8Parser.tryParseDecimal` 在失败分支不回写 `bytesConsumed` / `value`；
   真实调用点都传新 holder，不影响 `JsonDocument`，但与其他 `tryParseXxx` 的
   写回约定不一致。
2. `spanToString(...).trim()` 会让带空白的输入报出偏小的 `bytesConsumed`；
   真实调用点传入的是精确的数字 token，暂不触发。
3. `Decimal` 用 `BigDecimal` 建模 C# decimal，`equals/hashCode` 走
   `stripTrailingZeros`，与 C# 的位级相等仍有细微差异，不在本次范围。
