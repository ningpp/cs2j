# C10 Attribute Normalize — 实施计划

- 设计：[2026-09-28-c10-attribute-normalize-design.md](../specs/2026-09-28-c10-attribute-normalize-design.md)
- 分支：`feature/preproc-c10-attribute-normalize`（独立 worktree）
- 状态：M1/M2/M3 代码完成；**仍保持 opt-in（`DefaultEnabled=false`）**，默认开启以设计 §8 六条门槛为门，本计划不翻开关。

## 分工红线（设计 §1.3）

- C10 只做 Caller* 归一、attribute inventory/桥接缺口标注与 AN003 透传。
- 永不发射 CS2J3xxx；不改平台分析的启用/豁免状态（含 `NativeMethods.cs` 文件名豁免）；不碰 Transformers / java compat 半区 / E08 实现。

## M1：Caller\* 先行

- Core：options/statistics/diagnostic/result/plan 骨架、入口与 FormatAndValidate；`KnownAttributeCatalog` 最小表（Caller 四族 + GovernedElsewhere 平台项）；`CallerInfoAnalyzer`/`CallerInfoRewriter`（AN-C1~AN-C10）。
- CLI：`AttributeNormalizePassAdapter`（自定义 ExecuteAsync + 回退通道）；注册表 Stub→Implemented 翻转（默认关）；链位插入 make-readonly 之后、member-desugar 之前。
- 测试：Core Caller* 矩阵单测；`SampleAttributeNormalizeSolution`（Lib+App，四元组 .NET 半区行为）；注册表/CLI 测试；sdk 测量轮（Caller 部分）。
- 验证：`--list-passes` 可见 Implemented；fixture build/test 全绿；Caller 残余闸门零残余（除登记 skip）。

## M2：JSON/序列化注解盘点

- inventory 全族分类表（设计 §4.4）；AN-J1~J4/AN-U 规则；overrides 文件与加载；CS2J1310/1320/1321/1322 全集。
- 测试：JSON fixture；四个既有 JSON/注解回归（`JsonNumberHandlingAnnotationProjectTests`、`ReflectionAttributeCompatTests`、`SystemTextJsonDeserializeTypeReferenceTests`、`DataContractSerializerCompatibilityTests`）开关双跑一致；sdk BridgeGap triage。
- 验证：inventory 快照稳定；sdk 无「未分类」点；候选默认开评审（最终未翻）。

## M3：平台注解盘点收口

- 6 类互操作 attribute（DllImport/LibraryImport/ComImport/MarshalAs/UnmanagedCallersOnly/GeneratedComInterface，family=Interop，OwnerPass=C06）与 4 个 OS guard（SupportedOSPlatform/UnsupportedOSPlatform/SupportedOSPlatformGuard/UnsupportedOSPlatformGuard，family=Platform，OwnerPass=C42）盘点完整。
- `AttributeOccurrence` 携带 `GoverningDiagnosticCodes`（Interop→CS2J3201，extern-on-decl 追加 CS2J3203；Platform→CS2J3101）；`AttributeInventoryReport.ByOwnerPass` 只读查询面；catalog `GetGoverningDiagnosticCodes(family)`。
- AN003 Info：仅 `ReportInfo`（CLI 仅 `--verbose`）发射，消息只引用主管辖码、不复述拒绝；树零改动、零 CS2J3xxx、零 CS2J13xx。
- 测试：Tests AN-P1/P2 单测；ProjectTests「平台边界零干扰」双跑（设计 §8 第 5 条）：默认链 vs `--enable-pass attribute-normalize`，CS2J3001/3101/3102/3201-3203 重放集合按 (码, 文件, 行, 消息) 归一化逐条一致；DllImport 声明区间逐字节保留。
- 现状基线（以实测为准）：默认链在 C06（UF060）处中止，两模式最终树均为 `.cs2j-unsafe-ffm-src`；项目管线两条文件级豁免（`NativeMethods.cs`、`*.AssemblyInfo.cs`）在测试中原样复刻；设计 §10 风险 7（ClassTransformer 异常与豁免注释矛盾）挂 C42 澄清，C10 不裁决。

## 转换器后续 PR 清单（设计 §5.4 登记，均不在本 pass 实施）

1. **E08 Caller 豁免退役**：`DefaultParameterVerifier` 与 `DefaultParameterDeclarationRewriter` 两处 `HasCallerAttribute` 豁免分支，在「项目经 C10 预处理」路径上已不可达；以 C10 默认开启为前置门，退役并删对应豁免测试，由全链测试兜底。
2. **JSON 按名契约补桥**：Newtonsoft `JsonProperty(Name)`、DataContract `DataMember(Name)` 成员级读取分支补桥（当前按名契约静默失效）；走 `CompatJsonPropertyName` 同款 compat 注解路线，C# 侧不生成注册代码；以 CS2J1310 BridgeGap 清单作为补/验收输入。
3. **共享 ExpressionTreeFacts 收敛**：表达式树/dynamic 禁区判定当前在 C10（`CallerInfoAnalyzer` 内联）与 JK/C02/C09 各有一份；抽共享 `ExpressionTreeFacts`，四处收敛到同一谓词，禁止继续漂移（设计 §10 风险 8）。
4. （M2+，非承诺）JSON 显式化注册代码形态、Caller 解冻后复用 E08 Core 补重载综合（Q1）、`CallerFilePath` 路径还原策略（Q2）：门槛阶段命中率/Java 半区盘点后单独立项。
