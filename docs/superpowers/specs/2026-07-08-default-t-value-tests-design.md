# default(T) 转换单元测试设计文档

- 日期：2026-07-08
- 作者：AI 助手（基于 brainstorming 流程）
- 状态：已批准（待实现）

## 1. 目标

针对 C#→Java 转换器中 `default(T)`（显式形式）与裸 `default` 字面量（C# 7.1+）的转换逻辑，编写**完整**的单元测试，并在三个层级验证转换正确性：

1. **单代码片段**（single code snippet）
2. **csproj 项目**（project）
3. **.sln 解决方案**（solution）

测试断言**正确的 Java 输出**；若正确期望与转换器当前行为不一致，则修复转换器（`TypeOperationTransformer.GetDefaultValueForType` 等）使测试通过。既有的分散 `default` 测试（见 §6）保留，并确保修复后依然通过。

## 2. 背景与现状

- 转换器是基于 Roslyn 的 C# 分析器，自身为 C# (.NET 10)。
- `default` 转换集中在 `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`：
  - `TransformDefault`（行 1013，处理 `default(Type)`）
  - `TransformDefaultLiteral`（行 1035，处理裸 `default`）
  - `GetDefaultValueForType`（行 1055，核心映射）
- 测试框架为 xUnit，分布在：
  - `tests/CSharpToJava.Tests/`（核心单测，可访问 internal API）
  - `tests/CSharpToJava.ConversionTests/`（正确性分类测试，基类 `ConversionTestBase`）
  - `tests/CSharpToJava.ProjectTests/`（csproj/sln 级测试）
- 单片段入口：`ConversionPipeline.Convert(ConversionRequest)`。
- 项目入口：`ConversionPipeline.ConvertProjectWithPartialMergeAsync(projectPath, options)`。
- 解决方案入口：`SolutionLoader.OpenSolutionAsync(solutionPath, ...)` 加载各 `WorkspaceProject`，再对每个项目用 `ProjectConversionPipeline.ConvertProjectAsync(compilation, ...)`（CLI 的 `ConvertProject` 也按 `.sln → .csproj` 顺序自动选择，但无独立公共 `ConvertSolution` 方法）。

## 3. 样例夹具（新建 `tests/SampleDefaultSolution/`）

新建一个专门的样例解决方案，同时供 csproj 级与 sln 级测试使用：

```
tests/SampleDefaultSolution/
  SampleDefaultSolution.sln
  SampleDefaultLib/
    SampleDefaultLib.csproj
    Defaults.cs        // struct / enum / 泛型(T:struct, T:class, 无约束) / tuple 的 default(T)
  SampleDefaultApp/
    SampleDefaultApp.csproj   // 引用 SampleDefaultLib
    Program.cs        // 基本类型 / int? 可空 / 嵌套泛型 / 各语法位置的 default
```

- `SampleDefaultLib`：类库，含用户定义 struct、enum（含 `[Flags]` 与不含 0 值成员两种）、泛型类（不同约束）、tuple 的 `default(T)` 用法，并刻意放在字段初始化、方法返回值、方法参数默认值、数组元素等位置。
- `SampleDefaultApp`：引用 Lib，含基本类型默认值、`int?` 可空值类型、嵌套泛型（如 `Dictionary<int, string>`）、以及 `default` 出现在字段初始化/返回值/方法参数/数组元素等位置。
- 该夹具的所有 `.cs` 在转换后不应残留 `=> ?. ?? $" nameof(` 等 C# 语法。

## 4. 单元测试文件与用例

### 4.1 单代码片段级（主力）

新建 `tests/CSharpToJava.ConversionTests/Categories/DefaultValueTests.cs`，继承 `ConversionTestBase`，使用 `[Theory]` + `[MemberData]` 参数化。分类（每类一个嵌套测试类或一组 Theory）：

| 分类 | C# 输入示例 | 期望 Java 输出（标记） |
|---|---|---|
| 基本类型 | `int x = default;` | `int x = 0;` |
| | `long x = default;` | `0L` |
| | `short x = default;` | `(short)0` |
| | `byte x = default;` | `0` |
| | `float x = default;` | `0.0f` |
| | `double x = default;` | `0.0` |
| | `bool x = default;` | `false` |
| | `char x = default;` | `'\0'` |
| | `decimal x = default;` | `Decimal.ZERO`（含 `import io.github.ningpp.compat.Decimal`） |
| | `string x = default;` | `null` |
| struct | `Point p = default;` | `new Point()` |
| enum（含 0 值成员） | `Status s = default;` | `Status.Full`（0 值成员名） |
| enum（不含 0 值成员） | `Color c = default;` | 应为值为 0 的表示（核实并修正为正确形式） |
| enum（`[Flags]`） | `Flags f = default;` | `0` |
| 泛型 `T:struct` | `T v = default;` | `new T()` |
| 泛型 `T:class` | `T v = default;` | `null` |
| 泛型无约束（类级） | 类级 TP | `_cs2jDefault_T()` |
| 泛型无约束（方法级） | 方法级 TP | `DefaultValue.of(_cs2j_T)`（含 `import io.github.ningpp.compat.DefaultValue`） |
| tuple | `var t = default((int, string));` | `Tuple.of(0, null)`（含 `import io.vavr.Tuple`） |
| 可空值类型 | `int? n = default;` | `null` |
| 嵌套泛型 | `var d = default(Dictionary<int,string>);` | `null` |
| 数组 | `int[] a = default;` | `null` |
| 语法位置 | 字段初始化/返回值/方法参数/数组元素 | 各自正确标记 |
| 显式 vs 裸字面量 | `default(int)` 与 `int x = default;` | 等价（均为 `0`） |

注意：`default(int?)` 的正确 Java 语义是 `null`（C# 中可空值类型默认值为 null），需核实当前实现是否误生成 `new ...()` 并修正。

### 4.2 csproj 级

新建 `tests/CSharpToJava.ProjectTests/DefaultValueProjectTests.cs`：
- 调用 `ConvertProjectWithPartialMergeAsync("tests/SampleDefaultSolution/SampleDefaultLib/SampleDefaultLib.csproj", options)`。
- 断言返回结果非空、`Success` 且生成了对应 `.java` 文件。
- 选取 `Defaults.cs` 中已知方法/字段，断言其转换后的 Java 含正确 `default` 标记（如 `new Point()`、`Status.Full`、`Tuple.of(...)`）。

### 4.3 sln 级

新建 `tests/CSharpToJava.ProjectTests/DefaultValueSolutionTests.cs`：
- 用 `SolutionLoader.OpenSolutionAsync("tests/SampleDefaultSolution/SampleDefaultSolution.sln", ...)` 加载全部项目。
- 对每个 `WorkspaceProject` 取 `CSharpCompilation` 并调用 `ProjectConversionPipeline.ConvertProjectAsync`。
- 断言解决方案下所有项目均转换成功（数量与夹具项目数一致），且 Lib/App 中的 `default(T)` 正确转换（`AssertJavaContains` 关键标记）。
- 同时验证无 C# 残留（`AssertNoCSharpResidue` 思想：生成代码不含 `=> ?. ?? $" nameof(`）。

## 5. 断言策略

- 单片段：复用 `ConversionTestBase.AssertConversion` / `AssertJavaContains` / `AssertJavaDoesNotContain` / `AssertNoCSharpResidue`。
- 项目/解决方案级：先断言 `Success`，再对关键文件做 `AssertJavaContains` 抽样验证；不要求整文件精确匹配，只验证 `default` 相关转换正确且无 C# 残留。
- 这些测试验证**语法正确性**（生成代码形态），不要求 Java 可编译运行（如 `DefaultValue.of` / `_cs2jDefault_T` 来自外部兼容库，仅验证生成形态）。

## 6. 既有的 default 相关测试（保留并回归）

- `tests/CSharpToJava.Tests/EnumTransformerTests.cs`（`DefaultExpression_EnumType_ReturnsZeroMember` 等）
- `tests/CSharpToJava.Tests/StructTransformerTests.cs`（`GenericTypeParam_DefaultExpression_EmitsNewStruct` 等）
- `tests/CSharpToJava.Tests/TupleSupportTests.cs`（`TupleDefaultExpression_...` 等）
- `tests/CSharpToJava.Tests/PhaseCExpressionIRTests.cs`（`DefaultValue.of(_cs2j_TValue)` 等）
- `tests/CSharpToJava.Tests/DecimalMappingTests.cs`
- `tests/CSharpToJava.ConversionTests/Categories/PrimitiveTypeTests.cs`

修复转换器后须确保上述全部仍通过（运行受影响测试项目）。

## 7. 易错点 / 需核实并可能修复的项

1. `default(int?)` 可空值类型：当前 `GetDefaultValueForType` 对 `Nullable<T>` 可能误走 struct 分支返回 `new ...()`，应为 `null`。
2. `[Flags]` enum：`default` 应返回 `0`（当前已实现，需回归确认）。
3. 不含 0 值成员的 enum：`default` 的正确表示需核实（当前可能返回 `values()[0]`，需确认是否符合预期并修正）。
4. 嵌套泛型 / 数组：应返回 `null`，需回归确认。
5. 方法级无约束泛型：返回 `DefaultValue.of(_cs2j_T)` 且正确注入 `import` 与运行时 `Class<T>` 参数。

## 8. 交付物

1. `docs/superpowers/specs/2026-07-08-default-t-value-tests-design.md`（本文件，已提交）
2. 样例夹具 `tests/SampleDefaultSolution/`（`.sln` + 多个 `.csproj` + `.cs`）
3. `tests/CSharpToJava.ConversionTests/Categories/DefaultValueTests.cs`
4. `tests/CSharpToJava.ProjectTests/DefaultValueProjectTests.cs`
5. `tests/CSharpToJava.ProjectTests/DefaultValueSolutionTests.cs`
6. 必要时修复 `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`
7. 运行受影响测试项目，确保单片段/项目/解决方案三级全部通过。

## 9. 范围与排除

- 不重写现有测试基础设施（仅新增文件，保留既有默认相关测试）。
- 不改动与 `default` 无关的转换逻辑。
- 不要求生成的 Java 实际编译/运行（仅验证形态正确）。
