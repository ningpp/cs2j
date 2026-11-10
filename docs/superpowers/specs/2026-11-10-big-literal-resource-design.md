# big-literal-resource（词法预处理 #2）设计

- 日期：2026-11-10
- pass id：`big-literal-resource`
- 阶段：`PassStage.Lexical`
- 位置：**第二个 pass**，紧随 `conditional`，锚 `runAfterIds = [conditional]`，
  注册表物理声明紧随 `Conditional`，由 Kahn 声明序破平锁定槽位
  `conditional → big-literal-resource → lexical-normalize`
- 语义模型：不需要（`needsSemanticModel: false`）
- 默认：开启；`--disable-pass big-literal-resource` 关闭

## 1. 问题

`DemoBigBytes.cs`（大量 `byte[]` 等数组）与 `DemoBigStrings.cs`（大量
`string[]` / 单个超长字符串）这类文件常超过 1 MB。它们同时踩两个坑：

1. **转换性能**：1 MB 字面量在语法树里是十几万个表达式节点。链上绝大多数
   语义 pass 每轮都要对整棵树建 compilation 并逐节点绑定，代价随字面量节点数
   线性放大（实测见 §6：`member-desugar` 单 pass 从 <1 s 涨到 487 s）。
2. **转换正确性**：生成的 Java 无法表达这些字面量——单个 `<clinit>` 承载数万
   数组元素会突破 JVM 方法体 64 KB 上限；单个字符串常量超过 65535 字节的
   modified-UTF-8 会突破常量池条目上限。

## 2. 方案

把超限字面量载荷搬出 `.cs` 文件，落到资源文件，调用点改成加载器调用：

```csharp
// before
public static readonly byte[] Data0 = new byte[] { 0x00, 0x01, /* 4 万个 */ };

// after
public static readonly byte[] Data0 =
    global::Cs2jPrelude.Cs2jResource.LoadBytes("cs2j/DemoBigBytes.Data0.bb34e9c2.bin");
```

两侧运行时一致：

- C#：资源以 `EmbeddedResource` + `LogicalName` 编入宿主程序集，
  `Cs2jPrelude.Cs2jResource` 在已加载程序集的清单流里按逻辑名查找；
- Java：`LogicalName` 同时就是 classpath 相对路径，转换器把资源拷到
  `src/main/resources/cs2j/…`，`io.github.ningpp.compat.Cs2jResource`
  用 `getResourceAsStream("/"+name)` 读取。

`Cs2jPrelude.Cs2jResource` 经 `config/TypeMappings.json` 映射到 compat 手写类，
与 `Cs2jArray.ArrayHelper` 等支撑库同一套机制（支撑库工程被过滤，不参与
Java 转换）。

## 3. 覆盖范围

| 形态 | 处理 |
|------|------|
| 一维数组字段/局部变量，元素类型 `byte/short/int/long/float/double/char/bool/string`，全部元素是字面量（可含 `-`、`+`、数值 cast） | 外置 |
| `string` 字段/局部变量，单个字面量 | 外置（`.txt`） |
| `const` 声明 | **不动** + `CS2J4321`：加载器调用不是常量表达式，且无语义模型时无法排除 attribute/case label/默认参数用法 |
| 交错/多维数组（`byte[][]`、`byte[,]`） | 不动（没有单一扁平字节布局） |
| 字面量与表达式混排的初始化器 | 不动 + `CS2J4320`（Info） |
| `sbyte/ushort/uint/ulong` 元素类型 | 不动（后续 `unsigned` pass 负责其 Java 形态） |
| 含未配对代理项的字符串 | 不动 + `CS2J4322`（UTF-8 往返不无损） |

### 3.1 两级阈值（单点阈值 + 文件预算）

单个字面量够大只是失败的一种形态；更常见的是「每个字面量都不大，但一个文件里
堆了成百上千个」。所以决策是两级的：

```text
move(x) = payload(x) >= MinCandidatePayloadBytes            // 256 字节，低于此不值得单独成文件
          && ( payload(x) >= Always(x)                      // 数组 4096 / 字符串 4096
               || fileMovableTotal > MaxInlinePayloadBytes )  // 64 KB
```

- **单点阈值**（数组 `MinArrayPayloadBytes=4096` / 字符串
  `MinStringPayloadBytes=4096`）：解决单个 `<clinit>` 过大、单个 Java 字符串常量
  超 65535 字节。
- **文件预算**（`MaxInlinePayloadBytes=64 KB`）：文件内所有*可外置*候选载荷之和
  超过预算时，该文件**全部**候选（≥ `MinCandidatePayloadBytes`）一起外置。
  一次文件级判定、不搞「按大小贪心到刚好压线」，避免留下任意的「留一半」边界。
- `const` 不计入 `fileMovableTotal`（它无论如何都动不了，计入会让预算永远够不着，
  反而逼着其他字面量无谓外置）；真正会被外置的 `const` 汇总成一条 `CS2J4321`。

对普通代码零扰动：文件内候选总量不到 64 KB 时，行为与只有单点阈值时完全一致
（单点阈值以上的照旧外置，其余一律不动）。

## 4. 载荷格式（C# / Java 逐字节一致）

- 定长元素数组：小端定宽槽位表；宽度按 `byte/bool=1`、`short/char=2`、
  `int/float=4`、`long/double=8`。整数字面量按 64 位二补码槽位写入后截断，
  因此 `-1`、`0x7FFF_FFFF`、`(byte)0x80` 的截断语义与 C# 编译期一致。
- `string[]`：每项 `int32 小端字节数 + UTF-8`。
- `string`：整份文件即 UTF-8 字节。

## 5. 资源命名与注册

- 文件名 = `<Owner>.<Member>.<hash8>.<bin|txt>`，`Owner` 为语法层嵌套类型链
  （局部变量再拼最近的方法名），`hash8` 为载荷 SHA-256 前 8 位 →
  内容寻址、可重复、幂等。
- 物理位置：声明文件同目录下的 `Cs2jResources/`。
- 工程注册：向**每个实际编译该源文件的宿主工程**追加

  ```xml
  <ItemGroup>
    <EmbeddedResource Include="Cs2jResources\X.bin">
      <LogicalName>cs2j/X.bin</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
  ```

  `LogicalName` 必须是**子元素**（`SolutionLoader.ScanProjectResources` 从
  `item.Elements()` 读取），转换期据此把资源拷到
  `src/main/resources/cs2j/`。已存在同名 `LogicalName` 时不再追加（幂等）。
  用 `EmbeddedResource` 而不是 `None + CopyToOutputDirectory`，是因为 SDK 默认
  的 `EmbeddedResource` glob 只覆盖 `**/*.resx`，显式 `Include` 不会触发
  NETSDK1022 重复项错误，SDK / 非 SDK 工程行为一致。宿主集合按
  `ProjectCompileSet` 求值的编译集合判定：共享源文件被多个工程（含
  `<Compile Include="..\X.cs" Link="…"/>` 链接）编译时，每个工程都会收到条目，
  否则其他程序集运行时找不到资源；编译集合无法解析时退回“资源目录最近的
  `.csproj`”启发式。
- 同时释放/引用 `Cs2jPrelude` 支撑工程（与 delegate 等 pass 共用同一套
  幂等注入逻辑，抽到 `SupportLibraryProjectReference`）。

## 6. 实测

语料：`DemoBigBytes.cs` 1.17 MB（`byte[]` 4 万 + 6 万 + 8 万）、
`DemoBigStrings.cs` 1.05 MB（`string[]` 26000 项 + 31798 字符 SQL 字符串）。

处理效果：`1.17 MB → 744 B`、`1.05 MB → 402 B`；载荷 5 个文件共
1017798 字节。

全链 56 pass（`--no-verify-build --no-verify-tests`，Windows / 同机）：

| 配置 | 墙钟 | `member-desugar` 单 pass |
|------|------|--------------------------|
| 开启本 pass | **28.7 s** | < 1 s |
| `--disable-pass big-literal-resource` | **542.6 s** | 487.1 s |

端到端正确性（同一语料）：C# 中间树运行输出 `bytes=180000 words=26000
sql=31798`；转出的 Maven 工程 `mvn compile` 通过，Java 侧运行输出完全相同的
`bytes=180000 words=26000 sql=31798`。

### 6.1 聚合形态（文件预算要解决的问题）

聚合语料 4.47 MB / 3 个文件：2000 个 1 KB 的 `static readonly string`、
200 个 `byte[1000]`、200 个 `int[1000]`。每个字面量都远小于单点阈值，
旧规则下 `-pass` 输出 `0 transformed`。

| 项 | pass 未生效 | 预算生效 |
|----|-------------|----------|
| `DemoManyIntArrays` `.java` | 1,025,575 B → javac **`代码过长` / BUILD FAILURE** | 21,826 B → BUILD SUCCESS，`.class` 17,245 B |
| `DemoManyStrings` `.java` | 2,088,579 B → `.class` 2,060,426 B | 216,625 B → `.class` 168,516 B |
| `DemoManyArrays` `.java` | 281,219 B → `.class` 276,436 B | 21,420 B → `.class` 16,636 B |
| 全链墙钟（含每 pass verify build） | — | 380 s，55 个 pass 合计 365.6 s，最慢单 pass 12.2 s |

`big-literal-resource: 3 transformed, 0 clean, 400 array(s), 2000 string(s),
3000000 payload bytes, 0 skipped`；转换输出拷贝 2400 个资源文件。

### 6.2 JVM 到底限制了什么

「class 总大小」**没有**上限：上面那个 2,060,426 B 的 `DemoManyStrings.class`
是合法且能编译的。真正会炸的是三个 u2/code 级硬上限：

| 约束 | 出处 | 触发后果 |
|------|------|----------|
| 常量池条目 ≤ 65535 | `constant_pool_count` 是 u2 | `常量池过大` / class 读取失败 |
| 单 `CONSTANT_Utf8` ≤ 65535 字节（modified UTF-8，非 ASCII 每字符 2–3 字节） | `CONSTANT_Utf8_info.length` 是 u2 | `常量字符串过长` |
| 单方法字节码 < 65536 字节 | JVMS §4.9.1 `code_length` | `代码过长` |
| 字段/方法数各 ≤ 65535 | `fields_count` / `methods_count` 是 u2 | class 格式错误 |

所以「2000 × 1 KB 字符串」本身**不违规**（每个常量 1 KB，`<clinit>` 约 16 KB，
常量池约 6000 条），它的问题是可用的规模：2 MB 的 `.java` 与 2 MB 的 `.class`。
真正编译不过的是**单方法字节码超 64 KB**（200 个 `int[1000]` 合成一个
`<clinit>` ≈ 20 万条元素赋值）与**单个字符串常量超 65535 字节**。
文件预算两级阈值同时覆盖这两类，并要求「文件因为字面量而变大」时整份清空。

## 7. 诊断码（CS2J43xx）

`CS2J4301/4302/4303`（restore/build/test 失败）、`CS2J4310`（每个文件一条汇总
Info，verbose 才输出，触发预算时附 `file budget tripped: N > M`）、`CS2J4320`
（混排未外置，Info）、`CS2J4321`（该文件有多少 `const` 本该外置但动不了，汇总一条）、
`CS2J4322`（未配对代理项）、`CS2J4330`（工程注册失败）。

## 8. 已知限制

- 只处理字段与局部变量声明（含同一语句里的多个声明符）；属性初始化器、事件字段、
  `const` 不处理。
- 单文件模式（`convert`）不注入支撑库，改写后的调用点需要在项目模式下转换。
- 载荷是二进制资源，diff 不可读；名字里保留了 `<Owner>.<Member>.<hash>`，
  审查时按 hash 与大小即可定位。
