# C43 词法层字面量归一（raw string / u8 / verbatim 组合）设计

**日期**: 2026-09-29
**范围**: 在 C28 锚点任务落地后的 `src/CSharpToJava.Core/LexicalNormalize/` 下新增 `Literals/` 子目录（纯语法、幂等、每文件只 parse 一次），追加四条字面量规则 LL-1…LL-4 与一个保真校验规则 LL-5：非插值 raw string `"""…"""` → 常规 C# 字符串字面量；`"x"u8` → 逐字节 `new byte[]{ (byte)0xXX, … }`；非插值 verbatim `@"…"` → 常规字符串字面量；插值形态（`$""`/`$@""`/`@$""`/`$"""..."""`/多美元）**整体跳过**交 C23。本任务不新注册 pass、不翻转默认开关、不改任何 C#→Java 转换代码。
**状态**: 设计稿（★观察项——默认关，实施触发条件见 §1.3 与 §8）
**族属**: catalog P3「词法/指令归一族」（[preprocessor-catalog.md §3.3 L1080](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1080-L1080)：E01 + C28 + **C43**）。注册表 stub `lexical-normalize` 条目 `catalogItems = "C28, C43 (observation)"`、`RunAfter={conditional}`、`Stage=Lexical`、`needsSemanticModel=false`（[PassStubCatalog.cs:50-60](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs#L50-L60)）。**C28 是锚点**：其设计 [2026-09-29-c28-directive-trivia-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c28-directive-trivia-design.md) 已为 C43 预留 `Literals/` 目录所有权（§4.1）、CS2J5570-5599 码段（§6）、规则序列追加位与适配器选项透传接缝（§11 协调条）。本设计是对该预留槽位的填充设计。

---

## 1. 背景与目标

### 1.1 ★诚实定位：现状不缺正确性

2026-09-28 已核实并在本次调研中复核：C43 覆盖的三种词法形态，主转换器已全部内联吸收，**无 fail-fast、无静默坏码、无测试红灯证据**：

- **raw string**：[LiteralExpressionTransformer.cs:233-245](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L233-L245)——`TargetJavaVersion >= 15` 发 Java text block `"""\n…"""`（尾行无换行时补一个 `\n`），低版本回落 `StringEscapeHelper.EscapeJavaString(token.ValueText)` 转义普通串；HIR 路径另在 [HIRExpressionGenerator.cs:81-88](file:///d:/code/cs2j/src/CSharpToJava.Core/HIR/HIRExpressionGenerator.cs#L81-L88) 对 raw token 原样透传 `token.Text`（C# `"""` 与 Java 15 text block 同形）。
- **u8**：[LiteralExpressionTransformer.cs:288-294](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L288-L294)——`Encoding.UTF8.GetBytes(token.ValueText)` 后发 `new byte[]{ (byte)0xXX, … }`，注册于 `SyntaxKind.Utf8StringLiteralExpression`（[:25](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L25-L25)、[:41](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L41-L41)）。
- **verbatim**：[LiteralExpressionTransformer.cs:247-255](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L247-L255)——`StringLiteralToken` 文本前缀 `@`/`$@`/`@$` 时取 `ValueText` 经 `EscapeJavaString` 发普通 Java 串（`""` → `\"`、换行保留为值再转义）；插值 verbatim 的正文/洞处理在 [StringExpressionTransformer.cs:290-339](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L290-L339)（`isVerbatim`/`isRaw` 分类于 [:48-50](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L48-L50)、[:140-142](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs#L140-L142)）。

catalog C43 条目（[L867-879](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L867-L879)）因此定调 **P3（观察项）、状态「不缺（转换器内联吸收中）」**；方向性输入称 sdk raw string 1345 处、「最高性价比第一个 pass」（[§5.5 L1194](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1194-L1194)），但 §5 篇首已声明方向性输入与代码证据冲突时以证据为准（[L1125](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1125-L1125)）——本设计全程遵循该定调。

### 1.2 §5.3 分层判据的正面回答

[preprocessor-foundation_plan.md](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md) 与 catalog §5.3（[L1149-1155](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1149-L1155)）的分层判据明确：**局部 1:1 表达式替换、不引入声明**（判据行点名 raw/u8/verbatim）可留 Java AST 层，「为收敛输入面也可做最简单的 C# pass」。本 pass 的存在理由必须正面回答这条判据：

- 按判据，现状（Java 层 1:1 处理）**合规**，C43 不是正确性修补；
- C43 的唯一收益是**输入面收敛**：把「目标 Java 版本分支」从转换器移到 C# 层（raw 归一后转换器不再需要 `>=15` text block 判断），产出**目标 Java 版本无关的中间树**；并使 E01+C28+C43 成为统一引擎「一次 parse 合批」首批纯语法零件（[L1184](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1184-L1184)、风险 19 [L1117](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1117-L1117)）；
- **结论**：若到实施排期时「统一引擎落地」与「版本无关中间树需求」两个驱动均不成立，则本 pass **不应实施**——维持转换器内联是判据允许的终态。本设计的全部工程动作都在默认关之后，不制造提前实施的既成事实。

### 1.3 目标

1. 在 C28 已翻转的 `lexical-normalize` pass 内，以追加（非修改既有 LN-1…LN-7 行为）方式交付 LL-1/2/3 三条归一规则、LL-4 插值跳过登记、LL-5 值保真校验；
2. 产物为**只含常规 C# 词法**的可编译中间树：无 raw 定界符、无 `u8` 后缀、无非插值 `@"`；字符串**运行时值**逐字符不变、u8 **字节序列**逐字节不变；
3. 幂等（二次跑零 diff）、纯语法（`NeedsSemanticModel` 维持 false）、trivia 保真，复用 C28 的一次 parse / 适配器 / verify / 中间目录与码段体系；
4. 完成 `catalogItems "C28" → "C28, C43"`、Description 更新与 C23 边界双向引用；
5. 明确**不做**：任何默认开关翻转（C28 本身仍默认关，C43 不改变这一点）、插值 raw/verbatim 的文本归一（归 C23）、转换器 text block/u8/verbatim 路径删除（仅登记退役条件，§11）、跨 pass 合批编排（M3 接缝，§9）、`\e`（C# 13 ESC 转义，见 §11 末与开放问题 5）。

### 1.4 硬不变式

1. **值保真**：每个被替换节点，输出重 parse 后新节点的「字符串值/字节序列」必须等于输入节点 `token.ValueText`（或其 UTF-8 编码），校验不过则整文件回退原文 + Error（CS2J5580/5581），不产出半改写文件；
2. `NeedsSemanticModel = false` 不被任何子项翻转（沿用 C28 D10「宁拆规则不升档」）；
3. 不碰插值节点（`InterpolatedStringExpressionSyntax` 全形态）——与 C23 语法面不相交（§4.5）；
4. 幂等：LL-1/2/3 产物（常规 `StringLiteralToken` / `ArrayCreationExpressionSyntax`）二次跑不再命中任何规则；
5. 不引入声明、不引入新 using（数组发射用 `byte` 关键字预定义类型，不出现 `System.Text.Encoding` 符号）；
6. Error 级诊断存在时该文件 Changed=false；禁止注释/占位/静默透传（catalog §4 风险 12）。

---

## 2. 关键设计决策表

| # | 决策点 | 结论 | 依据/备注 |
|---|---|---|---|
| D1 | raw → 常规串的取值层 | **不手写解码，统一用 Roslyn `token.ValueText` + `SyntaxFactory.Literal(value)` 重建常规 `StringLiteralToken`**；严禁自行按 `"""` 切分源码文本 | `ValueText` 已由 Roslyn 完成全部词法语义：定界符剥离、首行换行去除、缩进裁剪、换行归一、引号/反斜杠字面化。手写算法必然与编译器漂移；C23 发射常规字面量也用同一工厂（[c23-design L91](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L91-L91)）。缩进裁剪规则（§4.3）作为**必须理解并用测试钉死的契约**，而非待实现的算法 |
| D2 | 多行 raw 缩进裁剪契约 | 精确复刻 Roslyn 规则的**观测口径**：① 开定界符所在行其后只允许空白且必须换行，该首个换行**不进值**；② 闭定界符左侧空白列 = 公共前导列，每个内容行必须带完全一致的前导空白前缀（tab/space 逐字符一致，混用即编译错误），该前缀从每行剥除；③ 全空白行剥前缀后归一为空串；④ 闭定界符前最后一个换行**进值**；⑤ 内容换行在 `ValueText` 中归一为 LF（`\r\n`/`\r` → `\n`）；⑥ raw 内无任何转义，`\` 与少于定界符长度的连续 `"`（含 `""`、`"""` 配 4 引号定界）均为字面内容 | 单行 raw 无换行概念，值即定界符间全部文本。⑤ 的精确行为以 .NET 半区对照测试钉死（§7.1），不在本仓重新论证；命中无法解析形态时 Roslyn 已先给 parse Error（文件原样透传，§6） |
| D3 | 常规串的转义发射 | 委托 `SyntaxFactory.Literal(valueText)`：`"` → `\"`、`\` → `\\`、CR/LF/Tab/控制字符 → `\r\n\t\uXXXX`；可打印非 ASCII（中文/emoji）按源码 UTF-8 原样发射；不引入 Java 专属转义（本 pass 产物是 **C#** 中间树，不做 `\uXXXX` 之外的 Java 化） | 与 [StringEscapeHelper.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Utilities/StringEscapeHelper.cs) 的 Java 转义职责分离：转换器收常规 C# 串后仍走现有 `EscapeJavaString`，本 pass 不预支 Java 决策 |
| D4 | u8 发射形态 | **逐字节常量数组**：`new byte[]{ (byte)0xXX, … }`（数组初始化器，元素为十六进制字节常量 + 显式 `(byte)` 强转，空串 → `new byte[]{}`）；**不选** `Encoding.UTF8.GetBytes("…")` | ① C# 编译器对 u8 的语义是编译期 UTF-8（无 BOM）常量 blob，逐字节常量是同形 lowering，.NET 半区可直接断言；② GetBytes 是运行时调用、引入分配与 `System.Text.Encoding` 符号（违反不变式 5），且非常量；③ 0x80-0xFF 半区强转写法与现有转换器发射一致（[:292](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L292-L292)），消除「隐式常量转换」在旧 LangVersion/部分上下文的可读性歧义。`new byte[]` 到 `ReadOnlySpan<byte>` 靠 C# 11 隐式 span 转换，方法实参/字段初始化场景 verify build 覆盖；与 C06 span 上下文无新增耦合（数组形态本就可隐式转 span） |
| D5 | u8 非 ASCII / 代理项 | 字节序列取 `Encoding.UTF8.GetBytes(token.ValueText)`（Core 计算用，不发射到产物）：中文 3 字节、emoji 4 字节按 UTF-8 展开；孤立代理项以 .NET 默认替换策略（U+FFFD → `EF BF BD`）为**暂定口径并钉测试**；若实测 Roslyn 对 `"\uD800"u8` 判编译错误，则 parse/编译阶段由 verify build 拦截，本 pass 不自行特判 | 正常可编译输入不含孤立代理；该条仅防值保真校验的边角分歧，属残余登记项 |
| D6 | verbatim 转义 | `@"` 前缀的**非插值** `StringLiteralToken`：取 `ValueText`（`""` 已合为单个 `"`；源换行**原样保留**——verbatim 不做 CRLF 归一，值中即源里的 `\r\n`/`\n`）→ `SyntaxFactory.Literal` 重建，换行以 `\r\n` 转义形式进常规串 | 与 raw 的换行口径差异（D2⑤ vs 本条）必须分别用 CRLF 夹具钉死；判定只认 `LiteralExpressionSyntax` 且 `token.Text` 以 `@"` 起始，不碰标识符 `@name`（那是 [JavaNaming.cs:156](file:///d:/code/cs2j/src/CSharpToJava.Core/Context/JavaNaming.cs#L156-L156) 的另一族词法） |
| D7 | `$@`/`@$`/插值 raw 的形态 | **v1 整体跳过，不剥离 `$`、不拆分洞**：凡 `InterpolatedStringExpressionSyntax`（start token 为 `InterpolatedStringStartToken`/`InterpolatedVerbatimStringStartToken`/`InterpolatedSingleLineRawStringStartToken`/`InterpolatedMultiLineRawStringStartToken`，含多美元 `$$`）→ LL-4 只计数（CS2J5574），节点原样 | 词法 pass 先于 C23（Expression stage）：若在 C23 之前把插值 raw 的文本段改写成常规 token，C23 看到的节点种类/token 种类与 `ValueText` 口径（多美元括号层数、对齐/格式洞）全部改变，两 pass 语法面相交即破坏 [c23 §11 L457](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L457-L457) 的「节点种类不同、不相交」约定。整体跳过后：C23 消费 Roslyn 已 parse 的洞并直接发射常规字面量（其 M3 矩阵含 raw/verbatim 全形态，[c23 L366](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L366-L366)），终态自然收敛；C23 落地前这些形态继续由转换器内联吸收，无正确性回退 |
| D8 | 幂等 | LL-1/3 产物为常规 `StringLiteralToken`（文本无前缀、非 raw kind）；LL-2 产物为数组创建表达式（无 `Utf8StringLiteralToken`）；二次跑零目标节点 | residual 闸门（LL-5）扫描输出树对应 token kind/前缀，违例 CS2J5582 |
| D9 | trivia 处理 | 新节点统一 `.WithTriviaFrom(旧节点)`（leading/trailing 全继承）；raw 多行字面量前导注释、行尾布局不额外格式化（不同于 LN-7，本规则不跑 `Formatter`） | 字面量替换是单点表达式替换，不引起周边 whitespace 变化，diff 最小化；幂等文本比较（ordinal）天然成立 |
| D10 | 纯语法 | 不建 compilation、不接 SemanticModel；字节计算仅用 `System.Text.Encoding.UTF8` 静态工具；目标形态判定全凭 `SyntaxKind` 与 token 文本前缀 | 维持 C28 parse-only 快路径（[ProjectPassRunner.cs:164-174](file:///d:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/ProjectPassRunner.cs#L164-L174)） |
| D11 | 规则序 | 在 C28 pass 内规则序列**追加末位**：DirectiveRewriter(LN-1/2/3) → UsingAliasRewriter(LN-4/5/6) → NamespaceRewriter(LN-7) → **LiteralRewriter(LL-1…5)**；同一棵树一次 parse、内存串接、一次 `ToFullString` | 节点集合与 C28 三 rewriter 不相交（字面量 vs 指令/using/命名空间节点）；追加末位使 LN-7 的 Formatter 先完成、字面量随所属节点搬迁后再替换，避免两轮 trivia 竞争；C28 所有权约束要求不回改既有规则（[c28 §4.1 L163](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c28-directive-trivia-design.md#L163-L163)），追加是唯一合规接法 |
| D12 | 默认开关 | 不新增 pass、不翻注册表；C43 随 C28 pass 的 opt-in 生效，三个子规则选项默认 true（pass 本身默认关即总闸）；不新增 convert-project 开关 | 「默认关观察项」的两层门：第 1 层 C28 pass 默认关；第 2 层通用 verb 可 `--ln-no-raw/--ln-no-u8/--ln-no-verbatim` 细粒度关闭（接 C28 §5.3 的 pass 专属参数透传） |

---

## 3. 链位与族契约

### 3.1 复用既有锚点，零注册改动

- **不新增 pass id、不新增 stub、不动 RunAfter/Stage**：`lexical-normalize` 经 C28 翻转后位于 `conditional` 之后、`local-function-lift` 之前（[c28 §3.2](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c28-directive-trivia-design.md#L98-L109)）。C43 是该 pass 内部规则集扩展，链序断言零改动。
- 执行序：`conditional → lexical-normalize(LN-* 然后 LL-*) → local-function-lift → …`。LL-* 处于一切语义 pass 之前，C23（expression-desugar 族，RunAfter=null-coalesce）远在其后——D7 的跳过约定在时间序上天然成立。
- 中间目录复用 `.cs2j-lexical-normalize-src`（不新增目录、不新增 preserve 接线、不新增指纹令牌：pass 级令牌 `pass:lexical-normalize=on/off` 已覆盖规则内容变化；规则集扩展造成的一次性缓存失效随 C43 提交自然发生，可接受）。
- Stage 保持 `Lexical`；`NeedsSemanticModel=false`、无支撑库、双 verify 沿用 C28 描述符（raw→多行大字符串与 u8 数组均有结构/编码风险，保持 verify build + tests 双开）。

### 3.2 C43 落地时的描述符字段差异（仅两处文本）

```
CatalogItems = "C28"            → "C28, C43"          # 仅此一行
Description  = …（C28 文案）     → 追加 "; normalize raw/u8/verbatim literals to regular C# lexical forms"
```

其余字段（Id/Stage/Status/DefaultEnabled=false/RunAfterIds/NeedsSemanticModel/SupportLibrary/Verify*/目录名）**不动**。`catalogItems` 不保留 `(observation)` 标注——代码进树即不再是观察项；「默认关 + §8 门槛」继续表达观察项定调。

### 3.3 与族契约的一致性

- 与 E01/C28 同为纯语法、幂等、parse-only，是 catalog §5.4「纯语法批共享一次 parse」与风险 19「E01+C28+C43 试点一次 parse 内存合批」的第三块合格零件；
- rewriter 形状遵守 C28 D11 接缝：无文件 IO、`(CompilationUnitSyntax, options, bag) → CompilationUnitSyntax` 的 tree-in/tree-out 可组合件，供未来 `LexicalBatchRunner` 链式组合（本任务不实现合批）。

---

## 4. Core 组件设计

### 4.1 目录与文件所有权（C28 预留槽位的填充）

```
src\CSharpToJava.Core\LexicalNormalize\
  Literals\                                    # 【本任务新增目录，全部新文件，零改动 C28 文件行为】
    LiteralSyntaxFacts.cs                      # token kind/前缀分类：raw / u8 / verbatim / 插值
    LiteralRewriteOptions.cs                   # 三个 bool 选项（默认 true）
    RawStringLiteralRewriter.cs                # LL-1（单行/多行非插值 raw）
    Utf8SuffixLiteralRewriter.cs               # LL-2（u8 → byte[] 数组创建）
    VerbatimLiteralRewriter.cs                 # LL-3（非插值 @"…"）
    InterpolatedLiteralCounter.cs              # LL-4（只读遍历，计数跳过，不改树）
    LiteralNormalizationVerifier.cs            # LL-5（重 parse + 值保真 + residual）
    ByteLiteralFactory.cs                      # ValueText → UTF-8 字节 → ArrayCreationExpressionSyntax
```

集成点（遵守 C28 §4.1 所有权硬约束）：仅在 `LexicalNormalizePreprocessor` 的规则序列与 `LexicalNormalizeModels.cs`（Options/Statistics 扩展字段、诊断码常量）做**追加性**修改；共享件需放宽时先扩根层并补 C28 回归。不新建对外入口类（仍由 `LexicalNormalizePreprocessor.Normalize` 统一对外）。

### 4.2 分类规则（LiteralSyntaxFacts）

| 形态 | 语法判定 | 规则 |
|---|---|---|
| 非插值 raw | `LiteralExpressionSyntax`，`Token.IsKind(SingleLineRawStringLiteralToken)` 或 `MultiLineRawStringLiteralToken` | LL-1 |
| u8 | `LiteralExpressionSyntax`，`Token.IsKind(Utf8StringLiteralToken)`（表达式 kind `Utf8StringLiteralExpression`） | LL-2 |
| 非插值 verbatim | `LiteralExpressionSyntax`，`Token.IsKind(StringLiteralToken)` 且 `Token.Text.StartsWith("@\"")` | LL-3 |
| 常规字符串/字符/数值 | 其余 `LiteralExpressionSyntax` | 不碰 |
| 任意插值 | 祖先/自身为 `InterpolatedStringExpressionSyntax`（start token 四种 kind，见 D7） | LL-4 跳过计数 |

注意：现有转换器 [:250](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L250-L250) 在 `StringLiteralToken` 上防御性匹配 `$@`/`@$` 前缀；在语法树模型里这些形态属于 `InterpolatedStringExpressionSyntax`，`LiteralSyntaxFacts` 只认 `@"` 前缀且要求外层不是插值表达式，避免误吞。

### 4.3 LL-1 RawStringLiteralRewriter 实现要点

1. `VisitLiteralExpression`：非 raw kind 直接 base 返回；
2. `var value = node.Token.ValueText;`——Roslyn 已按 §2 D2 六条完成解码（含闭合引号列缩进裁剪与 LF 归一）；
3. `var token = SyntaxFactory.Literal(value);`（内部完成全部 C# 常规转义）；`var replacement = SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, token).WithTriviaFrom(node);`
4. 单行 raw 同路径（无换行特判需求，ValueText 已不同）；
5. 在 rewrite 上下文袋记录 `(node.Span, oldKind, value.Length)` 供 LL-5 保真回查；
6. **禁止**读取 `token.Text` 做任何手工裁剪——多行缩进算法只存在于「理解 Roslyn 行为 + 测试矩阵」中（§7.1 缩进裁剪四档），实现层零算法。

### 4.4 LL-2 Utf8SuffixLiteralRewriter 与 ByteLiteralFactory

1. 命中 `Utf8StringLiteralToken`：`var value = node.Token.ValueText; var bytes = Encoding.UTF8.GetBytes(value);`（D5 代理项口径钉测试）；
2. 逐字节构造初始化器元素：`(byte)0xXX`——`SyntaxFactory.CastExpression(byteType, SyntaxFactory.NumericLiteralExpression(SyntaxFactory.Literal("0x" + b.ToString("X2"), (int)b)))`；数值 token 文本用十六进制两位大写（与转换器 :292 风格一致，diff 可读）；
3. 组装 `ArrayCreationExpression`：预定义 `byte` 关键字类型 + rank specifier + `ArrayInitializerExpression`；空数组 initializer 零元素；
4. 返回数组表达式 `.WithTriviaFrom(node)`（替换的是表达式节点，外层若在实参/`return`/字段初始化位置，C# 11 span 隐式转换负责类型兼容）；
5. 记录字节序列副本供 LL-5 逐字节回查；
6. 统计 `Utf8LiteralsExpanded` 与 `Utf8BytesEmitted`；超长数组（元素数阈值，默认 1024）仅 CS2J5590 Info 提示，不限流、不报错（编译期数组无长度上限问题，仅提示 verify 成本）。

### 4.5 LL-3 / LL-4 与 C23 接缝

- LL-3：`@"…"` → 同 LL-1 的 ValueText+`SyntaxFactory.Literal` 路径（区别仅在换行口径：verbatim 值中保留源换行，工厂会发 `\r\n`/`\n` 转义）；
- LL-4：`InterpolatedLiteralCounter` 用 `SyntaxWalker` 或 rewriter 中 `VisitInterpolatedStringExpression` 计数后**原样返回**（不递归改写其内部文本 token——内部 `InterpolatedStringTextSyntax.TextToken` 即使带 raw/verbatim 种类也不碰）；
- 接缝承诺（与 [c23 §11 L457](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c23-interpolation-expand-design.md#L457-L457) 对齐）：C43 管「定界符与非插值字面量」，C23 管「已 parse 插值的插值部分」；raw 插值的文本缩进/多美元括号归一由 Roslyn ValueText 承担，双方都不做对方的词法变换。C23 未落地前，插值形态零改写继续走转换器内联路径；C23 落地后，其产物全为常规词法，LL-5 residual 自然收紧到零。

### 4.6 LL-5 LiteralNormalizationVerifier

输出文本重 parse（与 C28 同一 parse 一次基线）后：

1. **零 parse Error**（沿用 C28 verifier）；
2. **值保真**（不变式 1 的执行件）：
   - LL-1/LL-3：按记录的 span 路径定位输出节点，断言新 `StringLiteralToken.ValueText` 与改写前值 ordinal 相等；不等 → CS2J5580 Error + 文件回退；
   - LL-2：解析输出数组初始化器，抽出全部常量字节（识别 `(byte)0xXX` 与裸数字两形态），序列与 `Encoding.UTF8.GetBytes(原值)` 逐字节相等；不等 → CS2J5581 Error + 回退；
3. **residual**：输出树中零 `SingleLineRawStringLiteralToken`/`MultiLineRawStringLiteralToken`/`Utf8StringLiteralToken`、零 `@"` 前缀非插值字符串 token；插值节点存在不计违例（单列计数）；违例 → CS2J5582（strict Error，默认 Warning）；
4. 幂等：对输出文本再跑一次 LL rewriter，ordinal 零变化（Core 单测内置，不依赖文件系统）；
5. 结构不扩散断言：改写不改变节点外的 token 序列（raw/verbatim/u8 占位 token 与其替代节点的 leading/trailing trivia 拼接后，周边第一个 token 不变）。

### 4.7 Options / Statistics 扩展（追加到 C28 模型）

- Options 追加：`NormalizeRawLiterals = true`、`NormalizeUtf8Literals = true`、`NormalizeVerbatimLiterals = true`（仅在 pass 已启用时有意义；false 时对应规则整段不命中）；不新增 Symbols、不新增语义选项。
- Statistics 在通用五字段基础上追加：`RawLiteralsNormalized`（分 `SingleLineRaw/MultiLineRaw`）、`Utf8LiteralsExpanded`、`Utf8BytesEmitted`、`VerbatimLiteralsNormalized`、`InterpolatedFormsSkipped`（分普通/verbatim/raw 插值）、`LiteralResidualCount`、`LiteralErrorsByCode`。

---

## 5. CLI 适配器与集成

### 5.1 复用 C28 单适配器，不新增适配器文件

- `LexicalNormalizePassAdapter`（C28 新增）构造参数扩展接收 `LiteralRewriteOptions`（或从 pass 总 options 取）；`RewriteFileAsync` 调 `Normalize` 时透传，Core 内部一次 parse 覆盖 LN+LL 全部规则——**parse-only 快路径不变**，`PassFileContext.SemanticModel` 仍恒 null；
- 诊断沿用 MSBuild 格式 `相对路径(行,列): … CS2J55xx [LL-x] …`；Changed 文件 `PreserveBom=true`；CS2J5580/5581 触发时 Core 已回退原文，适配器按 Error 计失败（与 C28 CS2J5542 同纪律）；
- 摘要行在 C28 行尾部追加字面量段：`…, R raw (m multi), U u8 (b bytes), V verbatim, I interpolated skipped`。

### 5.2 verify 策略

- 沿用描述符双 verify（build+tests 默认开）：raw 多行值保真、u8 span 隐式转换、verbatim CRLF 均由中间树 `dotnet build -c Release` + `dotnet test -c Release` 守门——这是 .NET 半区正确性证据（[catalog §5.1 L1129-1134](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1129-L1134)）；
- u8 夹具的 Tests 工程直接断言 `ReadOnlySpan<byte> span = "…"u8` 形态的使用点（如 `SequenceEqual`、传给受 span 参数方法）在归一后行为一致；
- 不做逐文件回退（同 enum/C16/C28 纪律），保真失败即整文件不改 + fail。

### 5.3 入口面

- convert-project：不新增开关（pass 总 opt-in `--lexical-normalize` / `--enable-pass lexical-normalize` 已是门）；
- 通用 `preprocess --pass lexical-normalize`：增三个 pass 专属参数 `--ln-no-raw` / `--ln-no-u8` / `--ln-no-verbatim`（按 C28 §5.3 既有 pass 参数透传机制接线）；
- `--list-passes` 描述自动随 CatalogItems/Description 更新；禁用时零目录、零耗时、零残留。

---

## 6. 诊断策略

全部落在 C28 预留码段 **CS2J5570-5599**：

| 码 | 级别 | 含义 |
|---|---|---|
| CS2J5570 | Info（verbose） | LL-1 raw 字面量已归一计数（单行/多行分列） |
| CS2J5572 | Info（verbose） | LL-2 u8 展开计数与字节总数 |
| CS2J5573 | Info（verbose） | LL-3 verbatim 归一计数 |
| CS2J5574 | Info（verbose） | LL-4 插值形态跳过计数（普通/verbatim/raw 插值分列；**不是告警**——跳过是设计行为） |
| CS2J5580 | Error | LL-1/LL-3 值保真校验失败（输出重 parse 后 ValueText 不等），文件已回退原文 |
| CS2J5581 | Error | LL-2 字节序列保真校验失败，文件已回退原文 |
| CS2J5582 | Warning（strict 即 Error） | LL-5 residual：输出树仍含 raw/u8/`@"` 词法（插值节点豁免单列） |
| CS2J5590 | Info（verbose） | 超长 u8 数组元素数提示（阈值登记，不阻断） |
| CS2J5571/5583-5589/5591-5599 | — | 预留（`\e` 等未来词法子项，见 §11/开放问题 5） |

异常词法（无法解析的 raw 定界符、非法 u8 等）**本不该发生**：Roslyn parse 阶段先拒（CS89xx/CS1xxx），C28 既有流程对 parse Error 文件收集诊断、原样透传、Changed=false；LL 规则不为此发明二级报错。因此本 pass 诊断主体是**计数登记 + 内部保真失败 + residual**，不承担输入合法性审计。

---

## 7. 测试计划

### 7.1 Core 单测矩阵（新增 `tests/CSharpToJava.Tests/LexicalLiteralNormalizeTests.cs`）

1. **raw 单行**：`"""hello"""`、含单引号/双引号 `"""a"b"""`、含反斜杠路径 `"""C:\temp\a"""`（反斜杠原样）、连续 `""`、4 引号定界符内含 `"""`（`""""a"""b""""`）→ 常规串，ValueText 相等；
2. **raw 多行四档缩进裁剪**：闭合引号列 = 0/2/4 空格 + 1 tab 四档；内容行含恰好等列前缀、更深缩进（保留多余空白）、全空白行（→空串）；首行换行去除、尾行换行保留逐字符断言；
3. **raw 换行口径**：源中 CRLF、LF、CR 三种换行 → ValueText 均为 LF（D2⑤）；输出常规串中表现为 `\n` 转义（源 CRLF 不产生 `\r`）；
4. **raw 闭列违规**：内容行前导空白少于闭列——构造该源码时 Roslyn 已报 parse Error，断言文件原样透传、Changed=false（验证不与编译器抢错）；
5. **u8 ASCII**：`"abc"u8`、空串 `""u8` → 字节序列 61/62/63、空数组；`.NET` 半区对照片段在单测内用 `Encoding.UTF8.GetBytes` 逐字节断言；
6. **u8 多字节**：中文 `"中"u8`（E4 B8 AD）、emoji `"😀"u8`（F0 9F 98 80）、含 BOM 场景断言**无 EF BB BF**、混合 `"a中b"u8`；
7. **u8 半区边界**：字节 0x7F/0x80/0xFF 在输出中的强转形态与数组可编译性；孤立代理项夹具（暂定 U+FFFD 口径，若编译器拒则改为 parse/verify 拦截断言）；
8. **verbatim**：`@"a""b"`（`""` → 单引号字符）、`@"line1\r\nline2"` 三换行源夹具（值保留 CRLF → 输出含 `\r\n` 转义，与 raw 的 LF 口径分别断言）、`@"C:\path"`（反斜杠不翻倍）；
9. **`$@`/`@$`/插值 raw 跳过**：`$"x{x}"`、`$@"a""{x}"`、`@$""`、`$"""raw {x}"""`、`$$"""{{x}}"""` 五形态 → 树零变化、CS2J5574 分类计数正确、内部文本 token 未被触碰；
10. **幂等二次跑**：LL-1/2/3 每类产物再跑一次零 diff；干净文件字节级一致；
11. **保真回退**：人为注入不匹配（测试内模拟 verifier 失败）→ CS2J5580/5581、Changed=false、原文返回；
12. **trivia**：raw 上方行注释、同行尾注释、leading 空白在替换后保留；周边 token 序列不扩散；
13. **选项门**：三个 `--ln-no-*` 对应规则不命中、其余规则照常；插值节点任何选项下都不被改写。

### 7.2 项目级 fixture

在 C28 的 `SampleLexicalNormalizeSolution`（C28 新增）中**追加文件**而非新建 sln（同一 fixture 扩展；若 C28 fixture 未落则在其 plan 中一并加入）：

- Lib：多行 raw（缩进/CRLF）、`"json"u8` 常量用于 `ReadOnlySpan<byte>` 实参、verbatim 多行 SQL/路径串、若干插值 raw（证明跳过）；
- Tests：.NET 运行时断言——raw 值逐行/逐空白相等、u8 字节序列与 `Encoding.UTF8.GetBytes` 一致、verbatim 值含 CRLF；
- 项目级测试：`result.Success`、统计字段、中间树文本断言、**中间树 `dotnet build/test -c Release` 退出码 0**、二次运行幂等、BOM 保持、residual 零（插值豁免清单显式列出）。

### 7.3 Release build/test 与回归

- `dotnet build -c Release`（CSharpToJavaConverter.slnx 全绿）+ 三个测试工程 `dotnet test -c Release` 全绿（本任务实际执行阶段运行；本设计阶段禁止运行）；
- 转换器零改动即零行为回归：[LiteralExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs)、[StringExpressionTransformer.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/StringExpressionTransformer.cs)、[HIRExpressionGenerator.cs](file:///d:/code/cs2j/src/CSharpToJava.Core/HIR/HIRExpressionGenerator.cs) 相关既有字符串测试全绿即可证明；
- C28 回归：LN-1…LN-7 全部既有单测零改动通过（追加式集成的硬要求）。

### 7.4 residual 闸门与 CLI

- 扫描型测试：对启用 LL 规则的输出中间树重 parse，断言零 raw/u8/`@"` 非插值 token；违例 CS2J5582 strict 退出非零；插值节点出现在豁免报表；
- CLI：默认关下 `.cs2j-lexical-normalize-src` 内容与仅 C28 时一致（LL 选项随 C28 pass 默认 true 但 pass 默认关，整体零痕迹）；启用后 `--list-passes` 显示 `C28, C43`；通用 verb 三个细粒度开关生效；诊断码/格式/退出码断言。

---

## 8. 默认开启验收门槛（★观察项绑定里程碑）

本 pass **没有独立的「默认开」动作**——总开关是 C28 的 `defaultEnabled`。C43 规则进入 C28 pass 后，C28 默认开评审（其 §8）必须额外携带以下 C43 证据；且 §1.2 的两个驱动至少一个成立才允许推进默认开：

1. **驱动成立性复核**：统一引擎纯语法合批已立项/落地，或已存在「目标 Java 版本无关中间树」的实际消费者（如 HIR/Java2 管线需要版本无关输入）；两者皆无 → 维持默认关，不提前开；
2. §7 全部测试 + fixture sln 中间树 build/test 双绿；幂等、BOM、字节保真、residual 全绿；
3. **sdk 只读画像（1345 处 raw）分桶报表**：单行/多行/插值 raw 占比、缩进闭列分布、CRLF 占比、u8 与 verbatim 命中数；逐类确认 ValueText 路径无保真回退（CS2J5580/5581 零命中）、插值跳过分桶与 C23 预期输入面一致；
4. **Java 半区等价对照**：启用后经完整链路转换代表性工程，低版本（<15）与高版本（≥15）两档 `TargetJavaVersion` 下 Java 产物字符串运行时值与未启用时一致——重点对照现状 text block 路径在归一后改走转义普通串的等价性（[:236-244](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L236-L244)）；
5. sdk 基线不退化：抽样工程中间树 build/test 不劣于未启用；E01/C28 及其余 pass 统计零变化；增量耗时可接受；
6. C23 边界双向核对完成（两文档互引、LL-4 跳过清单与 C23 M3 矩阵无重叠缺口）。

**明确不建议提前默认开**：独立实施收益低（现状正确），风险面（多行 raw 值）虽由 ValueText 路径大幅降低，但「没有消费者的收敛」只是 diff 噪声与 verify 成本。

---

## 9. 里程碑

| 里程碑 | 内容 | 前置 | 预估规模 |
|---|---|---|---|
| M1 非插值 raw（LL-1） | `Literals/` 骨架（Facts/Options/Verifier）+ RawStringLiteralRewriter + 模型/序列追加 + 适配器选项透传 + `CatalogItems`/Description 更新；§7.1-1/2/3/4/10/12 + C28 回归 + Release build/test | **C28 分支合入 base**（§12）；pass 仍默认关 | Core ~250 行（含 verifier），适配器改动 ~30 行 |
| M2 u8 + verbatim（LL-2/3/4） | ByteLiteralFactory、Utf8/Verbatim rewriter、插值计数；fixture 扩展（span 实参/中文/emoji/CRLF）；§7.1-5…9/11/13、7.2/7.4/7.5；Release 全绿 | M1 | Core ~250 行 + fixture |
| M3 一次 parse 合批（统一引擎期） | E01+C28+C43 三 rewriter 链在 `LexicalBatchRunner` 中共享一次 parse（catalog 风险 19 试点），验证指纹/回滚/落盘调试三不变；sdk 分桶报表（§8-3）与 Java 半区对照；默认开评审随 C28 §8 | 统一引擎立项（§8-1 驱动成立） | 编排/数据，非本 pass 业务规则 |

---

## 10. 风险

1. **★缩进裁剪误判改变字符串值（最高风险）**：多行 raw 的闭合引号列/全空白行/首末换行口径一旦理解错误，产物是编译通过但值错误的字符串（静默坏码）。缓解：实现层零手写算法（D1，只用 ValueText），把风险从「实现风险」降级为「Roslyn 行为认知风险」；§7.1-2/3 四档缩进 + 三换行源的逐字符夹具 + LL-5 值保真回查 + 中间树 .NET 测试（断言真实运行值）三重守门；任何不确定口径以 .NET 半区实测为准并回填本设计。
2. **CRLF 规范化口径混淆**：raw（归 LF）与 verbatim（保留源换行）相反；源文件被 git/编辑器改行尾会造成 ValueText 差异。缓解：两类分别夹具、verbatim 夹具显式 CRLF 字节构造、fixture 加 `.gitattributes`/保留原始字节读取（runner 已保 BOM，行尾不转换需在 fixture 工程核实）。
3. **u8 多字节/代理项分歧**：UTF-8 编码口径与编译器 blob 若在代理项/非常见字符上不一致，字节级静默错误。缓解：逐字节 .NET 半区对照（D5）、CS2J5581 回退、无 BOM 显式断言；sdk 报表（§8-3）过一遍真实 u8 站点。
4. **双 pass 接缝重复转义**：未来若 C23 或其他 pass 对已归一串再次「转义归一」会产生 `\\"` 类双重转义。缓解：LL-1/3 产物是标准 `StringLiteralToken`（ValueText 层再处理天然幂等）；D7 保证插值节点不被本 pass 触碰；与 C23 文档双向边界锁定；幂等二次跑对 C23 接入后的组合形态也加测试。
5. **数组创建的上下文兼容**：`new byte[]{}` 在个别语法位置（如 `stackalloc` 语境、attribute 参数、`const`）非法或语义不同——但 u8 本就不能用于 const/attribute；span 转换在 C# 7 旧 LangVersion 不支持。缓解：parse 固定 `LanguageVersion.Latest`（与 C28 一致），verify build 对真实使用点（字段/实参/局部）全覆盖；若 sdk 报表出现罕见位置，按站点登记显式诊断而非硬发。
6. **观察项沉没成本**：规则与测试先行后驱动长期不成立，维护面空转。缓解：默认关零运行成本；§8 每评审周期复核驱动；M3 之前代码量严格压在 ~500 行 + fixture，不做合批预埋。
7. **HIR 路径漏网**：[HIRExpressionGenerator.cs:81-85](file:///d:/code/cs2j/src/CSharpToJava.Core/HIR/HIRExpressionGenerator.cs#L81-L85) 对 raw 透传 `token.Text`；若未来 HIR 管线绕过主转换器消费中间树，pass 关闭时仍需该路径。缓解：本任务不删不改 HIR 路径，仅在 §11 登记：pass 默认开且 HIR 成为主路径时，该透传分支与 text block 分支一同评估退役。

---

## 11. 并行协调

- **C28（锚点，已设计待实施）**：强前置——M1 开工前 C28 分支须合入工作基线（§12）。所有权边界按 [c28 §4.1/§11](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c28-directive-trivia-design.md#L355-L365)：C43 只新增 `Literals/` 文件、只追加模型字段与规则序列步、只占 CS2J5570-5599、只在适配器构造处加选项；`CatalogItems/Description` 两行更新归 C43 提交。热文件冲突解法照搬 C28 §11：行级追加、不碰他人条目；统计字段合并冲突时后合入方 rebase。
- **C23（插值边界）**：约定不变——节点种类不相交（非插值 `LiteralExpressionSyntax` vs `InterpolatedStringExpressionSyntax`），LL-4 整体跳过；C23 的 handler 语义判定（需 SemanticModel）与本 pass 无先后矛盾（不同 Stage）。联合测试：含插值 raw 的文件先过 lexical-normalize（树不变）再过 C23（M3 后），产物全常规词法、值正确。
- **C36（集合表达式）**：无关。C36 处理 `[…]` 表达式；`new byte[]{…}` 是显式数组创建，不属于集合表达式语法面，无残余计数/重写交互。
- **C06（span/ref struct）**：无新增耦合。u8 数组到 `ReadOnlySpan<byte>` 的隐式转换是编译器既定语义；若 C06 未来对 span 实参做物化，其输入看到的是数组表达式（与手写 `new byte[]{}` 完全同形），不需要 C43 感知。
- **转换器 text block / u8 / verbatim 路径的旁路与退役条件**：pass 启用后，转换器收不到 raw/`u8`/`@"`（非插值）新语法，[:233-245](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L233-L245) 的 raw 分支（含低版本回落）、[:288-294](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L288-L294) 的 u8 分支、[:247-255](file:///d:/code/cs2j/src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs#L247-L255) 的 verbatim 前缀分支与 HIR [:81-85](file:///d:/code/cs2j/src/CSharpToJava.Core/HIR/HIRExpressionGenerator.cs#L81-L85) 成为**事实上的死路径**。**退役条件**（本任务不执行删除）：① lexical-normalize 默认开且稳定一个发布周期；② residual 闸门在全量目标输入（含 sdk/EF Core 画像）上证明零 raw/u8/verbatim token 到达转换器；③ HIR 管线的输入来源同样被 pass 覆盖。三条满足后由独立退役任务删分支并把 `TargetJavaVersion>=15` text block 决策从转换器移除——在此之前这些分支是 pass 关闭路径的正确性保障，必须保留；插值 raw/verbatim 路径（StringExpressionTransformer）在 C23 落地前一律保留。
- **`\e`（C# 13 ESC 转义）归属**：catalog 两处口径不一——五次补全（[L778](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L778-L778)）写 `\e→C43`，Layer 0 全景（[L1199](file:///d:/code/cs2j/prompts/preprocessor-catalog.md#L1199-L1199)）写 C52（C43/C28 同族词法批候选）。其形态是常规 `StringLiteralToken` 内含 `\e` 转义（ValueText 已为 U+001B），LL-1/3 的 ValueText 工厂路径可**零特判自然吸收**，但本任务范围不含它；码位预留（§6），归属在 C52 伞条目审计时与 catalog 维护者拍板（开放问题 5）。

---

## 12. Worktree 分配（只写不执行）

- 分支：`feature/preproc-c43-lexical-literal`
- 目录：`.worktrees/preproc-c43-lexical-literal`
- base：`953293b9`（已用只读 `git rev-parse --verify` 核实存在：`953293b908e9143437c45f287fb4277e890c398e`；当前工作区 HEAD 为 `55d9d109`，以任务指定 base 为准）
- ★**实现前置**：base 同一点位须先包含 C28 分支（`feature/preproc-c28-directive-trivia`）的合入——C43 依赖 C28 创建的 `LexicalNormalize/` 目录、pass 翻转与适配器；若开工时 C28 未合入 base，先合入 C28（或在 C28 worktree 基线上 rebase 本分支），不在 C43 分支内复制 C28 基建。
- 创建命令（仓库根、PowerShell；**本设计阶段不执行，实施阶段由执行者运行**）：

```powershell
git worktree add .worktrees/preproc-c43-lexical-literal -b feature/preproc-c43-lexical-literal 953293b9
```

- worktree 内交付面遵循 foundation plan「并行开发交付包模板」（[preprocessor-foundation_plan.md:108-117](file:///d:/code/cs2j/.trae/documents/preprocessor-foundation_plan.md#L108-L117)）：Core `LexicalNormalize/Literals/`（6 个规则/工厂文件 + 模型追加）、C28 适配器选项透传与 `CatalogItems/Description` 两行更新、Core 单测 + fixture 扩展 + Release build/test + residual 闸门 + CLI 参数；plan 文档 `docs/superpowers/plans/2026-09-29-c43-lexical-literal.md` 在实施批准后另写；
- 不新建支撑工程；不触转换器/HIR 代码；不新增 pass/stub/开关；不创建标签、不 push（实施阶段仍以任务授权为准）。

---

## 附：关键开放问题

1. **驱动成立时点**：统一引擎「一次 parse 合批」与版本无关中间树两个驱动，何者先成立、由哪个消费方（HIR？Java2 管线？）背书？两者皆不成立则本设计仅存档（§1.2/§8）。
2. **D2⑤ 换行归一的实测口径**：多行 raw 在 CRLF/CR 源下 `ValueText` 是否一律 LF（含全空白行、闭行），以 M1 的 §7.1-3 夹具实测为准并回填本文；若 Roslyn 版本间行为变化，是否需要把「换行归一」做成显式步骤而非依赖 ValueText。
3. **孤立代理项 u8 语义**：`"\uD800"u8` 是编译器报错还是 UTF-8 替换字节（EF BF BD）？决定 D5 口径与该夹具形态；sdk 画像是否存在此类站点待 §8-3 报表。
4. **超长 u8 的产物形态**：大 blob（如内嵌 PE/JSON 常量）发 `new byte[]{…上千元素…}` 的 verify 编译开销与源码可读性是否需要阈值策略（例如超阈值改发射独立静态字段数组）——该备选会引入成员声明，直接撞 §5.3 判据，默认不做，待 sdk 长度分布数据。
5. **`\e` 归属（C43 vs C52）**：catalog L778 与 L1199 口径冲突；因 LL 工厂路径可自然吸收，倾向在 C52 伞条目审计时顺手归入本 pass 新增一条 LL-6（零算法），需 catalog 维护者确认后更新两处行号引用。
