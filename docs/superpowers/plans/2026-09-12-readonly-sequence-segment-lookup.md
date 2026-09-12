# Fix Plan: ReadOnlySequence 段查找退化为 O(N²)

## 问题描述

从 C# 转换来的 `System.Text.Json` 测试套件中，`Utf8JsonReaderTests.testJsonReaderLargeUtf8SegmentSizeOne`
的 4 个大数据量参数耗时异常：

| 参数 | 数据 | Java 耗时 | C# 原始耗时（同负载实测） |
|------|------|-----------|--------------------------|
| `[16]` | non-compact, Json400KB | 94.466s | ~0.07s |
| `[8]` | compact, Json400KB | 58.653s | — |
| `[13]` | non-compact, ProjectLockJson | 24.271s | — |
| `[5]` | compact, ProjectLockJson | 14.170s | — |

这一个测试方法的 4 个参数合计 191.6s，占整个套件 345.2s 的 55%。

## 根因分析

`ReadOnlySequence<T>` 是 C# `System.Buffers.ReadOnlySequence<T>` 的 compat 替身。
C# 中它是 `ReadOnlySequenceSegment<T>` 的**真链表**，`TryGet` 直接从
`SequencePosition` 持有的 segment 对象引用读出内存，`Next` 只跟一个指针，**均为 O(1)**。

compat 版把 segment 链**物化**成数组，并用**字节偏移**表示 `SequencePosition`
（见 `SequencePosition.java` 注释 "The multi-segment sequence model is not simulated;
positions are opaque tokens"）。于是 `tryGet` 每次都要把偏移**反查**回 segment：

```java
// 修复前
private int findSegment(int offset) {
    for (int i = 0; i < segments.length; i++) {          // 每次都从 0 开始线性扫描
        if (offset >= runningIndex[i] && offset < runningIndex[i + 1]) return i;
    }
    return segments.length;
}
```

调用方 `Utf8JsonReader.getNextSpan()`（生成的 `Utf8JsonReader.java`）**每跨一个 segment
就调用一次 `tryGet(..., advance: true)`**。而 `JsonTestHelper.GetSequence(data, 1)` 会构造
**每字节一个 segment** 的链，于是读 N 字节 = N 次推进，第 k 次推进扫描 k 项：

```
总代价 = Σk = N²/2
```

### 实测证据（转换后的测试方法，受控尺寸）

| payload | C# (.NET 10 BCL) | Java 修复前 | Java 修复后 |
|---------|------------------|-------------|-------------|
| 9,961 B | 2.2 ms | 41.2 ms | 16.3 ms |
| 39,961 B | 5.5 ms | 405.7 ms | 32.8 ms |
| 79,981 B | 12.3 ms | 1,496.7 ms | 46.4 ms |
| 159,961 B | 21.0 ms | 12,481.3 ms | 64.0 ms |
| 319,981 B | 53.9 ms | 56,600.4 ms | 111.5 ms |

C# 每翻倍 ≈ ×2.2（线性），Java 修复前每翻倍 ≈ ×3.7~4.5（平方）。
用平方模型从 320KB 外推 400KB：`56,600 × (414472/319981)² = 94,935 ms`，
与 surefire 实测 **94,466 ms 相差 0.5%**，根因确认。

## 修复方案

`runningIndex` 天然单调不减，把线性扫描改为**二分查找**，语义完全等价：

```java
private int findSegment(int offset) {
    if (offset < 0) {
        return 0;
    }
    int low = 0;
    int high = segments.length - 1;
    int candidate = -1;
    while (low <= high) {
        int mid = (low + high) >>> 1;
        if (runningIndex[mid] <= offset) {
            candidate = mid;
            low = mid + 1;
        } else {
            high = mid - 1;
        }
    }
    // candidate 是起始位置不晚于 offset 的最后一个 segment；
    // 仅当 offset 未越过下一个 segment 起点时它才包含该 offset。
    if (candidate < 0 || offset >= runningIndex[candidate + 1]) {
        return segments.length;
    }
    return candidate;
}
```

**为什么选二分查找而不是"记住上次索引的游标"缓存**：二分查找不引入新的可变状态，
对任意访问顺序都成立（测试 `tryGet_resolvesOffsetsOutOfOrder` 锁定了这一点），
而游标缓存只对单调访问成立，且会让本类更加不可重入。

**未改动**：`tryGet` 返回整个 segment 而非从偏移处切片，这与 .NET 有细微差异，
但 `Utf8JsonReader` 只在 segment 边界查询，故不在本次范围内。

## 单元测试

新增 `src/test/java/io/github/ningpp/compat/ReadOnlySequenceSegmentLookupTest.java`（11 个用例）。

按 `Agent.md` 规则 4（测试先行），先写测试并确认失败：

```
[ERROR] ReadOnlySequenceSegmentLookupTest.fullTraversalOfPerByteChainStaysLinear
  walking 300000 single-byte segments took 22158ms, budget is 2000ms
```

- **语义测试（10 个）**：逐字节链全偏移解析、混合段长链全偏移解析、段内空 segment 跳过、
  前导空 segment、末尾/越界返回 false、乱序偏移、`advance=false` 不动位置、
  外来/缺失 position 回退到首段、单 segment 兼容路径。
- **复杂度回归测试（1 个）**：`fullTraversalOfPerByteChainStaysLinear`
  对 300,000 个单字节 segment 做完整 `tryGet` 推进。构建不计时，只计时推进循环，
  断言 < 2000ms（实测：线性扫描 22,158ms，二分查找 16ms；两侧余量分别为 11× 和 125×）。

## 验证步骤与结果

1. `mvn -f java/csharptojava-compat/pom.xml install`
   → **1702 个测试全部通过**，其中新增的 `ReadOnlySequenceSegmentLookupTest` 11/11 通过。
2. `system-text-json-tests` 目标测试 `Utf8JsonReaderTests#testJsonReaderLargeUtf8SegmentSizeOne`
   → 16/16 通过，整类 **0.532s**（修复前 4 个慢参数合计 191.6s）：

   | 参数 | 修复前 | 修复后 | 提升 |
   |------|--------|--------|------|
   | `[5]` | 14.170s | 0.096s | 148× |
   | `[8]` | 58.653s | 0.120s | 489× |
   | `[13]` | 24.271s | 0.042s | 578× |
   | `[16]` | 94.466s | 0.101s | 935× |

3. `system-text-json-tests` 全量套件（7904 个用例）A/B 对照 —— 同一份源码，只切换
   `findSegment` 的实现：

   | | 线性扫描（修复前） | 二分查找（修复后） |
   |---|---|---|
   | `Utf8JsonReaderTests` 耗时 | 208.3 s | **12.15 s**（17×） |
   | 全量结果 | 7904 run, 435 F, 460 E | 7904 run, **435 F, 460 E** |
   | 失败用例集合 | 551 个 | **551 个（完全相同）** |

   **新增失败 0 个，回归 0 个。**

### 关于套件中既有的 895 个失败/错误

`system-text-json-tests` 全量运行本来就不是绿的：工作区中 2026-09-12 08:35 的
`mvn-build.log` 记录了修复前的 `Tests run: 7904, Failures: 442, Errors: 487`。
这些失败分布在 `CollectionTests`、`PropertyVisibilityTests`、`Utf8JsonWriterTests` 等
大量与 `ReadOnlySequence` 无关的类中，属于 C#→Java 转换的既有缺口，**不是本次改动引入的**，
也不在本次范围内。对照实验证明本次改动既不增加也不减少这些失败。

## 遗留（本次未处理）

1. `ReadOnlySequence.getFirst()` 在单 segment 路径上做 `Arrays.copyOfRange` 全量拷贝。
   当前只在 `Utf8JsonReader` 构造函数中调用一次，未进入热路径；若将来进入循环即为新的 O(N²) 隐患。
2. `ReadOnlySequence<T>`/`ReadOnlyMemory<T>`/`ReadOnlySpan<T>` 用 `Integer` 装箱字节，
   以及 `getNextSpan` 每次推进分配 2~3 个 `ObjectHolder` —— 均为常数项开销，不影响复杂度。
3. 本次修复只解决 `testJsonReaderLargeUtf8SegmentSizeOne`。
   `JsonDocumentTests.readTooPreciseDouble`(84.3s) 与 `ValueTests.veryLongInputString`(36.8s)
   是独立问题，需另行分析。
