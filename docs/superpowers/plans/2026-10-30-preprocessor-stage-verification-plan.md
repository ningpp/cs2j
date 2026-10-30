# 预处理器阶段验证计划（SDK / AGL）

日期：2026-10-30
状态：进行中

## 目标

对 `csharp-sdk`（`D:\csharp-sdk\csharp-sdk-without-json.sln`）与 AGL（`E:\agl-master\GraphLayout\agl.slnx`）
的每一个预处理器中间树，按锁定顺序逐个验证：

1. `dotnet build -c Release` 0 error；
2. `dotnet test -c Release` 0 单元测试失败。

一次只跑一个 pass（`preprocess --pass <id>`），一个失败一个修复、验证后提交；生成树（`D:\cs\...`）只读，
所有修复落在转换器源码与测试中。

## 基线（2026-10-30）

| 阶段 | SDK | AGL |
| --- | --- | --- |
| `.cs2j-index-range-src`（readonly 的上一个 pass） | build 0 error；tests 0 失败 | build 0 error；tests 613/613 通过 |
| `.cs2j-readonly-src`（修复前） | build 3 error（CS0200，`FacetsCompiler` 构造函数） | build 0 error；tests 613/613 通过 |
| `.cs2j-readonly-src`（修复 #1 后，重新生成） | build 0 error；tests 0 失败（11 个测试程序集，与 index-range 基线一致） | build 0 error；tests 613/613 通过 |
| `.cs2j-attribute-normalize-src` 及之后 | 待逐阶段验证 | 待逐阶段验证 |

## 阶段顺序（按实际生成顺序）

conditional → lexical-normalize → partial-merge → local-fn → null-coalesce → expression-desugar →
enum → enum-declaration → delegate → tuple → anonymous-type-lift → foreach-deconstruction →
foreach-source → pattern-desugar → strip-default → refout-in → unsigned → numeric-boundary →
generic → thread-static → generic-deepen → closed-metadata → variance-audit → record-readonly →
array → index-range → **readonly** → attribute-normalize → struct-value-semantics → unsafe-ffm →
closure-lift → iterator-lowering → async-normalize → extension-methods → eliminate-goto →
member-desugar → linq-channel → concurrency-normalize → equality-concat → platform-isolation →
dynamic-desugar → java-keywords（最后一个）

## 复现 / 验证命令

```powershell
# 单个 pass，从上一阶段树生成下一阶段树
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- preprocess `
  -s <上一阶段 .sln> -d <work root> --pass <pass-id> --no-verify-build --no-verify-tests

# 阶段验证
cd <work root>\.cs2j-<pass>-src
dotnet build -c Release
dotnet test -c Release --no-build
```

SDK 与 AGL 每个阶段都必须各跑一次；两者共用同一套转换器修复。

## 修复记录

| # | 阶段 | 现象 | 根因 | 修复 |
| --- | --- | --- | --- | --- |
| 1 | readonly | `FacetsCompiler` 构造函数 3×CS0200 | L5 迁移 2c 只识别 `this.Prop = v`，不识别非限定 `Prop = v`；私有 setter 的后备字段已知但构造函数赋值未重定向。L2 路径则完全没有构造函数重写 | `PrivateSetterFacts`（私有 setter / 纯后备字段写事实）+ `CtorPropertyToBackingFieldRewriter` 支持非限定目标（L2 用语义模型解符号，L5 用保守语法 shadow 集）+ L2 `ApplyPropertyConvert` 接入同一重写；非纯写 setter / 构造函数外写 → NotConvertible |
