# AGL Error Analysis

## Iteration AGL-WinExclude — Windows/WPF 项目误放行 + 三个中间目录逻辑不一致

- **现象**: `.cs2j-readonly-src` 出现构建产物并包含 Windows/WPF 项目；最终生成 `*-windows` Maven 模块并在 `agl/Program.java` 报 `Application`/`CheckBox`/`bringToFront` 缺失。
- **根因分类**: 项目排除规划 + 预处理共享逻辑
- **涉及组件**:
  - `src/CSharpToJava.CLI/ProjectConversionExclusionPlanner.cs`
  - `src/CSharpToJava.CLI/SupportedProjectFiltering.cs`
  - `src/CSharpToJava.CLI/ProjectPreprocessSource.cs`（新增共享源准备）
  - `src/CSharpToJava.CLI/ProjectUnsignedTypePreprocessor.cs`
  - `src/CSharpToJava.CLI/ProjectReadOnlyStructPreprocessor.cs`
  - `src/CSharpToJava.CLI/ProjectGotoPreprocessor.cs`
- **分析**: `88a0804c` 将硬编码 allow-list 改为“namespaceMappings 自动放行”，而 `TypeMappings.json` 有 `System.Windows.Forms`/`System.Windows` 映射，导致 Windows/WPF 项目被重新放行。另外 readonly 预处理器在中间目录内执行 `dotnet build/test`，把 `bin/obj` 写回源码树，三个中间目录行为不一致。
- **修复**: WindowsDesktop/WPF/WinForms/windows-only TFM 一律视为不支持的 Windows 项目；抽取共享 `ProjectPreprocessSource`，三个预处理器统一解析、复制、过滤、restore；readonly 验证构建后清理 `bin`；目录扫描额外排除非 solution 下的 Windows/WPF 项目。
- **状态**: ✅ Fixed

## Iteration AGL-MSTest-Throws — `Assert.ThrowsException<T>` 丢失泛型类型参数

- **Java 文件**: `aglningpptests/.../DgmlParserTests.java:104,109`
- **错误信息**: `Assert.throwsException(Supplier<Object>)` 找不到合适方法
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- **分析**: MSTest `Assert.ThrowsException<T>` 未像 xUnit 那样把 `<T>` 转成 `T.class` 前置参数。
- **修复**: 新增 `TryGetExceptionClassLiteral` 共享逻辑，MSTest `throwsException` 同样前置 `FileNotFoundException.class` 等 class literal。
- **状态**: ✅ Fixed

## Iteration AGL-StringAssert — compat 缺少 StringAssert

- **Java 文件**: `GraphToStringTests.java:36`
- **错误信息**: `找不到符号: 类 StringAssert`
- **根因分类**: compat 缺失
- **涉及组件**: `java/csharptojava-compat/src/main/java/Microsoft/VisualStudio/TestTools/UnitTesting/StringAssert.java`（新增）
- **分析**: MSTest `StringAssert.StartsWith/EndsWith/Contains` 没有 compat 层。
- **状态**: ✅ Fixed

## Iteration AGL-XDocument — 缺失文件异常被 XmlException 包装

- **Java 文件**: `DgmlParserTests.java:108`
- **错误信息**: 期望 `FileNotFoundException`，实际抛 `XmlException`
- **根因分类**: compat
- **涉及组件**: `Assert.java`、`XDocument.java`
- **分析**: `XDocument.load` 把 `java.io.FileNotFoundException` 包装成 `XmlException`，而 C# 测试期望 `FileNotFoundException`；Java 端 checked exception 又不能在生成代码中直接透传。
- **修复**: `Assert.throwsException` 在匹配失败时沿 cause 链查找期望异常，并把 `java.io.FileNotFoundException` 桥接为 compat `FileNotFoundException`。
- **状态**: ✅ Fixed

## Iteration AGL-StringWriter — `StringWriter.WriteLine` 硬编码 `\n`

- **Java 文件**: `Graph.java:105`
- **错误信息**: Graph.ToString 换行与 `Environment.NewLine`（`System.lineSeparator()`）不一致
- **根因分类**: Transformer
- **涉及组件**: `InvocationExpressionTransformer.cs`
- **分析**: `StringWriter.WriteLine` 被转成 `write(expr + "\n")`，应使用平台换行 `System.lineSeparator()`。
- **状态**: ✅ Fixed

## Iteration AGL-DoubleFormat — double 字符串化不符合 .NET

- **Java 文件**: `RealNumberSpan.java`、`Node.java`、`GraphAttr.java` 等
- **错误信息**: 期望 `5,5`/`pos="0,675.0625"`，实际 `5.0,5.0`/`pos="0.0,675.0625"`
- **根因分类**: Transformer + compat
- **涉及组件**:
  - `java/csharptojava-compat/.../DoubleHelper.java`（新增）
  - `BinaryExpressionTransformer.cs`
  - `InvocationExpressionTransformer.cs`
- **分析**: Java `String + double` 和 `String.format("%s", double)` 输出 `36.0`，.NET 默认格式输出 `36`。
- **修复**: 新增 `DoubleHelper.toString`；字符串 `+` 拼接和普通 `{N}` 占位符参数自动包 `DoubleHelper.toString`；带 `{N:F2}` 等格式说明符的参数保持数值。
- **状态**: ✅ Fixed

## Iteration AGL-Collection — CollectionAssert/comparer/数组到 IEnumerable

- **Java 文件**: `RbTreeTests.java`、`RTreeTests.java`
- **错误信息**: `CollectionAssert.areEquivalent` 缺失、`CSharpDefaultComparerGeneric.create` 缺失、`int[]`/`String[]` 未包装为 `CSharpGenericIterable`
- **根因分类**: compat + Transformer
- **涉及组件**:
  - `CollectionAssert.java`
  - `CSharpDefaultComparerGeneric.java`
  - `InvocationExpressionTransformer.cs`
- **修复**: 新增 `areEquivalent`（多重集合比较）；新增 `create(Comparator<T>)`；`CreateRectangleNodeOnData` 数组参数包装；`IntStream.range(...).toArray()` 不再被误判为已包装集合。
- **状态**: ✅ Fixed

## Iteration AGL-TypeHelper — C# internal 方法反射 NonPublic 找不到

- **Java 文件**: `MinDistCurveCurveTest.java:46,47`
- **错误信息**: `SolveMethod`/`DeltaMethod` 为 null
- **根因分类**: compat
- **涉及组件**: `TypeHelper.java`
- **分析**: C# internal 方法在 Java 中转为 public，`BindingFlags.NonPublic` 反射找不到。
- **修复**: `NonPublic` 单独请求且未指定 `Public` 时，允许回退匹配 public Java 成员。
- **状态**: ✅ Fixed

## Iteration AGL-PointReadOnly — `[Serializable]` 可变字段 struct 被反射安全规则跳过

- **Java 文件**: `automaticgraphlayout/.../Microsoft/Msagl/Core/Geometry/Point.java`
- **错误信息**: `make-readonly` 输出 `Point: serialization/mapping attribute present (reflection mutation risk)`，Point 保持可变 `public class`，未按 readonly struct / value class 输出。
- **根因分类**: 配置驱动缺失 + Transformer
- **涉及组件**:
  - `config/ReadOnlyStructOverrides.json`（新增 force-readonly 配置）
  - `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructMakerOptions.cs`
  - `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`
  - `src/CSharpToJava.CLI/Program.cs`
- **分析**: `StructAnalyzer` 对带序列化/映射特性且含可变状态、无迁移方法的 struct 一律跳过。Point 因 `[Serializable]` 被跳过，但转换后的 Java 并不存在 .NET 反射序列化语义，保留可变形状反而破坏值类型语义。
- **修复**: 新增配置驱动的 `ForceReadOnlyQualifiedNames` 覆盖机制，命中完全限定名时仅绕过反射安全（boxing/序列化）跳过，其余不可转换守卫保留；CLI 默认加载 `config/ReadOnlyStructOverrides.json`，AGL 转换强制把 `Microsoft.Msagl.Core.Geometry.Point` 转为 readonly。
- **状态**: ✅ Fixed

## Iteration AGL-LinqFirstContains — `First()/Last()` 返回数组后误加 `.boxed()`

- **Java 文件**: 生成的 `CollectionTests`/`Sample.java`（回归测试 `FirstElementContainsOnArrayTests.cs`）
- **错误信息**: `result.First().Contains(1)` 生成 `...findFirst().orElseThrow().boxed()...`，`orElseThrow()` 已返回 `int[]`，`int[].boxed()` 非法。
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- **分析**: `Contains` 处理以 `containsSourceType` 是否为原始数组判断，但 `ContainsStreamMethodAtTopLevel` 对 `...findFirst().orElseThrow()` 误判为流表达式，导致直接给数组值追加 `.boxed()`。
- **修复**: C# 接收表达式为方法调用（如 `First()`/`Last()`/`ToArray()`）且 C# 类型为原始数组时，一律用 `Arrays.stream(receiver).boxed().collect(Collectors.toSet()).contains(...)`，避免把终态数组当流处理；直接 `int[].Contains(...)` 的双重包装保护保持不变。
- **状态**: ✅ Fixed

## 最终复验（2026-08-16 第二次）

- 子任务一：使用同一份 cs2j 代码重新转换 `D:\csharp-sdk` → `d:\cs-java-816-sdk`，`mvn clean install -e` → **BUILD SUCCESS**（7102 个测试，0 失败）
- 子任务二：使用同一份 cs2j 代码重新转换 `E:\agl-master\GraphLayout\` → `d:\agl-java-816`，`mvn clean package -e` → **BUILD SUCCESS**（10 个模块，无 Windows/WPF 模块；`Point.java` 为 `public value class`，`X`/`Y` 为 `public final double`，带 `withX/withY`）
- 全量测试：`dotnet test -c Release` → **2830 + 51 全部通过，0 失败**
