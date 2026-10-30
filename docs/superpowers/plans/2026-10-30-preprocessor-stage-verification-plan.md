# 预处理器阶段验证计划（SDK / AGL）

日期：2026-10-30
状态：已完成（2026-10-30）

## 目标

对 `csharp-sdk`（`D:\csharp-sdk\csharp-sdk-without-json.sln`）与 AGL（`E:\agl-master\GraphLayout\agl.slnx`）
的每一个预处理器中间树，按锁定顺序逐个验证：

1. `dotnet build -c Release` 0 error；
2. `dotnet test -c Release` 0 单元测试失败。

一次只跑一个 pass（`preprocess --pass <id>`），一个失败一个修复、验证后提交；生成树（`D:\cs\...`）只读，
所有修复落在转换器源码与测试中。

## 结论

- `make-readonly` 阶段发现并修复 1 个转换器缺陷（见修复记录 #1，commit `b9f71bf8`）。
- 修复后从 `.cs2j-index-range-src` 重新生成并逐 pass 前推，直到最后一个 pass `java-keywords`：
  - SDK 最终树：build 0 error，11 个测试程序集共 17,724 个测试通过、0 失败（12 skipped）；
  - AGL 最终树：build 0 error，613/613 测试通过；
  - 中间每个 pass 的 SDK / AGL 树均逐一验证为 build 0 error、测试 0 失败。

## 阶段验证结果（修复 #1 之后，逐个 pass 重新生成）

| # | Pass | 中间目录 | SDK build | SDK tests | AGL build | AGL tests |
| --- | --- | --- | --- | --- | --- | --- |
| 26 | index-range（基线，上一阶段） | `.cs2j-index-range-src` | 0 error | 0 失败（17,724 通过） | 0 error | 613/613 |
| 27 | make-readonly（修复后） | `.cs2j-readonly-src` | 0 error | 0 失败（与基线一致） | 0 error | 613/613 |
| 28 | attribute-normalize | `.cs2j-attribute-normalize-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 29 | struct-value-semantics | `.cs2j-struct-value-semantics-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 30 | unsafe-ffm | `.cs2j-unsafe-ffm-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 31 | closure-lift | `.cs2j-closure-lift-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 32 | iterator-lowering | `.cs2j-iterator-lowering-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 33 | async-normalize | `.cs2j-async-normalize-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 34 | extension-methods | `.cs2j-extension-methods-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 35 | eliminate-goto | `.cs2j-no-goto-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 36 | member-desugar | `.cs2j-member-desugar-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 37 | linq-channel | `.cs2j-linq-channel-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 38 | concurrency-normalize | `.cs2j-concurrency-normalize-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 39 | equality-concat | `.cs2j-equality-concat-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 40 | platform-isolation | `.cs2j-platform-isolation-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 41 | dynamic-desugar | `.cs2j-dynamic-desugar-src` | 0 error | 0 失败 | 0 error | 613/613 |
| 42 | java-keywords（最后一个） | `.cs2j-java-keywords-src` | 0 error | 0 失败（17,724 通过） | 0 error | 613/613 |

验证工作树（未改动原参考目录 `D:\cs\cs-sdk-r7248dd36`、`D:\cs\cs-agl-r7248dd36`）：

- `D:\cs\work-sdk-ro2\.cs2j-readonly-src`（第 27 阶段）
- `D:\cs\work-agl-ro2\.cs2j-readonly-src`
- `D:\cs\work-sdk-stage\.cs2j-*-src`（第 28–42 阶段）
- `D:\cs\work-agl-stage\.cs2j-*-src`

日志：`D:\cs\_logs\`（`<label>-preprocess.log` / `<label>-build.log` / `<label>-test.log`）。

## 基线（修复前）

| 阶段 | SDK | AGL |
| --- | --- | --- |
| `.cs2j-index-range-src` | build 0 error；tests 0 失败 | build 0 error；tests 613/613 |
| `.cs2j-readonly-src` | build 3 error（CS0200，`FacetsCompiler` 构造函数） | build 0 error；tests 613/613 |

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

## 修复记录

| # | 阶段 | 现象 | 根因 | 修复 | Commit |
| --- | --- | --- | --- | --- | --- |
| 1 | make-readonly | `FacetsCompiler` 构造函数 3×CS0200 | L5 迁移 2c 只识别 `this.Prop = v`，不识别非限定 `Prop = v`；私有 setter 的后备字段已知但构造函数赋值未重定向。L2 路径则完全没有构造函数重写 | `PrivateSetterFacts`（私有 setter / 纯后备字段写事实）+ `CtorPropertyToBackingFieldRewriter` 支持非限定目标（L2 用语义模型解符号，L5 用保守语法 shadow 集）+ L2 `ApplyPropertyConvert` 接入同一重写；非纯写 setter / 构造函数外写 → NotConvertible | `b9f71bf8` |

## 备注

- 转换器单元测试套件 `tests/CSharpToJava.Tests` 在 HEAD 与修复后均有 14 个与本改动无关的既有失败
  （Java 转换命名/导入类断言）；已在独立 worktree 对比确认非本修复引入，`ReadOnlyStruct*` 过滤测试 84/84 通过。
- 未改动生成树源码；所有阶段树均由 `preprocess --pass` 重新生成。
