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

## Iteration AGL-NullIterable — `CSharpGenericIterable.from(null)` 吞掉 null 语义导致 Rectilinear NPE

- **Java 文件**: `msagltests/src/test/java/Microsoft/Msagl/UnitTests/Rectilinear/RectilinearEdgeRouterWrapper.java:524`（`nudgePaths` 中 `edgePath.getPathPoints().ToList()`）
- **错误信息**: `NullPointerException: Cannot invoke "java.lang.Iterable.iterator()" because "this.val$iterable" is null`（`io.github.ningpp.compat.CSharpGenericIterable$1.iterator`）
- **对应 C# 文件**:
  - `E:\agl-master\GraphLayout\MSAGL\Routing\Rectilinear\MsmtRectilinearPath.cs`
  - `E:\agl-master\GraphLayout\MSAGL\Routing\Rectilinear\SsstRectilinearPath.cs`
  - `E:\agl-master\GraphLayout\MSAGL\Routing\Rectilinear\RectilinearEdgeRouter.cs`
- **根因分类**: compat
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpGenericIterable.java`
- **分析**: C# `SsstRectilinearPath.RestorePath(null)` 返回 `null`，于是 `Path.PathPoints = null` → `GeneratePath` 返回 false → `EnsureNonNullPath`/重试兜底。转换器生成的 Java 用 `CSharpGenericIterable.from(...)` 包装 `IEnumerable<T>` 返回值，而 `from(null)` 构造了一个“外层非 null、内部 iterable 为 null”的匿名对象，`PathPoints != null` 被误判为路径成功，跳过兜底；最后 `nudgePaths` 对 `PathPoints` 做 `ToList()` 时枚举到 null iterable 抛 NPE。10 个 Rectilinear 用例（OutsideGroup / Landlock / Obstacle crossing / Spatial parent / FreePort）全部是同一根因。
- **修复**: `from(Iterable)` 与 `from(String)` 对 null 输入返回 null，保持 C# 引用语义；新增 `CSharpGenericIterableNullSemanticsTest`（4 个用例：null iterable / null string / 非 null 包装 / CSharpList 保持同一实例）。
- **验证**: compat `mvn test` 1500 个测试 0 失败；AGL `mvn clean package -e` 609 个测试 0 失败 0 错误，`BUILD SUCCESS`。
- **状态**: ✅ Fixed

## 最终复验（2026-08-16 第三次）

- 子任务一：使用同一份 cs2j 代码重新转换 `D:\csharp-sdk` → `d:\cs-java-816-sdk`，`mvn clean install -e` → **BUILD SUCCESS**（7102 个测试，0 失败）
- 子任务二：使用同一份 cs2j 代码重新转换 `E:\agl-master\GraphLayout\` → `d:\agl-java-816`，`mvn clean package -e` → **BUILD SUCCESS**（10 个模块，609 个测试 0 失败 0 错误）
- compat：`mvn test` → **1500 个测试全部通过，0 失败**（含新增 `CSharpGenericIterableNullSemanticsTest`）

## Iteration AGL-1 — 未报告的已检查异常 FileNotFoundException（if 块内 throw 未被包裹）
- **Java 文件**: `aglningpptests/src/test/java/AglNingppTests/Agl/Layout/LayeredV2Tests/LayeredV2TestSupport.java:207`（同模式 `RoutingTestSupport.java:117`）
- **行号**: 207
- **错误信息**: `未报告的异常错误io.github.ningpp.compat.FileNotFoundException; 必须对其进行捕获或声明以便抛出`
- **代码片段**:
  ```java
  if (!FileHelper.exists(path)) {
      throw new FileNotFoundException("Missing SciPy reference data: " + path);
  }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\Test\AglNingppTests\Agl\Layout\LayeredV2\LayeredV2TestSupport.cs`
- **根因分类**: Transformer（异常重写器覆盖不足）
- **涉及组件**: `src/CSharpToJava.Core/Java/Rewriters/JavaExceptionCheckRewriter.cs`
- **分析**: 所有控制流语句（if/for/while/switch）由 StatementTransformer 以原始字符串（`JavaStatementNode`/`JavaRawStatement`）发射，`JavaExceptionCheckRewriter.VisitThrowStatement` 只能看到方法体最外层的结构化 throw，嵌套在 if 块内的 `throw new FileNotFoundException(...)` 永远无法触发 try-catch 包裹，导致 javac 报未报告异常。
- **修复**: `VisitRawStatement` 新增 `ThrowNewExceptionPattern`（`throw new X(` 正则）扫描，解析类型后经 `ResolveCanonical`/`IsUnchecked` 判断，仅对已检查异常加入 pending 集合触发方法体包裹；unchecked（IllegalArgumentException、compat SystemException 子类等）不受影响。
- **测试**: 新增 `FileNotFoundException_ThrownInIfBlock_MustCompile`（项目管线 + javac 编译验证）；全量 `dotnet test` 2872 通过 0 失败。
- **状态**: ✅ Fixed

## Iteration AGL-2 — 跨项目调用递归泛型方法被误注入 Class 参数

- **Java 文件**: `msagltests/src/test/java/Microsoft/Msagl/UnitTests/Rectilinear/RectilinearEdgeRouterWrapper.java`、`aglningpptests/src/test/java/AglNingppTests/Agl/Routing/RectilinearTests/RectilinearEdgeRouterWrapper.java`
- **错误类型**: `cannot find symbol` — 调用点生成 `crossRectangleNodes_2tp(Obstacle.class, Point.class, ...)`，而目标签名 `crossRectangleNodes_2tp(RectangleNode<TA,P>, RectangleNode<TA,P>, BiConsumer<TA,TA>)` 没有 Class 参数
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Core\Geometry\RectanglePacking\RectangleNodeUtils.cs`
- **根因分类**: 语义丢失（跨编译单元 `GetSymbolInfo` 返回 null）
- **涉及组件**:
  - `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（`DetectDefaultUsageInMethod`）
  - `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs`（`DetectTypeOfDefaultUsage`）
- **分析**: `RectangleNodeUtils.CrossRectangleNodes<TA,P>` 无 `typeof(T)`/`default(T)`，不应带 Class 参数；但测试项目转换时库源码作为源码可达，CRN 递归体 `CrossRectangleNodes<TA,P>(...)` 因 `inv.SyntaxTree` 属于另一编译单元、`ProjectCompilation.ContainsSyntaxTree(...) == false` 导致 `GetSymbolInfo` 返回 null；两处宽松的 `callee == null` 转发逻辑把 null 当作"需要 Class 令牌"，把 `TA`/`P` 注册为 CRN 的 Class 参数，后续调用点全部前置 `.class` 字面量。
- **修复**: `DetectDefaultUsageInMethod` 由 `explicitCallee == null || MethodLikelyNeedsClassTokens(...)` 改为 `explicitCallee != null && MethodLikelyNeedsClassTokens(...)`；`DetectTypeOfDefaultUsage` 由 `calleeSym != null && !MethodLikelyNeedsClassTokens(...)` 改为 `calleeSym == null || !MethodLikelyNeedsClassTokens(...)`。跨编译单元无法解析符号时不再据此注入 Class 参数。
- **状态**: ✅ Fixed

## Iteration AGL-8 — Flags 枚举 toString 方法体重复嵌套签名

- **Java 文件**: `automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Geometry/Direction.java`（同批所有 [Flags] 枚举类）
- **行号**: 89 / 106
- **错误信息**: `非法的表达式开始` / `需要 class、interface、enum 或 record`
- **代码片段**:
  ```java
  public static String toString(int value) {
      public static String toString(int value) {
      if (value == 0) { return "None"; }
      ...
      }
  }
  ```
- **对应 C# 文件**: `E:\agl-master\GraphLayout\MSAGL\Core\Geometry\Direction.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/EnumTransformer.cs`（AddFlagsToString）
- **分析**: 新增的 flags 枚举 `toString(value)` 合成方法把方法签名 `public static String toString(...) {` 和收尾 `}` 一起塞进 `JavaMethodDeclaration.Body`，而渲染器本身已生成签名与花括号，导致签名嵌套重复、类体提前闭合。
- **修复**: AddFlagsToString 的 Body 只保留语句（去掉签名前缀与结尾花括号）；FlagsEnumToStringTests 增加 javac 编译校验（拆分顶层类型文件）。
- **状态**: ✅ Fixed

## Iteration AGL-9 — 元组交换 (a,b) = (b,a) 被拆成顺序赋值导致置换损坏

- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Layout/Ordering/OrderingMatrix.java（及相关调用点）
- **错误信息**: OrderingMatrixTest/OrderingTest/OrderingV2Test 中 "Not optimal for seed N" / "did not reach brute-force optimum"
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\Layout\Ordering\OrderingMatrix.cs（Permute 中的元组交换）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs（TupleAssignmentSplitters.SplitTupleAssignment）
- **分析**: 转换器把 (a,b) = (b,a) 拆成顺序赋值 a = b; b = a;，两个值都被破坏。Permute 生成的排列退化为 [5,5,5]/[2,2,2]，暴力最小值为 0 而真实最小值为 1，导致排序测试断言失败。C# 参考测试（临时副本）5/5 通过，差异 harness 证明图边完全一致，问题只在元组交换。
- **修复**: SplitTupleAssignment 收集 RHS 文本与 LHS 目标文本，检测重叠（RHS 文本包含某个 LHS 目标字符串）；若重叠，先把每个 RHS 提升为 var _cs2jTupleTmpN = ... 再依次赋值。非重叠场景输出不变（既有测试仍固定 this.x = x; 等）。新增 TupleAssignmentTests.TupleSwapWithOverlappingElements_HoistsTemps（Red→Green），完整转换器测试 2898/2898 通过。
- **状态**: ✅ Fixed

## Iteration AGL-10 — 嵌套对象初始化器 Prop = { Sub = v } 被整体丢弃

- **Java 文件**: aglningpptests/src/test/java/AglNingppTests/Agl/Layout/LayeredTests/SugiyamaLayoutSettingsTest.java（settings_Clone_CopiesValues_Test）
- **错误信息**: Expected <StraightLine> but was <SugiyamaSplines>
- **对应 C# 文件**: E:\agl-master\GraphLayout\Test\AglNingppTests\Agl\Layout\Layered\SugiyamaLayoutSettingsTest.cs（EdgeRoutingSettings = { EdgeRoutingMode = StraightLine }）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs（TransformObjectCreationWithInitializer）
- **分析**: 嵌套对象初始化器（RHS 是 ObjectInitializerExpression）经 facade.Transform 得到 "/* TODO: ObjectInitializerExpression ... */" 占位符，被"跳过 TODO 赋值"逻辑整体丢弃，导致 clone 前从未设置 EdgeRoutingMode。C# 语义是对 getter 返回的现有对象做原地修改，不是 setter 赋值新对象。
- **修复**: 新增 BuildNestedInitializerAssignments：把 Prop = { A.B = v, C = { D = w } } 展开为 getProp().getA().setB(v) / getProp().getC().getD().setD(w) 链式调用；TODO 跳过逻辑对嵌套对象初始化器放行。新增 ObjectInitializerMethodChainTests.NestedObjectInitializer_ShouldEmitGetterSetterChain 与 DeeplyNestedObjectInitializer_ShouldEmitChainedGettersAndSetter（Red→Green）。
- **状态**: ✅ Fixed

## Iteration AGL-11 — 可空结构体与 null 比较被转成值比较方法调用导致 NPE

- **Java 文件**: automaticgraphlayout/src/main/java/Microsoft/Msagl/Core/Layout/EdgeLabelPlacement.java（pointSetLength）
- **行号**: 378
- **错误信息**: NullPointerException: Cannot read field "X" because "point0" is null（Point.valueEquals → Point.notEquals）
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Core\Layout\EdgeLabelPlacement.cs（Point? q = null; if (q != null)）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs（用户定义运算符分支）
- **分析**: Point? 与 null 字面量的 != 在 C# 中是提升运算符（HasValue 检查），不会调用底层值运算符；转换器却把 q != null 生成 Point.notEquals(q, null)，valueEquals 直接解引用 null 参数抛 NPE。
- **修复**: 新增 IsStructNullLiteralComparison 守卫：结构体（值类型）运算符与 null 字面量比较时跳过方法调用路径，回落到普通 Java ==/!= 引用比较（Java 中结构体映射为类，null 即"无值"）。IR 路径与字符串路径两处用户定义运算符分支同步修复。新增 StructTransformerTests.Struct_NullableComparisonAgainstNull_StaysReferenceCheck（Red→Green）。
- **状态**: ✅ Fixed


## Iteration AGL-12 — StaircaseRemover 数组身份语义丢失 + stream.toArray 误判为已包装

- **Java 文件**: aglningpptests/src/test/java/AglNingppTests/Agl/Routing/Rectilinear/NudgingTests/NudgingHelpersTest.java（staircaseRemover_RemovesStaircase_Test）
- **错误信息**: 去掉 C# [Ignore] 后转换出的 Java 测试无法通过/主模块编译报错：ConstrainedOrdering.java:249、Nudger.java:310、PathRefiner.java:88（T[] 无法转换为 CSharpGenericIterable<T>）
- **对应 C# 文件**: E:\agl-master\GraphLayout\MSAGL\Routing\Rectilinear\Nudging\StaircaseRemover.cs（SegWithIndex.Equals 按数组引用比较）、Path.cs（PathPoints）、Nudger.cs / PathRefiner.cs
- **根因分类**: Transformer + compat 库
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs（IsArrayAlreadyWrappedForCollectionOrIterable / WrapArrayForIterableTarget）、AssignmentTransformer.cs / StatementTransformer.Declarations.cs、java/csharptojava-compat（ArrayHelper.asIterable / CSharpArrayIterable）
- **分析**: 两个叠加问题：(1) C# 中 array 赋给 IEnumerable<T> 属性/局部变量时保存数组本身，(T[]) 强转回原数组；StaircaseRemover 依赖 SegWithIndex.Equals 的 Points 引用相等来忽略自身线段。转换器此前用 ArrayHelper.toList 复制，破坏身份，导致楼梯永远无法移除。(2) Enumerable.ToArray() 生成 StreamSupport.stream(...).toArray(T[]::new)，表达式以 StreamSupport.stream( 开头被 IsArrayAlreadyWrappedForCollectionOrIterable 误判为"已包装"，IEnumerable 目标处不再包 asIterable，产生 T[] 传给 CSharpGenericIterable<T> 参数的编译错误。
- **修复**: (1) compat 新增 CSharpArrayIterable（数组后备、toArray 返回同一实例），ArrayHelper.asIterable 供 IEnumerable 目标使用；属性赋值/局部声明/方法实参三处统一改用 asIterable。(2) IsArrayAlreadyWrappedForCollectionOrIterable 对以 .toArray(...) 结尾的表达式返回 false（那是原始 Java 数组，仍需包装）。新增 IEnumerableArrayIdentityTests 3 个用例（LocalIEnumerableDeclaredFromToArrayCall / IEnumerablePropertyAssignedToArrayFromToArrayCall / GenericMethodArgumentFromToArrayDeclaredLocal），Red→Green；转换器全量 2964/2964 通过；重新转换后 mvn：aglningpptests 1661/1661、msagltests 612/612 全部通过，NudgingHelpersTest 10/10（含 staircaseRemover_RemovesStaircase_Test）。
- **状态**: ✅ Fixed
