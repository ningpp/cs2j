# SDK 转换错误分析（sdk-error-analysis.md）

目标：`D:\csharp-sdk` → `d:\cs-java-812`，迭代修复转换器代码直至 `mvn clean install -e` 零错误 `BUILD SUCCESS`。

**最终状态：✅ BUILD SUCCESS（EXIT=0）** — `d:\cs-java-812` 与 `d:\cs-java-813` 均验证通过；cs2j 全量单测 3386 个全部通过。

---

## Iteration 1 — make-readonly 嵌套结构体迁移丢失（CS1501/CS1061/CS0019）
- **Java 文件**: （C# 中间产物）`xml\src\System\Xml\Schema\XsdDateTime.cs` 等 16 处错误
- **错误信息**: `Parse` 无 3 参重载 / `Namespace` 无 `WithprevNsIndex` / `+` 不能用于 `TagInfo`
- **代码片段**:
  ```csharp
  if (!parser.Parse(text, kinds, out parser))   // 调用点被加 out 实参，但 Parser 未被迁移
  ```
- **对应 C# 文件**: `D:\csharp-sdk\xml\src\System\Xml\Schema\XsdDateTime.cs`
- **根因分类**: Transformer（ReadOnlyStructMaker）
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/ReadOnlyStructRewriter.cs`
- **分析**: 外层结构体 L5 迁移时 `ReplaceNode`/`FieldAssignmentToWithRewriter.VisitBlock` 重建整棵子树，嵌套结构体的 Roslyn red-node 引用丢失，`analysisResults` 按引用查找失败 → 嵌套结构体被跳过，但 CallSiteUpdater 仍按其迁移信息改写调用点。
- **修复**: ① `TryGetResult` 增加 name+text 回退匹配（red 引用在树重建后不保留）；② `FieldAssignmentToWithRewriter.VisitBlock` 仅在真正变化时重建节点。
- **回归测试**: `ReadOnlyStructMakerL5Tests.NestedStruct_InsideMigratedStruct_IsMigratedToo`（含输出可编译断言，Red→Green 验证）。
- **状态**: ✅ Fixed

---

## Iteration 2 — 跨文件同名结构体误匹配（CS1061 Withkind/WithprevNsIndex）
- **Java 文件**: `xml\src\System\Xml\Core\XmlWellFormedWriter.cs`
- **错误信息**: `XmlWellFormedWriter.Namespace` 未包含 `Withkind`/`WithprevNsIndex` 定义
- **代码片段**:
  ```csharp
  _nsStack[namespaceIndex] = _nsStack[namespaceIndex].WithprevNsIndex(existingNsIndex);
  ```
- **对应 C# 文件**: `XmlWellFormedWriterHelpers.cs`（`private partial struct Namespace`，被跳过未转换）
- **根因分类**: Transformer（AssignmentRewriter 跨文件 pass 按裸名匹配）
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/AssignmentRewriter.cs`、`ProjectReadOnlyStructPreprocessor.cs`
- **分析**: `UpdateCrossFileFieldReads` 用结构体**裸名**建映射，`XmlTextWriter.Namespace`（已转换）与 `XmlWellFormedWriter.Namespace`（partial，未转换）同名混淆，语义模型解析到未转换的那个却仍被重写。
- **修复**: 目标结构体注册限定名（namespace+containing-type 链），`TryGetTargetStructName` 语义解析后按限定名匹配。
- **状态**: ✅ Fixed

---

## Iteration 3 — 表达式内自增自减重写错误（CS0019 TagInfo + int）
- **Java 文件**: `xml\src\System\Xml\Core\XmlTextWriter.cs`
- **错误信息**: `运算符“+”无法应用于 XmlTextWriter.TagInfo 和 int`
- **代码片段**:
  ```java
  int temp = _stack[_top] = _stack[_top].WithprefixCount(_stack[_top].prefixCount + 1) + 1;
  ```
- **对应 C# 文件**: `XmlTextWriter.cs`（`int temp = _stack[_top].prefixCount++ + 1;`）
- **根因分类**: Transformer（AssignmentRewriter 一元运算在表达式上下文中返回赋值表达式，无法保持值语义；且 hoister 缺少 Kind 守卫把 `!x.f` 当 `--x.f`）
- **涉及组件**: `AssignmentRewriter.cs`
- **修复**: 语句级 `x.f++` 原位改写；嵌套表达式中的自增/自减提升为临时变量 + With 写回语句（`VisitBlock` hoist），并补 Kind 检查（`!`/`x!` 不是自增自减）。
- **状态**: ✅ Fixed

---

## Iteration 4 — Parser 的 OtherReturnToOut 迁移静默丢状态（C# 测试失败）
- **Java 文件**: （C# 中间产物）`XsdDateTime.cs` — make-readonly 的 `dotnet test` 门禁 152 个失败
- **错误信息**: `The string '9999' is not a valid AllXsd value`
- **代码片段**:
  ```csharp
  if (ParseDate(start, out var _)) { ... }   // 状态被丢弃，后续读到默认值
  ```
- **对应 C# 文件**: `XsdDateTime.cs`（`private struct Parser`）
- **根因分类**: Transformer（StructAnalyzer 分类策略）
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`
- **分析**: OtherReturnToOut 迁移要求每个调用点捕获 out 状态；结构体**内部**裸调用（out 被丢弃）与自身字段 ref 传参（写回太晚）无法保持语义。
- **修复**: 分析器检测到内部使用模式（内部裸调用 other-return 迁移方法 / 表达式内以自身字段作 ref/out 实参）→ 判定 NotConvertible（保持可变）。
- **状态**: ✅ Fixed

---

## Iteration 5 — 副作用索引表达式被复制（XmlDocument 40 个失败）
- **Java 文件**: `XmlNamespacemanager.cs`（`_nsdecls[++_lastDecl].Set(...)` → `_nsdecls[++_lastDecl] = _nsdecls[++_lastDecl].Set(...)`）
- **错误信息**: `'xml' is an undeclared prefix`（命名空间状态损坏）
- **对应 C# 文件**: `XmlNamespacemanager.cs:160`
- **根因分类**: Transformer（CallSiteUpdater 副作用表达式重复求值）
- **涉及组件**: `CallSiteUpdater.cs`
- **分析**: `arr[i++].M()` → `arr[i++] = arr[i++].M()` 把 `++i` 求值两次且左右下标错位。
- **修复**: 接收者表达式中的副作用子表达式提升为临时变量（`var __cs2jExpr0 = ++i; arr[__cs2jExpr0] = arr[__cs2jExpr0].M(...)`）。
- **状态**: ✅ Fixed

---

## Iteration 6 — 迁移方法体内参数与字段同名被改写（XmlDocument 残留失败）
- **Java 文件**: `XmlNamespacemanager.cs`（`NamespaceDeclaration.Set` 迁移后 `result = result.Withprefix(result.prefix)`）
- **错误信息**: 命名空间声明全部失效（40 个 XmlDocument 失败）
- **对应 C# 文件**: `XmlNamespacemanager.cs`（`void Set(string prefix, ...) { this.prefix = prefix; }`）
- **根因分类**: Transformer（MethodMigrator.ImplicitFieldToResultRewriter 未跟踪方法参数）
- **涉及组件**: `MethodMigrator.cs`
- **分析**: rewriter 只作用于方法体 Block，参数名不在 `_localNames` 中；与字段同名的参数（`prefix`）被错误改写为字段读取，参数值静默丢失。
- **修复**: 构造 rewriter 时播种方法参数名（含 setter 的 `value`）。
- **状态**: ✅ Fixed

---

## Iteration 7 — Task 类型映射不一致（AsyncHelper cannot find symbol）
- **Java 文件**: `dotnet/xml/AsyncHelper.java` — `Task.completedFuture(...)` 找不到符号
- **对应 C# 文件**: `xml\src\System\Xml\AsyncHelper.cs`（`Task.FromResult(true)` / `Task.CompletedTask`）
- **根因分类**: 类型映射缺失/错误（TypeMappings.json）
- **涉及组件**: `config/TypeMappings.json`、`InvocationExpressionTransformer.cs`
- **分析**: 非泛型 `Task` 映射到同步 compat `Task` 类，但成员映射（isDone/join/completedFuture/supplyAsync）全是 `CompletableFuture` 语义，类型与方法不一致。
- **修复**: 类型映射 `System.Threading.Tasks.Task` → `CompletableFuture`；`Task.FromResult` 调用点直接生成 `CompletableFuture.completedFuture(...)`。
- **状态**: ✅ Fixed

---

## Iteration 8 — IEnumerable 派生类 GetEnumerator 覆盖返回类型不匹配
- **Java 文件**: `XmlTextReaderImpl.java`（NoNamespaceManager.iterator() 无法实现基类契约）
- **根因分类**: Transformer（MethodTransformer.GetReturnType 覆盖基类升级后的签名）
- **涉及组件**: `MethodTransformer.cs`
- **分析**: 基类因实现 IEnumerable 被升级为 `CSharpGenericEnumerator<Object> iterator()`，派生覆盖仍按声明类型（`IEnumerator`→`CSharpEnumerator`）发射 → 无法覆盖。
- **修复**: override 的 `GetEnumerator` 且基类实现 IEnumerable 时，返回类型取基类元素类型（`CSharpGenericEnumerator<T>`）。
- **状态**: ✅ Fixed

---

## Iteration 9 — 非泛型枚举器类不可赋值给泛型位置 / from(Iterator) 参数类型不符
- **Java 文件**: `XmlNode.java` / `XmlChildNodes.java` / `XmlNamedNodeMap.java`
- **根因分类**: Transformer + compat 库
- **涉及组件**: `ClassTransformer.cs`、`CSharpGenericEnumerator.java`
- **分析**: ① 实现非泛型 `IEnumerator`（→CSharpEnumerator）的类被升级后的 iterator() 返回（CSharpGenericEnumerator&lt;Object&gt;）引用时不可赋值；② `CSharpGenericEnumerator.from(...)` 的参数是 CSharpEnumerator 而非 Iterator。
- **修复**: ① 非泛型枚举器类同时实现 `CSharpGenericEnumerator<Object>`；② compat 增加 `fromEnumerator(CSharpEnumerator)` 适配器，升级路径统一使用之。
- **状态**: ✅ Fixed

---

## Iteration 10 — MathHelper 缺少 byte/short 解析与 NumberFormatInfo 重载
- **Java 文件**: `XmlConvert.java`（parseByte/parseShort 找不到方法、NumberFormatInfo→CultureInfo 转换失败）
- **根因分类**: 类型映射缺失 + compat 库
- **涉及组件**: `MathHelper.java`、`InvocationExpressionTransformer.cs`
- **分析**: `byte.Parse/sbyte.Parse/short.Parse` 落入 `Byte.parseByte`（java.lang.Byte 无 provider 重载）；MathHelper 的 tryParseInt/Long/UShort/UInt/ULong 缺 NumberFormatInfo 重载。
- **修复**: MathHelper 补齐 parseByte/parseShort 全套重载与 NumberFormatInfo tryParse 重载；`MapPrimitiveParseHelper` 增加 byte/sbyte/short（单参数形式保留 `Byte.parseByte`）。
- **状态**: ✅ Fixed

---

## Iteration 11 — AssemblyCompat 资源流返回类型不符
- **Java 文件**: `XmlPreloadedResolver.java:304`（InputStream 无法转换为 StreamWrapper）
- **根因分类**: compat 库
- **涉及组件**: `AssemblyCompat.java`
- **分析**: `GetManifestResourceStream(String)` 返回 `java.io.InputStream`，C# 侧 `Stream`（→StreamWrapper）语义不符。
- **修复**: 返回 `StreamWrapper.of(...)`。
- **状态**: ✅ Fixed

---

## Iteration 12 — this(...) 链内联按参数个数误匹配重载（XsdDateTime 构造器体错乱）
- **Java 文件**: `XsdDateTime.java:172`（DateTimeOffset 构造器拿到字符串构造器体）
- **根因分类**: Transformer（StructTransformer this(...) 链内联目标匹配）
- **涉及组件**: `StructTransformer.cs`
- **分析**: `XsdDateTime(DateTimeOffset) : this(dateTimeOffset, DateTime)` 与 `XsdDateTime(string, XsdDateTimeFlags)` 同为 2 参，按参数个数 FirstOrDefault 错选。
- **修复**: 内联目标优先按链实参标识符与参数名对齐者匹配（参数转发模式 `this(dateTimeOffset, kinds)` 2:2 对齐）。
- **状态**: ✅ Fixed

---

## Iteration 13 — 构造器 Java 擦除冲突致 compose 构造器被丢弃（DateTime 10 个失败）
- **Java 文件**: `XsdDateTime.java`（with_extra 误绑定公共 `(DateTime, flags)` 构造器，kind 位丢失）
- **根因分类**: Transformer（readonly pass 生成 compose 构造器时未检测 Java 擦除冲突）
- **涉及组件**: `ReadOnlyStructRewriter.cs`（GenerateComposeConstructor）
- **分析**: 私有 compose 构造器 `(DateTime, uint)` 与公共 `(DateTime, XsdDateTimeFlags)` 在 Java 中擦除同为 `(CSharpDateTime, int)`，转换器静默丢弃私有构造器，With 方法调用点错绑公共构造器（重新计算 _extra）。
- **修复**: 用语义模型计算 Java 擦除签名（枚举→int、uint/ushort/byte/sbyte→int、DateTime→CSharpDateTime 等），擦除冲突时加 `__cs2jTag` 参数消歧（C# 侧既有机制自动传入 0）。
- **状态**: ✅ Fixed

---

## Iteration 14 — compat 异常类型不符（ArgumentOutOfRangeException）
- **Java 文件**: `CSharpDateTimeOffset.java` / `StringHelper.java`
- **根因分类**: compat 库
- **涉及组件**: `CSharpDateTimeOffset.java`、`StringHelper.java`
- **分析**: 偏移超范围/字符串范围校验抛 `IllegalArgumentException`，C# 语义为 `ArgumentOutOfRangeException`。
- **修复**: 改为 compat `ArgumentOutOfRangeException`。
- **状态**: ✅ Fixed

---

## Iteration 15 — 测试注入的 xunit shim 无 Java 等价物
- **Java 文件**: `Cs2jEnUsCultureExecutor.java` 等（XunitTestFrameworkExecutor 基类不存在）
- **根因分类**: Transformer（转换文件选择）
- **涉及组件**: `Cs2jLibrary.cs`、`ProjectPasses.cs`
- **分析**: make-readonly 注入的 `Cs2jTestUiCultureShim.cs` 继承 xunit NuGet 框架类（不参与转换），产生不可解析基类。
- **修复**: 转换时跳过 `Cs2jTestUiCultureShim.cs`（文档组 + 类型组两级过滤）。
- **状态**: ✅ Fixed

---

## 汇总
- 迭代轮次：15 类根因（含一次目录切换验证 `d:\cs-java-813`）
- 修复涉及：`ReadOnlyStructMaker/`（Rewriter、Analyzer、MethodMigrator、CallSiteUpdater、AssignmentRewriter）、`StructTransformer.cs`、`MethodTransformer.cs`、`ClassTransformer.cs`、`InvocationExpressionTransformer.cs`、`Cs2jLibrary.cs`、`ProjectPasses.cs`、`config/TypeMappings.json`、compat 库（`CSharpGenericEnumerator`、`MathHelper`、`AssemblyCompat`、`CSharpDateTimeOffset`、`StringHelper`）
- 回归测试：新增 `NestedStruct_InsideMigratedStruct_IsMigratedToo`（Red→Green）；更新 5 个旧行为断言测试
- 最终验证：`dotnet test -c Release` 全量 3386 通过；`mvn clean install -e` 在 `d:\cs-java-812` 与 `d:\cs-java-813` 均 BUILD SUCCESS（EXIT=0）
