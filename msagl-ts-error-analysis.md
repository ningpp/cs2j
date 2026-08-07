# MSAGL → TypeScript 错误分析记录

目标：`E:\agl-master\GraphLayout\MSAGL`（只读）经 cs2j 转出的 TypeScript 工程全模块 `npx tsc --noEmit` 零错误。
每次迭代：cs2j 改动 → `dotnet build` → 全量重转（保留 node_modules）→ 收集全部 TS 诊断 → 按根因修复 → `dotnet test` 全绿 → 单根因单提交。

| 轮次 | 错误总数 | 本轮修复根因 | 涉及文件 | 提交 hash |
|------|---------|------------|---------|----------|
| 1 | 91148 → 2751 | MSBuild 零引用 → SolutionLoader 注入本机 ref pack | src/CSharpToJava.Core（SolutionLoader 相关） | b1b5e475 |
| 2 | 2751 → 2399 | Java 风味泛型类型映射缺失 | TypeMappings.json 相关 + NpmProjectPlan.GenerateGlobalTypeStub | f83831ec |
| 3 | 2399 → 2381 | FQCN 兜底误切泛型实参 | TsExpressionWriter / RegisterImports | 8cbc0ab1 |
| 4 | 2381 → 593 | raw 方法体 Java 文本未归一化 | TsRawStatementNormalizer / TsMemberWriter / TsExpressionWriter | 94bed66b |
| 4b | — | 重转保留 node_modules（省 npm install） | src/CSharpToJava.CLI/Program.cs | f1d8482d |

## Round 1 — MSBuild 零引用 → 注入本机 ref pack

- **C# 构造**：MSAGL 工程依赖完整 BCL / NuGet（System.Drawing 等），MSBuild 未解析出任何引用程序集。
- **错误 TS 输出**：无法打开引用 / 类型缺失导致 9 万+ 错误。
- **正确输出**：引用注入后诊断归零。
- **根因**：SolutionLoader 解析引用为空时未提供兜底。
- **修复位置**：cs2j SolutionLoader 注入本机 ref pack；MSAGL 诊断 0，错误降至 2751。

## Round 2 — Java 风味泛型类型映射缺失

- **C# 构造**：`Class<?>`、`Map.Entry<K,V>`、`CSharpGenericIterable<T>`、`java.util.List<T>`、`Map<K,V>` 及项目 FQCN 出现在 TS 类型位。
- **错误 TS 输出**：`Class<?>` 等非法类型名（TS1005/TS2318 等）。
- **正确输出**：`any`、`CsKeyValuePair<K,V>`、`Iterable<T>`、`Array<T>`、`CSharpDictionary<K,V>`、FQCN → 末段。
- **根因**：TS 类型映射表缺 Java 风味条目。
- **修复位置**：类型映射 + `NpmProjectPlan.GenerateGlobalTypeStub` 增 `CSharpStack`/`CSharpICollection`/`CSharpGenericComparer`/`CsKeyValuePair` 全局别名。错误降至 2399。

## Round 3 — FQCN 兜底误切泛型实参

- **C# 构造**：`SymmetricTuple<Point>` 等嵌套泛型实参中的 FQCN。
- **错误 TS 输出**：FQCN 末段截取把 `SymmetricTuple<...Point>` 切坏成 `Point>`（错误数一度暴涨 15262）。
- **正确输出**：`SymmetricTuple<Point>` 完整保留。
- **根因**：FQCN 兜底按 `>` 截断，未感知泛型深度。
- **修复位置**：嵌套实参 compat 导入改用 `Symbol|module` 编码，`RegisterImports` 解析；本轮全量重转 2381。

## Round 4 — raw 方法体 Java 文本归一化

- **C# 构造**：方法体 / 字段初始化器中的 Java 风味直通文本：`double factor = 1.5f;`、经典 for 头、`new String[] { "a", "b" }`、`(Iterable<Edge>)(Iterable<?>)(expr)` 引用 cast、`new Microsoft.Msagl.Core.Geometry.Point(...)` FQCN new 表达式。
- **错误 TS 输出**：`double factor = 1.5f;`（TS1434）、`for (int i = 0; ...)`（TS1005）等。
- **正确 TS 输出**：`let factor: number = 1.5;`、`for (let i: number = 0; ...)`、`["a", "b"]`、去 cast、`new Point(...)`。
- **根因**：`TsMemberWriter.AppendMethodBody/AppendArrowBody` 原样输出 raw body；`JavaRawExpression` 在 `ToInlineString` 中原样输出绕过归一化；`EmitLiteral` 未剥 float 后缀。
- **修复位置**：
  - `TsRawStatementNormalizer`：新增 float 后缀、数组字面量、引用 cast 剥除、FQCN 表达式归一化；声明 let 化支持多变量/无初始化/泛型/数组/FQCN 类型；经典 for 头重写；`Normalize` 增 `normalizeChars` 开关（raw 表达式保留单引号字符字面量）。
  - `TsMemberWriter`：raw body / arrow body / 字段初始化器接入 `Normalize`。
  - `TsExpressionWriter`：`EmitLiteral` 剥 float 后缀；`EmitNew` 加类型映射；raw 表达式接入 `Normalize(normalizeChars:false)`。
- **测试**：`TsRawBodyNormalizationTests`（4 例）、`TsRawStatementNormalizerTests` +10 例；全量 `dotnet test` 绿（515+2424）。

## Round 5 进行中 — 当前 593 个错误全为语法错误

注意：TS 7 原生编译器在项目存在语法错误时只报语法错误、抑制语义错误；语法清零后语义层（未定义名、类型不匹配等）会浮现，需继续迭代。

按根因分类（文本出现次数为全库统计）：

| 根因 | 规模 | 错误码 |
|------|------|--------|
| 方法引用 `::`（`this::x`、`X[]::new`、`Integer::intValue`、`Runnable::run`） | ~103 处 | TS1005/TS1135/TS1128 |
| Stream 链（`Stream.concat`、`Arrays.stream`、`.stream()`、`.toArray(X[]::new)`、`IntStream.range(...).boxed()`） | ~144+ | TS1109/TS1005/TS1135 |
| Java 数组维度创建 `new T[n][]` / `new T[n]` | 45 处 | TS1011 |
| 声明未 let 化：尾 `//` 注释、FQCN 类型、`[]` 类型、嵌套泛型 | 37+ | TS1434/TS1005 |
| `catch (Exception __e)` 类型化捕获 | 10+ | TS1128/TS1005 |
| 增强 for 集合为带点方法调用（`for (T v : a.b.c())`） | 多处 | TS1005 |
| `synchronized (this)`、`0.0d`、`self.new X(...)`、`self.0`、`if return true;`、`Collections.<T>emptyList()`、Java lambda `->` | 少量 | 混合 |
