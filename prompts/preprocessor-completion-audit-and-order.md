# Preprocessor 完成态核实与后续实现顺序

> **本文为 2026-10-04 重新核实版，取代 2026-10-02 初版结论。**
> 核实基线：仓库 `d:\code\cs2j`，分支 `dev-3.x`，HEAD `3147e093`（all-on policy）＋波次 0 接线工作变更（C24 默认接通、C27/C50/C46 CLI 开关、catalogItems 标签订正）。
> 输入件：`C:\Users\15097\Desktop\preprocessor-status.md`、[preprocessor-catalog.md](file:///D:/code/cs2j/prompts/preprocessor-catalog.md)（文本仍停在 10-01，以代码实况为准）。
> 性质：以注册表＋Options 默认值＋适配器接线＋git 里程碑＋测试运行五重证据完成的双向核实。

---

## 1. 核实方法

1. `preprocess --list-passes` 取锁定拓扑序（34 Implemented 全默认 on ＋ 7 Stub）。
2. **不只看注册表标签**：逐个打开规则 Options 的默认值、CLI 工厂是否接线、适配器是否以「子节非 null」为激活条件——区分「真生效」「代码完整但休眠」「仅审计/报告」。
3. git 提交链核实（10-02→10-04 并行 worktree 落地潮，HEAD `3147e093`）。
4. 测试实测（2026-10-04，Debug）：CLI/三个测试项目构建 0 错误；新增波次 0 CLI 接线测试 3/3；全量回归全绿——**CSharpToJava.Tests 2938/2938 通过、CSharpToJava.ProjectTests 1585/1585 通过（20 项耗时 e2e 按既有 Skip 标记跳过）、CSharpToJava.ConversionTests 675/675 通过，0 失败**（TRX 计数，证据见 §6）。

「真正完成」判据：Implemented ＋ 默认链实际执行 ＋ 中间树 build/test 双 verify ＋ residual 闸门 ＋ 单测/项目级 fixture。

---

## 2. 现状全景（2026-10-04）

- **34 个 Implemented pass 全部默认 on**；**7 个 Stub 默认 off**（启用即 CS2J9001）：
  `thread-static(C09)`、`attribute-normalize(C10)`、`extension-members(C49)`、`async-normalize(C18,C38)`、`anonymous-type-lift(C21)`、`linq-channel(C26)`、`dynamic-desugar(C25)`。
- 已落地但默认**休眠的子集开关**（代码/测试完整，opt-in）：**C27、C50、C46**（member-desugar 内 `rules.records/fieldKeyword/signatures`，波次 0 起可用 CLI 开关启用）。
- 特殊形态：**C42** 默认 Report（审计，不隔离/不改写；Reject/Stub/Facade opt-in）；**C33** 按设计即只读审计型；**C06** 指针半区生效，extern/ref struct 为 Error 显式拒绝。
- **无任何实现的条目**：C35/C51（仅一行预留注释）、C41（仅拒绝诊断）、C52（伞条目无建制）。
- 观察项（转换器已内联吸收，非缺口）：C43 词法面已随 lexical-normalize 前置；C44 同步 using 仍内联（仅 ref struct/async/yield 子题挂 C06/C38/C04）。

---

## 3. C01–C52 逐项台账（10-04 代码实况）

| 编号 | 状态 | 承载 pass / 激活方式 | 备注 |
|------|------|----------------------|------|
| C01 | ✅ 默认开 | ref-out-in | M5 sdk 全量比对/旧码下线为挂账 |
| C02 | ✅ 默认开 | generic-deepen | 非 T 引用型静态字段语义审计归 C52 |
| C03/C37 | ✅ 默认开 | pattern-desugar | 语句＋表达式两侧 |
| C04 | ✅ 默认开 | iterator-lowering | CS2J0405 捕获形态已随 C32/C45 解锁（枚举名保留，无抛出点） |
| C05 | ✅ 默认开 | enum-declaration | |
| C06 | ✅ 半区 | unsafe-ffm | 指针/fixed/stackalloc 生效；extern FF M 白名单未做（UF060/070 Error） |
| C07 | ✅ 默认开 | struct-value-semantics | StructCloneHelper 退役评估挂账 |
| C08 | ✅ 默认开 | java-keywords | |
| C09 | ❌ Stub | thread-static | 转换器三处特判代偿 |
| C10 | ❌ Stub | attribute-normalize | Caller*/[Conditional] 无通道 |
| C11–C14 | ✅ 默认开 | member-desugar 主规则 | |
| C15/C17 | ✅ 默认开 | numeric-boundary | |
| C16/C40 | ✅ 默认开 | foreach-source | |
| C18/C38 | ❌ Stub | async-normalize | **新关键路径第一位**；C44 子题随本通道 |
| C19 | ✅ 默认开 | extension-methods | M0–M4 完整 |
| C20 | ✅ 默认开 | partial-merge | 不可逆合并用重放闸门解决 |
| C21 | ❌ Stub | anonymous-type-lift | |
| C22 | ✅ 默认开 | numeric-boundary NullableLiftRule | `EnableNullable=true`，CLI 无关闭开关 |
| C23 | ✅ 默认开 | expression-desugar 插值子节 | `--interpolation-expand` 默认 on；表达式树内插值保留（0515） |
| **C24** | ✅ **波次 0 接通** | expression-desugar 初始化器子节 | 此前代码完整但两处工厂未接线；现已默认 on，`--no-initializer-desugar` 关闭 |
| C25 | ❌ Stub | dynamic-desugar | |
| C26 | ❌ Stub | linq-channel | IQueryable/Expression 三路分流门仍为前置验收 |
| C27 | 🟡 休眠 | member-desugar `--enable-records-rule`（波次 0 新增；亦支持 JSON） | M1–M3 完整，默认仍关待 sdk 基线后翻转 |
| C28 | ✅ 默认开 | lexical-normalize | LN-1~7 |
| C29 | ✅ 默认开 | concurrency-normalize | Lock/Monitor/Volatile 三 Rewriter |
| C30 | ✅ 默认开 | closed-metadata | M3 注册文件生成；**下游仓 D:\dotnet-text-json M1 核实门仍挂** |
| C31 | ✅ 默认开 | index-range | |
| C32/C45 | ✅ 默认开 | closure-lift 四 stage 无条件执行 | catalogItems 波次 0 补标 C45；递归 lambda D13 显式拒绝 |
| C33 | ✅ 审计型 | variance-audit（只读，strict 默认 false） | 使用-site 型变改写按设计延后 |
| C34 | ✅ 默认开 | numeric-boundary DecimalOpsRule | `--no-decimal-ops` kill-switch；10-02「假完成」结论已失效 |
| C35/C51 | ❌ 无实现 | — | 仅 [ExpressionDesugarRuleSet.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/ExpressionDesugar/Shared/ExpressionDesugarRuleSet.cs#L27) 预留注释＋Options append 位 |
| C36 | ✅ 默认开 | expression-desugar 集合表达式 | |
| C39 | ✅ 默认开 | member-desugar finalizers（Policy 默认 Reject） | |
| C41 | ❌ 仅守卫 | 无映射无规则 | Half/Int128/UInt128/nint 报 cannot express |
| C42 | 🟡 审计默认 | platform-isolation Mode=Report | Stub/Reject 桩化已实现（opt-in）；Facade M3 预留 |
| C43 | ✅ 观察项 | lexical-normalize（raw/u8/verbatim/`\e`） | |
| C44 | ⏸️ 观察项 | 同步 using 内联 | 缺口拆归 C06/C38/C04 |
| C46 | 🟡 休眠 | member-desugar `--enable-signatures-rule`（波次 0 新增） | SI/NH/CR M1–M4c 完整；catalogItems 波次 0 加回 |
| C47 | ✅ 默认开 | equality-concat | lifted Nullable 站点随 C22 默认链收口 |
| C48 | ✅ 默认开 | strip-default ParamsCollections | `[CollectionBuilder]` 站点 v1 Error（M2 预留 Emit） |
| C49 | ❌ Stub | extension-members | 前置 C19＋Roslyn 5.0.0 已就位 |
| C50 | 🟡 休眠 | member-desugar `--enable-field-keyword-rule`（波次 0 新增） | FK-1~9 完整，默认仍关 |
| C52 | ❌ 伞条目 | 无建制 | 防线散在各家族（C36 0585-0589 等） |

图例：✅ 生效｜🟡 已实现但默认休眠/仅审计｜❌ 未实现｜⏸️ 观察项。

---

## 4. 波次 0 接线/激活（2026-10-04 已实施）

零新增改写逻辑，只接通已完成并测绿的规则：

1. **C24 默认翻转**：`convert-project` 与 `preprocess` 两个入口的 `ExpressionDesugarOptionsFactory` 均补 `Initializers` 子节（非 null 即激活），新增 `--no-initializer-desugar` kill-switch；Strict 跟随 `--no-local-function-strict`。
2. **C27/C50/C46 CLI 开关**（此前仅能改 MemberDesugarOverrides.json）：
   `--enable-records-rule` / `--enable-field-keyword-rule` / `--enable-signatures-rule`，两个入口一致；语义为 **JSON 或 CLI 任一启用即生效，CLI 不重置 JSON 其他子节**；三者默认仍关（待 sdk 基线计数后翻默认）。
3. **标签订正**：member-desugar catalogItems 恢复为 `C11, C12, C13, C14, C39, C46, C50, C27`；closure-lift 补标 `C32, C45`；同步锁定测试断言。
4. 新增 3 个 CLI 端到端测试（C24 默认生效、C24 kill-switch 红线、C50 CLI 开关直连规则）。

---

## 5. 后续实现波次

### 波次 1：四条并行开发线（无相互依赖，建议 4 worktree）

- **L1（关键路径）C18→C38 async 族**：M0 形态计数/compat 盘点 → 形态①（async lambda/局部函数提升为命名 async 方法＋ConfigureAwait 归一；消费 closure-lift 产物）→ 方法级 async → C38（await foreach/using＋IAsync* 类型映射/compat；lock 体内 await fail-fast 与 C29 对齐）。形态②（源码级状态机消解）暂不投。
- **L2 C35+C51 null 糖**：在 expression-desugar 已预留的第三规则位实现读取链（复用 E03 两遍临时变量提升）→ `?[]`/`??`/`??=` 组合 → C51 空条件赋值（RHS 条件求值、ref 目标 fail-fast）→ whole-tree gate → 退役 facade 4 处 owner 链回溯。
- **L3 C49 扩展成员**：extension 块改写回经典静态类（扩展属性 get/set 对、扩展运算符并入 C14 形态、静态扩展类型名限定调用）。
- **L4 C21 匿名类型**：复用 E06 shape registry 做跨文件形状去重与具名不可变类型生成。

### 波次 2：LINQ 通道化与声明侧小族

- **C26**（L3/L4 合入后）：入口先落 **IQueryable/IOrderedQueryable 绝不展开、`Expression<Func<>>` 保留节点、仅 IEnumerable 进现有重写** 的 SemanticModel 三路分流门，再通道化（独立目录/verb/双 verify/skipped-chain 报告）。
- 可并行小线：**C09**（ThreadStatic→无参 ThreadLocal，注意初始化器单线程语义；可与 C02 holder 合并评估）、**C10 窄版**（CallerArgumentExpression 调用点注入＋[Conditional] 按构建符号剥调用点；JSON 族维持白名单）、**C25**（静态可证子集＋其余报错）。

### 波次 3：边界/选型/建制

- **C06 extern 裁决**：永久拒绝（文档收口）或 FFM 白名单 opt-in（ExternWhitelistPath 已预留）；C42 的 Reject/Stub/Facade 是其着陆点。
- **C41**：默认显式拒绝升格，Half→float 作可选映射。
- 休眠子集翻转：C27/C50/C46 在 sdk 基线扫描后翻默认开。
- 保留子项：C48 M2 CollectionBuilder 发射、C23 表达式树插值、C45 递归 lambda、C33 使用-site 型变、C42 Facade M3、C30 下游仓核实门。
- **C52**：建制为「每次 Roslyn/基线升级重跑差集审计」的固定流程。

### 并行 worktree 分组

```text
波次 1（4 worktree）：
  WT-A C18→C38（独占关键路径）
  WT-B C35+C51（同属 ExpressionDesugar，必须同树）
  WT-C C49
  WT-D C21
波次 2：C26（WT-C/WT-D 合入后）；C09/C10/C25 另开 1-3 个新目录 worktree
```

冲突纪律：PassCatalog/PassStubCatalog/PassChainFactory/Program.cs 为公共热文件——各 stub 翻转前先合一个「id/RunAfter/选项名全部注册为默认关空骨架」的基建 commit；Core 实现各建新目录天然不相交。合并按固定顺序 rebase。

---

## 6. 证据索引

- 注册表：[PassCatalog.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassCatalog.cs)、[PassStubCatalog.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/PassStubCatalog.cs)
- 接线：[Program.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Program.cs)（两入口选项与工厂；`BuildMemberDesugarOptions`）、[CliPreprocessChain.cs](file:///D:/code/cs2j/src/CSharpToJava.CLI/Preprocessing/CliPreprocessChain.cs)
- 休眠子节：[MemberDesugarModels.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/MemberDesugar/MemberDesugarModels.cs#L325-L361)、[InterpolationOptions.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/ExpressionDesugar/Interpolations/InterpolationOptions.cs)、[InitializerOptions.cs](file:///D:/code/cs2j/src/CSharpToJava.Core/ExpressionDesugar/Initializers/InitializerOptions.cs)
- 波次 0 测试：[PreprocessVerbCLITests.cs](file:///D:/code/cs2j/tests/CSharpToJava.ProjectTests/PreprocessVerbCLITests.cs)、[PassChainOrderTests.cs](file:///D:/code/cs2j/tests/CSharpToJava.Tests/Preprocessing/PassChainOrderTests.cs)
- 波次 0 全量回归（2026-10-04，Debug，TRX 实测计数）：CSharpToJava.Tests **2938/2938 通过 0 失败**（含 C24 默认翻转后的 ObjectInitializer* 全链测试）；CSharpToJava.ProjectTests **1585/1585 通过 0 失败**（20 项 `[Fact(Skip)]` 未执行，为既有耗时 e2e 跳过）；CSharpToJava.ConversionTests **675/675 通过 0 失败**；新增 3 个接线 e2e（C24 默认生效／C24 kill-switch 红线／C50 CLI 直连）全部在内。
