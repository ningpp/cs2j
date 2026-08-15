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

---

# 第二轮（目标目录 `d:\cs-java-81319`，迭代直至 BUILD SUCCESS）

## Iteration 1 — MathHelper.tryParseUInt null provider 重载歧义（ambiguous reference）
- **Java 文件**: `dotnet-text-regularexpressions/src/main/java/dotnet/text/regularexpressions/Regex.java:299`
- **错误信息**: `对tryParseUInt的引用不明确` — `tryParseUInt(String,int,CultureInfo,IntHolder)` 与 `tryParseUInt(String,int,NumberFormatInfo,IntHolder)` 都匹配
- **代码片段**:
  ```java
  var _ret = (MathHelper.tryParseUInt(name, NumberStyles.None, null, _resultHolder1) && ...);
  ```
- **对应 C# 文件**: `D:\csharp-sdk\regular-expressions\src\System\Text\RegularExpressions\Regex.cs:364`（`uint.TryParse(name, NumberStyles.None, provider: null, out uint result)`）
- **根因分类**: Transformer（参数类型化强制转换缺失）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`、`ArgumentTransformer.cs`
- **分析**: 数值 Parse/TryParse 静态调用映射到 `MathHelper.tryParseXxx` 时，`IFormatProvider` 位置的 `null` 字面量按裸 `null` 发射；compat MathHelper 在该位置声明 `CultureInfo` 与 `NumberFormatInfo` 两个互不相关的重载，Java 无法消歧（C# 只有一个 IFormatProvider 参数，无兄弟重载，既有 null 强制转换机制不触发）。
- **修复**: ① Parse/TryParse 辅助路径将解析出的 `methodSymbol` 传入 `TransformArgumentList`，使参数类型化强制转换生效；② `CoerceArgumentType` 的 null 强制转换块新增 `System.IFormatProvider` 参数识别：发射 `(CultureInfo) null`（compat 默认 provider 类型）并补充 compat import。
- **回归测试**: `PrimitiveStaticParseMappingTests.UIntTryParse_NullProvider_EmitsDisambiguatedCultureInfoNull`、`DoubleParse_NullProvider_EmitsDisambiguatedCultureInfoNull`（Red→Green 验证）。
- **状态**: ✅ Fixed

---

## Iteration 2 — 公共序列化构造器 base(info, context) 无 super 目标（找不到合适的构造器）
- **Java 文件**: `dotnet-text-regularexpressions-tests/src/test/java/dotnet/text/regularexpressions/Tests/RegexConstructorTests.java:217`
- **错误信息**: `对于Regex(__suppress__,__suppress__), 找不到合适的构造器`（`super(info, context)` 无目标）
- **代码片段**:
  ```java
  public SerializableDerivedRegex(__suppress__ info, __suppress__ context) {
      super(info, context);
  }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\regular-expressions\tests\Regex.Ctor.Tests.cs:189`（`public SerializableDerivedRegex(SerializationInfo info, StreamingContext context) : base(info, context)`）
- **根因分类**: Transformer（序列化构造器检测遗漏 public）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs`
- **分析**: 基类序列化构造器（protected/private `(SerializationInfo, StreamingContext)`）被转换器整体省略（Java 无对应 ctor），但 `IsDotNetSerializationConstructor` 显式排除 `public` 构造器，导致派生类的 public 序列化构造器未被识别、仍发射 `super(info, context)` 转发到已省略的基类 ctor。
- **修复**: 抽出 `IsSerializationShapedConstructor`（不限可见性，含语法名回退）；非 public 序列化构造器保持省略；public 序列化构造器正常发射但丢弃 `base(...)` 转发，并补充 `throw new PlatformNotSupportedException();` 体（与 C# 基类行为一致，测试断言可运行时通过）。
- **回归测试**: `SerializationConstructorForwardingTests.DerivedPublicSerializationCtor_DoesNotForwardToMissingBaseCtor`（Red→Green 验证）。
- **状态**: ✅ Fixed

---

## 汇总（第二轮）
- 迭代轮次：2 类根因
- 修复涉及：`InvocationExpressionTransformer.cs`、`ArgumentTransformer.cs`、`ConstructorTransformer.cs`
- 回归测试：新增 3 个（Red→Green）；全量 `dotnet test -c Release` 3391 通过（CSharpToJava 2693 + CSharpToTypeScript 691 + ProjectTests 7）
- 最终验证：`mvn clean install -e` 在 `d:\cs-java-81319` **BUILD SUCCESS（EXIT=0）**，日志 `d:\cs-java-81319\mvn-build.log`

---

# 第三轮（运行期发现：结构体数组扩容未填充元素）

## Iteration 1 — NPE：_attrStack[top] 为 null（XmlWellFormedWriter.addAttribute 扩容后读新槽）
- **Java 文件**: `d:\cs-java-81319\system-private-xml\src\main\java\dotnet\xml\XmlWellFormedWriter.java:1748`；由 `d:\agl-java-81319` 的 WriteToSvgSample 运行触发（`: java.lang.NullPointerException: Cannot invoke "dotnet.xml.XmlWellFormedWriter$AttrName.set(String,String,String)" because "this._attrStack[top]" is null`）
- **行号**: 1748（`_attrStack[top] = _attrStack[top].set(prefix, localName, namespaceName);`）
- **错误信息**: NPE — 扩容后 `_attrStack[top]` 为 null
- **代码片段**:
  ```java
  AttrName[] newStack = new AttrName[top * 2];
  System.arraycopy(_attrStack, 0, newStack, 0, top);
  _attrStack = newStack;
  _attrStack[top] = _attrStack[top].set(prefix, localName, namespaceName);  // newStack[top] 是 null
  ```
- **对应 C# 文件**: `D:\csharp-sdk\xml\src\System\Xml\Core\XmlWellFormedWriter.cs:2206-2212`（`AttrName[] newStack = new AttrName[top * 2]; ... _attrStack[top].Set(...)`）
- **根因分类**: Transformer（局部变量声明路径缺失结构体数组零值填充）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs`（`TransformLocalDeclaration`）
- **分析**: C# `new UserStruct[n]` 零初始化每个元素；Java 中结构体转成的类数组初始全 null。赋值路径（`TryGetStructArrayFillStatement` → `Arrays.setAll`，见 `AssignmentTransformer.cs:1595`）已处理 `x = new T[n]`，但局部声明 `T[] x = new T[n]` 未填充；`addAttribute` 扩容后立刻读取新槽做结构体写回（`arr[i] = arr[i].set(...)`）→ NPE。首次 8 个槽由构造器填充所以 SDK 单测未暴露，超过 8 个属性/命名空间才触发（SvgGraphWriter 写节点属性时命中）。
- **修复**: 在 `TransformLocalDeclaration` 增加与枚举数组填充（`Arrays.fill`）平行的结构体数组填充：单变量、无初始化器的 `new T[size]` 且元素为用户自定义结构体时，声明后追加 `Arrays.setAll(var, _i -> new T());`（复用 `StructCloneHelper.IsUserDefinedStruct` 判定，排除枚举/元组/基元类型）。
- **回归测试**: `StructTransformerTests.StructArray_LocalDeclaration_FillsElementsWithDefaultInstances`（Red→Green 验证）

---

# 第四轮（目标目录 `d:\cs-java-81512`，迭代直至 BUILD SUCCESS）

## Iteration 1 — GroupCollection 与 CSharpReadOnlyDict 方法名冲突（name clash）
- **Java 文件**: `d:\cs-java-81512\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\GroupCollection.java`
- **行号**: 62、174、195、198
- **错误信息**: `名称冲突: class GroupCollection 的两种方法具有相同的疑符, 但两者均不覆盖对方` — `CSharpReadOnlyDict.get(java.lang.String)` 与 `GroupCollection.get(java.lang.Object)`；`CSharpReadOnlyDict.containsKey(java.lang.String)` 与 `GroupCollection.containsKey(java.lang.Object)`
- **代码片段**:
  ```java
  @DebuggerDisplay("Count = {Count}") @DebuggerTypeProxy(CollectionDebuggerProxy.class) public class GroupCollection implements CSharpReadOnlyDict<String, Group> {
      private Match _match;
      private CSharpHashtable _captureMap;
      private Group[] _groups;

      public GroupCollection(Match match, CSharpHashtable caps) {
          _match = match;
          _captureMap = caps;
      }

      public void reset() { _groups = null; }
      public boolean getIsReadOnly() { return true; }
      public int getCount() { return _match._matchcount.length; }
      public Group get(int groupnum) { return getGroup(groupnum); }
      public Group get(String groupname) { return (_match._regex == null ? Group.s_emptyGroup : getGroup(_match._regex.groupNumberFromName(groupname))); }
      public CSharpGenericEnumerator<CSharpKeyValuePair<String, Group>> iterator() { ... }
      private Group getGroup(int groupnum) { ... }
      private Group getGroupImpl(int groupnum) { ... }
      public boolean getIsSynchronized() { return false; }
      public Object getSyncRoot() { return _match; }
      public void copyTo(CSharpArray array, int arrayIndex) { ... }
      public void copyTo(Group[] array, int arrayIndex) { ... }
      public int indexOf(Object item) { ... }
      public void insert(int index, Group item) { throw new UnsupportedOperationException(...); }
      public void removeAt(int index) { throw new UnsupportedOperationException(...); }
      public Group set(int index, Group value) { ... }
      public boolean add(Group item) { throw new UnsupportedOperationException(...); }
      public void clear() { throw new UnsupportedOperationException(...); }
      public boolean contains(Object item) { ... }
      public boolean remove(Object item__obj) { ... }
      public boolean getIsFixedSize() { return true; }
      public void remove$Class(Object value) { throw new UnsupportedOperationException(...); }
      public boolean tryGetValue(String key, ObjectHolder<Group> value) { ... }
      public boolean containsKey(String key) { return _match._regex.groupNumberFromName(key) >= 0; }
      public CSharpReadOnlyCollection<String> getKeys() { ... }
      public CSharpReadOnlyCollection<Group> getValues() { ... }
      public int size() { return getCount(); }
      public boolean isEmpty() { return getCount() == 0; }
      public boolean containsKey(Object key) { return containsKey((String) key); }
      public Group get(Object key) { return get((String) key); }
      public Group tryGetValue(String key) { ... }
      ...
  }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\regular-expressions\src\System\Text\RegularExpressions\GroupCollection.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs`（`ResolveReadOnlyDictListConflict` 第 3/4 步）
- **分析**: compat 接口 `CSharpReadOnlyDict<K,V>` 已从 `containsKey(Object)/get(Object)` 改为 `containsKey(K)/get(K)`，但转换器仍按旧契约为 String 键合成 `containsKey(Object)/get(Object)` 桥接；Java 中 String 键类型方法的擦除就是 `Object`，显式 Object 重载与接口生成桥接冲突。
- **状态**: ✅ Fixed（重新转换后 `dotnet-text-regularexpressions` 模块编译通过）

---

## Iteration 2 — byte[] 初始化器内 unsigned 转换返回 int（incompatible types: int → byte）
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\JsonConstants.java`、`JsonSerializer.java`、`Utf8JsonWriter.java`
- **行号**: `JsonConstants.java:108-117`、`JsonSerializer.java:42-44`、`Utf8JsonWriter.java:5351`
- **错误信息**: `不兼容的类型: 从int转换到byte可能会有损失`（`UnsignedMath.toByteInt32(...)` 出现在 `new byte[] { ... }` 中）
- **代码片段**:
  ```java
  public static ReadOnlySpan<Integer> getTrueValue() {
      return new ReadOnlySpan<Integer>(new byte[] {
          UnsignedMath.toByteInt32((int) ('t')),
          UnsignedMath.toByteInt32((int) ('r')),
          UnsignedMath.toByteInt32((int) ('u')),
          UnsignedMath.toByteInt32((int) ('e'))
      });
  }
  public static final byte[] s_idPropertyName = new byte[] {
      UnsignedMath.toByteInt32((int) ('$')),
      UnsignedMath.toByteInt32((int) ('i')),
      UnsignedMath.toByteInt32((int) ('d'))
  };
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\JsonConstants.cs:35-48`（`new byte[] { (byte)'t', ... }`）、`D:\csharp-sdk\json\src\System\Text\Json\Serialization\JsonSerializer.cs`（`s_idPropertyName` 等）、`D:\csharp-sdk\json\src\System\Text\Json\Writer\Utf8JsonWriter.cs`（`SingleLineCommentDelimiterUtf8`）
- **根因分类**: Transformer（unsigned 重写）
- **涉及组件**: `src/CSharpToJava.Core/UnsignedTypeRewriter/UnsignedTypeSyntaxRewriter.cs`（`VisitCastExpression` / `GetCastReplacement`）
- **分析**: unsigned 重写把 `(byte)'t'` 替换成返回 `System.Byte` 的 `UnsignedMath.ToByteInt32(...)`；C# `byte` 标量映射为 Java `int`，但 `byte[]` 仍映射为 Java `byte[]`，因此数组初始化器里的方法调用不是编译期常量且为 `int`，javac 报 narrowing 错误；重写时缺少“目标为 byte[] 元素”的 `(byte)` 收尾转换。
- **状态**: ✅ Fixed（重新转换后 `system-text-json` 主模块编译通过）

---

## Iteration 3 — 非泛型 IList 局部变量被降级为 var 且未套用适配器（Object → Capture/Match）
- **Java 文件**: `d:\cs-java-81512\dotnet-text-regularexpressions-tests\src\test\java\dotnet\text\regularexpressions\Tests\CaptureCollectionTests.java:253,255`、`MatchCollectionTests.java:242,244`
- **行号**: 253/255、242/244
- **错误信息**: `不兼容的类型: java.lang.Object无法转换为dotnet.text.regularexpressions.Capture/Match`（`list.add((Object) null)`、`list.insert(0, (Object) null)`）
- **代码片段**:
  ```java
  public void iList_IsReadOnly() {
      var list = createCollection();
      Assert.true_(list.getIsReadOnly());
      Assert.true_(list.getIsFixedSize());
      Assert.throws_(UnsupportedOperationException.class, () -> list.add((Object) null));
      Assert.throws_(UnsupportedOperationException.class, () -> list.clear());
      Assert.throws_(UnsupportedOperationException.class, () -> list.insert(0, (Object) null));
      ...
  }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\regular-expressions\tests\CaptureCollectionTests2.cs:154-163`、`D:\csharp-sdk\regular-expressions\tests\MatchCollectionTests2.cs:148-157`（`IList list = CreateCollection();`）
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.Declarations.cs`（`TransformLocalDeclaration` 的 var 降级 + `IsAssignableToCSharpGenericIList`）
- **分析**: 显式 `System.Collections.IList` 局部变量被无条件降级为 `var`，且 `IsAssignableToCSharpGenericIList` 误把“C# 类型实现非泛型 IList”当作可直接赋给 Java `CSharpGenericIList<Object>`；实际生成类只实现 `CSharpGenericIList<T>`（泛型不变），因此需要 `CSharpGenericIList.from(...)` 适配器并保留 `CSharpGenericIList<Object>` 声明，否则 `add(Object)` 调用落到类型化 `add(T)` 上。
- **状态**: ✅ Fixed（单元测试 Red→Green；待 Maven 推进到 regex-tests 模块复核）

---

## Iteration 4 — char[].AsSpan 再包装为 Span<> 时菱形推断失败（无法推断类型参数）
- **Java 文件**: `d:\cs-java-81512\system-private-xml\src\main\java\dotnet\xml\NameTable.java:169`
- **行号**: 169
- **错误信息**: `不兼容的类型: 无法推断io.github.ningpp.compat.Span<>的类型参数` — `不存在类型变量T的实例, 以使io.github.ningpp.compat.Span<T>与io.github.ningpp.compat.ReadOnlySpan一致`
- **代码片段**:
  ```java
  private static int computeHash32(char[] key, int start, int len) {
      ReadOnlySpan<Integer> bytes = new ReadOnlySpan<>(
          MemoryMarshal.asBytes(new Span<>(MemoryExtensions.asSpan(key, start, len))));
      return Marvin.computeHash32(bytes, Marvin.getDefaultSeed());
  }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\xml\src\System\Xml\NameTable.cs:242-245`（`MemoryMarshal.AsBytes(key.AsSpan(start, len))`）
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`（Span 适配，`new Span<>(MemoryExtensions.asSpan(...))`）
- **分析**: `MemoryExtensions.asSpan(char[], ...)` 返回 `ReadOnlySpan<Character>`，但转换器用菱形 `new Span<>(...)` 再包装；该菱形出现在接受 raw `ReadOnlySpan` 的 `MemoryMarshal.asBytes` 实参位置，javac 无法从 raw 参数类型推断 T，需显式 `new Span<Character>(...)`（byte 路径同理应为 `<Integer>`）。
- **状态**: ⏳ In Progress
