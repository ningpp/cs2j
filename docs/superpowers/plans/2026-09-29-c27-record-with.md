# C27 record with 去糖实施计划（M0–M3 + 收口）

> 规范来源：[2026-09-29-c27-record-with-design.md](file:///d:/code/cs2j/docs/superpowers/specs/2026-09-29-c27-record-with-design.md)。诊断码沿用 MemberDesugar 52xx 序列，子集开关 `records.enabled`，默认关零行为。

## 1. 范围

member-desugar 新增 Records 阶段，在中间 C# 树上完成 record with 表达式去糖，使下游 C#→Java 管线无需处理 with：

- **RC-1/RC-2** record class 克隆工厂（`Cs2jWithClone`，virtual/override 多态）。
- **RC-3/RC-4** 位置 record class / record struct reshape 终形：去参数表、字段 + get_X/set_X、主 ctor / 无参 ctor / 复制 ctor（class）、Deconstruct。
- **RC-5** record 面 `required` / `[SetsRequiredMembers]` 剥离（CS2J5215 留痕）。
- **WR-1/2/3/6** class with：工厂克隆 + 成员 set 序列；空初始化器纯内联；嵌套 with 后序提升；箭头体成员（方法/运算符/转换/本地函数/属性/索引器 get）块化提升。
- **WR-4** struct / 元组 with：克隆局部按值拷贝 + 成员直写（无工厂）。
- **WR-5** 嵌套 with：内层先提升，外层源直接引用内层克隆。
- **WR-8** residual `with` 零残留闸门（CS2J5230）。
- 无体 record（`record R;`）工厂注入前补花括号。
- 项目级 5290 单文件 Warning 降级与 5221 force 放行。

不在范围：C#→Java 侧 RecordTransformer/StructTransformer 无改动；Java 侧 record 支持由其他卡片负责。

## 2. 阶段与提交

base `840010e2`（dev-3.x HEAD）起，单 worktree 单分支 `feature/preproc-c27-c50-record-with-field-keyword`：

| 阶段 | 提交 | 内容 |
|---|---|---|
| M0 | `45095d16` | 子集开关/诊断/统计接线，默认关零行为 |
| M1 | `063d906f` `25fe0b87` `ae4afe84` `7c95a6a0` | 认领索引、工厂注入、使用点改写、28 例单测 |
| M2 | `3195c99c` `fac3720e` `c193c412` `93484519` `32bf0e1c` `7da2e2cd` | reshape 终形、struct/tuple、嵌套、表达式体提升、矩阵 |
| M3 | `7782e3e7` `0993c184` `196d4dfa` | required 剥离、无体 record 收口、5290/5221、样例夹具 |
| 收口 fix | `fa55cffc` | 跨文件克隆局部显式闭合类型；位置基类 reshape 后无参 ctor 判定；清除全部调试输出；80 例单测矩阵 |
| 收口夹具 | `0fd8d51b` | SampleMemberDesugarSolution 夹具恢复 + 补档（空初始化器/元组/required/跳过族），行为测试双档 |
| 收口项目测试 | `15d0823c` | MemberDesugarRecordWithProjectTests A/B 双档（默认关零改树；启用档统计/中间树/行为对账） |

## 3. 收口修复要点

### 3.1 克隆局部显式闭合接收者类型（跨文件必需）

克隆局部一律发射显式类型而非 `var`。类型显示串在分析期取站点 SemanticModel 的 `GetTypeInfo(with).Type`（`RecordClaimIndex.ClassifySite` → `WithSite.CloneTypeDisplay`）：

- 元组接收者为闭合构造 ITuple，如 `(int A, string B)`、泛型容器内 `(T First, string Tag)`（站点可见 T）；
- 泛型 record 接收者为构造实例 `Box<int>`，**不**使用 plan 的 `OriginalDefinition`（开放 `Box<T>` 是旧坑来源）；
- 自检：若接收者仅解析为开放泛型定义（`IsDefinition && TypeParameters.Length>0`），站点走 CS2J5223 原子不改（专属文案 UnboundGenericCloneType），不产出半残类型名。

根因：跨文件（多语法树编译）改写时，工厂声明文件可能尚未处理，重解析模型中 `Cs2jWithClone` 未绑定，`var` 推断失败导致后续 `clone.set_X` 接收者失绑；显式闭合名在站点可独立 `ParseTypeName` 绑定。回归：`WR4_MultiFile_CloneLocalsUseClosedReceiverTypes_TupleAndGenericRecord`（双语法树，闭合类型发射断言 + 禁 `(T1, T2)`/开放 `Box<T>` + 反射行为）。

### 3.2 位置基类补发无参 ctor 的判定

`PositionalRecordShapeAnalyzer.BaseHasAccessibleParameterless`：基类是同编译位置 record（声明带参数表）时，reshape 后必补发无参 ctor，派生类需同步补发以维持 `base()` 链。

## 4. 诊断码

| 码 | 名称 | 级别 | 用途 |
|---|---|---|---|
| 5212 | WithSiteRewritten | Info（verbose） | 站点已改写留痕 |
| 5215 | RequiredRecordTouched | Info | required / SetsRequiredMembers 剥离 |
| 5220 | PositionalReshapeGate | Warning（strict Error） | 位置 record reshape 闸门（readonly struct 成员直写不可行、参数特性等） |
| 5223 | NonLiftableWithContext | Warning（strict Error） | with 位于不可提升上下文（字段/属性初始化器、特性实参、查询子句、非语句表达式位、开放泛型定义） |
| 5230 | ResidualWith | Warning（strict Error） | 终树残留 with，零残留闸门 |
| 5290 | RecordSingleFileNotSelfContained | Warning | 单文件模式不闭合（跨文件 record）降级提示 |
| 5221 | MetadataDerivedRisk | Warning | 元数据推导风险，force 可放行 |

## 5. 默认关零行为

`records.enabled=false`（默认）时分析层不建任何 plan：`CloneFactoriesSynthesized/WithSitesTotal/ResidualWithCount/PositionalRecordsReshaped` 均 0，树原样保留。项目级由 `TierA_DefaultDisabled_ZeroDiff_NoCloneFactories` 断言（含中间树无 `Cs2jWithClone`/`__cs2j_wclone`、无 5223）。

## 6. 验证命令与结果（收口，2026-09-29 后）

```powershell
dotnet build CSharpToJavaConverter.slnx -c Debug      # 0 error，104 警告（均既有 CS86xx/NU1510 类别）
dotnet build CSharpToJavaConverter.slnx -c Release    # 0 error，104 警告
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Debug `
  --filter "FullyQualifiedName~MemberDesugarRecordWith"   # 82/82（80 单测 + 2 项目级）
dotnet test tests/CSharpToJava.ProjectTests/CSharpToJava.ProjectTests.csproj -c Debug `
  --filter "FullyQualifiedName~RecordReadOnly"            # 153 过 / 1 既有跳过 / 0 失败（E11 共处）
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Debug     # 2536/2536
dotnet test tests/CSharpToJava.Tests/CSharpToJava.Tests.csproj -c Release   # 2536/2536
dotnet test tests/SampleMemberDesugarSolution/SampleMemberDesugarSolution.sln -c Debug  # 原生 55/55
```

- 幂等：`Enabled_SecondRun_IsZeroDiff` / `WR4_ArrowLift_SecondRun_IsZeroDiff` / `M2_PositionalClass_SecondRun_BytesStable` 等 5 例二次管线零 diff，全绿。
- 编码：样例夹具含中文注释，沿用仓库 UTF-8 无 BOM 约定（与 FieldKeywordSamples.cs 一致），管线双档读写不乱码。
- B 档样例统计（校准）：record 类型 9、with 站点 10（实际改写 9；Vec2 readonly struct 命中 5220 保留原生 with）、克隆工厂 7、位置 reshape 6、residual 0、5223×1（SkipCarrier 字段初始化器）、5215×1（RequiredNote）；55 条行为测试在 A/B 两档中间树上全绿。
- 调试残留清零：`git grep -n "Console.WriteLine" -- src/CSharpToJava.Core/MemberDesugar` 与 `git grep -n "DEBUG" -- src/CSharpToJava.Core/MemberDesugar` 均无匹配。

## 7. 已知环境抖动（与 C27 无关，失败勿追产品逻辑）

- `CliPreprocessChain`（C43/C40/foreach-source/StripDefault）偶发 build-server 并行抖动；`dotnet build-server shutdown` 后串行可过。
- `LexicalNormalizeProjectTests.Fixture_StripsLineDirectives` 在长 worktree 路径下稳定撞 Win32 267（MAX_PATH）；仅当新增 Records 夹具撞 MAX_PATH 才报告并以替代证据处理，不删测试。
