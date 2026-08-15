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
- **状态**: ✅ Fixed（含 compat `MemoryMarshal.asBytes(Span)`/`Span.asReadOnlySpan()` 与 `Task.Run<T>` 不再 Runnable 化；`system-private-xml` 主模块编译通过）

---

## Iteration 5 — 源定义 ICollection<T>.CopyTo 被泛型数组拷贝重写，丢失克隆语义（运行时测试失败）
- **Java 文件**: `d:\cs-java-81512\xmldocument-tests\src\test\java\dotnet\xml\Tests\CopyToTests.java:49`
- **行号**: 49（`Assert.notSame(attr1, destinationArray[0])`）
- **错误信息**: `Assert.notSame() failure: References are the same`（`CopyToCopiesClonedAttributes`）
- **代码片段**:
  ```java
  dotnet.xml.XmlAttribute[] destinationArray = new dotnet.xml.XmlAttribute[2];
  dotnet.xml.XmlAttributeCollection target = element.getAttributes();
  CSharpArray.copyTo(target.toArray(), 0, destinationArray, 0, target.size());
  Assert.notNull(destinationArray[0]);
  Assert.notSame(attr1, destinationArray[0]);   // FAILS: same reference
  ```
- **对应 C# 文件**: `D:\csharp-sdk\xml\tests\XmlDocument\XmlAttributeCollectionTests\CopyToTests.cs:27-45`；`D:\csharp-sdk\xml\src\System\Xml\Dom\XmlAttributeCollection.cs:290-296`（`CopyTo(XmlAttribute[], int)` 内调用 `CloneNode(true)`）
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（`IsFrameworkCollectionCopyTo`）
- **分析**: `IsFrameworkCollectionCopyTo` 通过“实现 `ICollection<T>`”匹配所有类型，把源定义的 `XmlAttributeCollection.CopyTo(XmlAttribute[], int)` 也重写成 `CSharpArray.copyTo(toArray(), ...)`；该方法在 C# 中有克隆属性语义，泛型数组拷贝只复制引用。修复为：方法所在类型若来自当前源码（`Locations.IsInSource`）则不重写，保留实例 `copyTo` 调用。
- **状态**: ✅ Fixed（提交 `fe7816c5`；xmldocument-tests 重新转换后编译/运行通过）

---

## Iteration 6 — Utf8JsonReaderTests 卡死：ReadOnlySpan.slice 对 byte[] 后端每次整段复制（O(n²)）
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Tests\Utf8JsonReaderTests.java`（`consumeLotsOfComments`）
- **行号**: 卡死栈 `ReadOnlySpan.toIntegerArray(ReadOnlySpan.java:308)` ← `slice` ← `Utf8JsonReader.consumeComment`
- **错误信息**: `mvn clean install -e` 停在 `Running dotnet.text.json.Tests.Utf8JsonReaderTests`，线程 CPU 213s 不返回
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Reader\Utf8JsonReader.cs`（`ConsumeComment` 反复 `_buffer.Slice(...)`）
- **根因分类**: compat（ReadOnlySpan）
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ReadOnlySpan.java`（`slice`）
- **分析**: `slice` 对 `byte[]` 后端每次物化 `Integer[]` 副本；`consumeLotsOfComments` 在超大缓冲上反复切片导致 O(n²)，表现为死循环。修复为返回 `ReadOnlySpan<>(bytes, offset+start, len)` 视图（O(1)）。
- **状态**: ✅ Fixed（Red→Green：`sliceOverByteArray_returnsViewNotCopy`）

---

## Iteration 7 — JsonTextReader 死循环：nextChar 永不消费字符
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Tests\Utf8JsonReaderTests.java`（`testTrySkipWithComments`）
- **行号**: 卡死栈 `JsonTextReader.read(JsonTextReader.java:51)` ← `JsonTestHelper.getTokenTypes`
- **错误信息**: `mvn test -pl system-text-json-tests -Dtest=Utf8JsonReaderTests` 卡在 `Running ...Utf8JsonReaderTests`
- **对应 C# 文件**: Newtonsoft `JsonTextReader` 兼容（compat 手写扫描器）
- **根因分类**: compat（JsonTextReader）
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/JsonTextReader.java`
- **分析**: `peekChar()` 把 `reader.peek()` 标成 pushedBack，`nextChar()` 只返回当前值、从不调用 `reader.read()`，所有空白/字符串扫描死循环。修复为 peek 不消费、next 走 `reader.read()`。
- **状态**: ✅ Fixed（Red→Green：`JsonTextReaderTest`）

---

## Iteration 8 — EnumHelper.getValues 返回底层数值而非枚举常量（ClassCastException 集群）
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Tests\Utf8JsonReaderTests.java`（`jsonWithTrailingCommasAndCommentsMultiSegment_Valid` 等）
- **行号**: `(Iterable<JsonCommentHandling>)(Iterable<?>)(EnumHelper.getValues(...))`
- **错误信息**: `ClassCastException: class java.lang.Integer cannot be cast to class dotnet.text.json.JsonCommentHandling`
- **对应 C# 文件**: `D:\csharp-sdk\json\tests\Utf8JsonReaderTests.MultiSegment.cs`（`foreach (JsonCommentHandling ... in Enum.GetValues(...))`）
- **根因分类**: compat（EnumHelper）
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/EnumHelper.java`
- **分析**: `Enum.GetValues` 返回枚举类型数组，之前为修 EnumConverter 把 `getValues` 改成了数值数组，导致 foreach 强转失败。恢复为返回枚举常量；EnumConverter 的数值转换在下一轮另行处理。
- **状态**: ✅ Fixed（Red→Green：`getValues_csharpArrayReturnsEnumConstants`）

---

## Iteration 9 — 参数化测试数据生成：CsvSource 特殊浮点值、MemberData 全限定名与可选参数填充
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Tests\Utf8JsonReaderTests.java`
- **行号**: `@CsvSource(value = {..., double.NegativeInfinity})`；`@MethodSource("jsonGuidTestData.InvalidGuidTests")`；`invalidJson(String,int,int,int)` 数据行只有 3 值
- **错误信息**: `ParameterResolution ... Failed to convert String "double.NegativeInfinity"`；`PreconditionViolation ... not a valid fully qualified method name`；`No ParameterResolver registered for parameter [int arg3]`
- **对应 C# 文件**: `D:\csharp-sdk\json\tests\Utf8JsonReaderTests.TryGet.cs`、`Utf8JsonReaderTests.cs`
- **根因分类**: Transformer（测试属性生成）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`
- **分析**: ① InlineData 回退把 `double.NegativeInfinity` 原文塞进 CSV；② MemberData 带 `MemberType` 时未生成 `包.类#方法`，且 `nameof(类.成员)` 取了全名；③ 可选参数 `maxDepth=64` 无法由 JUnit 填充，生成适配数据方法按默认值补行。
- **状态**: ✅ Fixed（Red→Green：`InlineData_FloatingSpecialValues...`、`MemberData_WithMemberType...`、`MemberData_WithOptionalParameter...`）

---

## Iteration 10 — Unsafe.addByteOffset 桩恒返回首字节（字符串扫描找不到结束引号）
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\JsonReaderHelper.java`（`indexOfOrLessThan`）
- **行号**: `lookUp = Unsafe.addByteOffset(searchSpace, index)` 恒等于首字节
- **错误信息**: `JsonReaderException: Expected end of string, but instead reached end of data`（约 2700 个）
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Reader\JsonReaderHelper.cs`（`IndexOfQuoteOrAnyControlOrBackSlash`）
- **根因分类**: compat + Transformer（ref 降级丢失 span）
- **涉及组件**: `java/csharptojava-compat/.../Unsafe.java`、`IntHolder.java`；`src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs`
- **分析**: `ref MemoryMarshal.GetReference(span)` 被降级为 `new IntHolder(首字节)`，`addByteOffset` 无法按偏移读字节。新增 `Unsafe.createByteSpanRef(span)`（携带 span 的 IntHolder 子类），`addByteOffset` 识别后按偏移 `span.get(offset)`；转换器对 byte 型 ref 实参生成 `createByteSpanRef`。
- **状态**: ✅ Fixed（Red→Green：`UnsafeByteSpanRefTest`、`RefGetReferenceByteArg_UsesSpanAwareHolder`）

---

## Iteration 11 — SequencePosition 缺少值相等（默认位置断言全挂）
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Tests\Utf8JsonReaderTests.java`（`defaultUtf8JsonReader` 等）
- **行号**: `Assert.equal(new SequencePosition(), json.getPosition())`
- **错误信息**: `Assert.equal() failure: Values differ`
- **对应 C# 文件**: `System.Buffers.SequencePosition`（值类型 struct）
- **根因分类**: compat（SequencePosition）
- **涉及组件**: `java/csharptojava-compat/.../SequencePosition.java`
- **分析**: Java 端无 `equals`，默认/同 segment+index 的实例按引用比较失败。补值相等与 hashCode。
- **状态**: ✅ Fixed（Red→Green：`SequencePositionTest`）

---

## Iteration 12 — ReadOnlySequence 多段序列：空末段长度算 0、位置被当段索引而非字节偏移
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Tests\Utf8JsonReaderTests.java`（大量 MultiSegment 用例）
- **行号**: 构造 `ReadOnlySequence(segment, 0, segment, endIndex)` 后 `getLength()==0`；`ValueSequence` 为空
- **错误信息**: `JsonReaderException: The input does not contain any JSON tokens`（多段输入被当空）；`Escaped ValueSequence to not be empty when HasValueSequence is true`
- **对应 C# 文件**: `D:\csharp-sdk\json\tests\JsonTestHelper.cs`、`System.Buffers.ReadOnlySequence`
- **根因分类**: compat（ReadOnlySequence）
- **涉及组件**: `java/csharptojava-compat/.../ReadOnlySequence.java`
- **分析**: ① 长度公式 `total - (total - endIndex)` 未处理“末段为空 + endIndex=0”，整段序列长度变 0；② `SequencePosition.getInteger()` 被 reader 当字节偏移使用（`+ consumed`/`+ literal长度`），compat 却当段索引。改为按 .NET 语义：位置=字节偏移，`tryGet` 定位所在段并推进到下一段起点，`getIsSingleSegment()` 对多段链返回 false。
- **状态**: ✅ Fixed（Red→Green：`ReadOnlySequenceChainTest` 5 例）

---

## Iteration 13 — Span/ReadOnlySpan 的 ==/!= 被生成成 Java 引用相等
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Tests\Utf8JsonReaderTests.java`（`checkOnlyOneOfValueSpanOrSequenceIsSet`）
- **行号**: `Assert.true_(json.getValueSpan() == new ReadOnlySpan<Integer>())`
- **错误信息**: `Assert.true() failure: Expected condition to be true`（两个空 span 实例引用不同）
- **对应 C# 文件**: `System.Span<T>`/`ReadOnlySpan<T>` 的 `op_Equality`（值比较）
- **根因分类**: Transformer（二元运算符）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`
- **分析**: C# `span == default`/`span1 == span2` 是长度+内容比较；Java 端类对象 `==` 是引用比较。检测 `System.ReadOnlySpan<T>`/`System.Span<T>` 操作数后改为 `MemoryExtensions.sequenceEqual(...)`。
- **状态**: ✅ Fixed（Red→Green：`SpanEqualityOperator_UsesSequenceEqual`）

---

## Iteration 14 — 枚举 `_UNMAPPED` 哨兵泄漏 + 枚举反装箱 ClassCastException
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Tests\Utf8JsonReaderTests.java`（`jsonWithTrailingCommasAndCommentsMultiSegment_Valid`）等
- **行号**: `JsonReaderOptions.setCommentHandling(JsonReaderOptions.java:53)`
- **错误信息**: `ArgumentOutOfRangeException: The JsonCommentHandling enum must be set to one of the supported values`；`EnumConverter.convertToUInt64` 对枚举常量做 `(int)` 强转
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Reader\JsonReaderOptions.cs`、`...\Converters\Value\EnumConverter.cs`
- **根因分类**: compat + Transformer
- **涉及组件**: `EnumHelper.java`、`TypeOperationTransformer.cs`
- **分析**: 转换器合成 `_UNMAPPED` 常量使 `Enum.GetValues` 多出 1 项；C# `(int)boxedEnum` 被生成成 Java `(Integer)value`。修复：`getValues/getValuesArray/getNames/tryParse` 过滤 `_UNMAPPED`；新增 `EnumHelper.toInt32/toInt64`，`object→数值` 反装箱改走该 helper。
- **状态**: ✅ Fixed（提交 `42aa0766`；Agent A 并行完成）

---

## Iteration 15 — struct Clone() 内 `return this` 生成 `this.clone()` 自递归
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\JsonElement.java`
- **行号**: `JsonElement.clone(JsonElement.java:942)` 无限递归（StackOverflowError）
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Document\JsonElement.cs`（`Clone()`）
- **根因分类**: Transformer（struct 值复制）
- **涉及组件**: `StatementTransformer.ExpressionAndReturn.cs`
- **分析**: C# `Clone()` 已映射为 Java `clone()`，值复制规则又把 `return this` 追加成 `.clone()`。修复：无参实例 `Clone()` 内 `return this` 跳过克隆。
- **状态**: ✅ Fixed（提交 `644c8dc1`）

---

## Iteration 16 — 非注解属性类型 `getAnnotationsByType` ClassCastException
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\...\CustomConverterTests.java`
- **错误信息**: `ClassCastException: class [Ldotnet.text.json.Serialization.JsonConverterAttribute; cannot be cast to [Ljava.lang.annotation.Annotation;`
- **对应 C# 文件**: 反射属性读取路径（`JsonSerializerOptions.GetAttributeThatCanHaveMultiple`）
- **根因分类**: compat
- **涉及组件**: `FieldInfo.java`、`ConstructorInfo.java`、`TypeMemberInfo.java`、`TypeHelper.java`
- **分析**: 转换器生成的属性类是普通类而非 Java 注解，直接 `getAnnotationsByType` 崩溃；非注解类型返回空数组。
- **状态**: ✅ Fixed（提交 `838c58ca`）

---

## Iteration 17 — uint/ulong 比较双重包装 + ReadOnlyMemory<byte> 切片复制 + 原始/包装类型相等
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\JsonHelpers.java`、SpanTests 等
- **错误信息**: `isInRangeInclusive` 恒 false；流读取写副本丢失；`typeof(bool)` 与 `boolean.class` 引用不等
- **根因分类**: compat + Transformer
- **涉及组件**: `BinaryExpressionTransformer.cs`（ContainsUIntCast 祖先遍历）、`MemoryExtensions.asMemory(byte[])`（改视图）、`TypeHelper.isSameType/isAssignableFrom`
- **分析**: ① 比较调用 `CompareUInt32(...)` 结果被再次 `Integer.compareUnsigned` 包装；② `asMemory` 复制导致异步写入丢失；③ 原始/包装类型对需要 `isSameType` 互认（配合 Type 相等路由）。
- **状态**: ✅ Fixed（提交 `ae75331e`）

---

## Iteration 18 — InlineData 浮点 Min/Max + ConditionalTheory/ConditionalFact 被丢弃
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Tests\Utf8JsonReaderTests.java`、RegexMatchTests
- **错误信息**: `@CsvSource` 出现 `double.MinValue` 文本；RegexMatchTests 的 `match_DefaultTimeout_Throws` 等 `[ConditionalTheory]` 用例从未运行
- **根因分类**: Transformer（测试属性生成）
- **涉及组件**: `MethodTransformer.cs`
- **分析**: ① InlineData 特殊值分支未覆盖 `MinValue/MaxValue`；② 只识别 `Theory`/`Fact`，`ConditionalTheory`/`ConditionalFact` 丢失 `@ParameterizedTest`/`@Test` 注解，用例被静默丢弃。
- **状态**: ✅ Fixed（Agent C 提交 InlineData Min/Max；父代理补 ConditionalTheory/ConditionalFact，Red→Green：`ConditionalTheory_GeneratesParameterizedTest`）

---

## Iteration 19 — RegexMatchTests 卡死最终根因：RemoteExecutor 条件测试在进程内执行
- **Java 文件**: `d:\cs-java-81512\dotnet-text-regularexpressions-tests\src\test\java\dotnet\text\regularexpressions\Tests\RegexMatchTests.java`（`match_DefaultTimeout_Throws`）
- **行号**: `RegexInterpreter.go(RegexInterpreter.java:995)` 无限回溯；`Regex.java:745` 的 `s_defaultMatchTimeout` 静态初始化缓存
- **错误信息**: `mvn clean install -e` 停在 `Running ...RegexMatchTests`（约 3 分钟无返回）
- **对应 C# 文件**: `D:\csharp-sdk\regular-expressions\tests\Regex.Match.Tests.cs`（`[ConditionalTheory(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]`）
- **根因分类**: Transformer（条件测试属性）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`
- **分析**: xUnit 在 `RemoteExecutor.IsSupported == false` 时跳过该测试；compat 的 RemoteExecutor 报告 false，但转换器把 `ConditionalTheory` 一律启用，导致 `AppDomain.SetData` 设置的默认超时对已静态初始化的 `Regex.s_defaultMatchTimeout` 无效，病态表达式无限回溯。修复：`ConditionalTheory/ConditionalFact` 条件引用 `RemoteExecutor.IsSupported` 时生成 `@Disabled`（与 .NET 行为一致）。
- **状态**: ✅ Fixed（Red→Green：`ConditionalTheory_UnsupportedRemoteExecutor_IsDisabled`；regex 模块 7106 测试 BUILD SUCCESS，RegexMatchTests 319 测试 10.34s 完成不再卡死）

---

## Iteration 14 — JsonElement.Clone 被生成成 `return this.clone()`，clone() 无限自递归
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\JsonElement.java:942`
- **行号**: 942（`clone()` 内 `if (!_parent.getIsDisposable()) { return this.clone(); }`）
- **错误信息**: `StackOverflowError`（大量 `at JsonElement.clone(JsonElement.java:942)` 重复栈帧）
- **代码片段**:
  ```java
  public JsonElement clone() {
      checkValidInstance();
      if (!_parent.getIsDisposable()) {
      return this.clone();   // 应为 return this;
      }
      return _parent.cloneElement(_idx);
  }
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Document\JsonElement.cs:1433-1444`（`Clone()` 中 `return this;`）
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs`
- **分析**: struct 值复制规则对所有 `return this;` 追加 `.clone()`，但 C# `Clone()` 已被映射为 Java `clone()`，于是 `return this.clone()` 变成 clone() 调用自身。修复为：当前方法是无参实例 `Clone()` 且返回 `this` 时不再追加 `.clone()`（普通方法的 `return this` 仍保留 `.clone()` 值复制语义）。
- **状态**: ✅ Fixed（Red→Green：`StructCloneReturnThisTests.CloneMethod_ReturnThis_StaysThis_NotRecursive` / `NonCloneMethod_ReturnThis_StillClones`）

---

## Iteration 15 — 枚举反装箱与 GetValues：`_UNMAPPED` 哨兵泄漏 + `(int)boxedEnum` 生成 Java 强转
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\Serialization\Converters\EnumConverter.java:255`
- **行号**: 255（`convertToUInt64` 内 `(Integer)(value)` / `(byte)(value)` / `(Number)(value)` 等反装箱）
- **错误信息**: `ClassCastException: class ...SampleEnum cannot be cast to class java.lang.Integer`（约 119 处 `ExceptionInInitializerError`/`TargetInvocationException` 链，并连带 `ObjectDefaultConverter not compatible with boolean` 等大量序列化错误）
- **代码片段**:
  ```java
  long result = (Objects.equals(s_enumTypeCode, TypeCode.Int32)
      ? (long)((int)(value))          // value 是枚举常量时 ClassCastException
      : ...);
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Serialization\Converters\Value\EnumConverter.cs:242-260`（`ConvertToUInt64(object value)` 中 `(ulong)(int)value` 等反装箱）
- **根因分类**: compat + Transformer
- **涉及组件**: `java/csharptojava-compat/.../EnumHelper.java`；`src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`
- **分析**: ① C# `(int)boxedEnum` 反装箱被生成成 Java `(Integer)value` 强转，而 Java 枚举常量不是 `Number`；② 转换器为未命名数值生成的 `_UNMAPPED` 哨兵被 `Enum.GetValues/GetNames` 泄漏出去。修复：`EnumHelper.getValues/getValuesArray/getNames` 过滤 `_UNMAPPED`；新增 `EnumHelper.toInt32/toInt64`（Number 直接取值，枚举经反射调用生成的 `getValue()`）；转换器对 `object → int/long/sbyte/short/byte/ushort/uint` 反装箱改为 `EnumHelper.toInt32/toInt64` 调用（byte/ushort/uint 保留原有掩码语义）。
- **状态**: ✅ Fixed（Red→Green：`EnumHelperTest.toInt32_toInt64_handlesEnumConstantsAndBoxedNumbers`、`EnumCastFromObjectTests` 2 例；314 个 enum/cast 相关转换器测试无回归）

---

## Iteration 14 — ObjectDefaultConverter 对 boolean/int 等基本类型报 not compatible（约 820 个错误）
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\JsonClassInfo.java:487`（`addProperty` → `getConverter` 失败）
- **行号**: `JsonSerializerOptions.java:354`（`throwInvalidOperationException_SerializationConverterNotCompatible`）
- **错误信息**: `The converter 'class ...ObjectDefaultConverter' is not compatible with the type 'boolean'/'int'`
- **代码片段**:
  ```java
  Class currentMemberType = propertyInfo.getPropertyType(); // boolean.class / int.class
  ... getConverter(memberType, ...) // s_defaultSimpleConverters 键是 Boolean.class / Integer.class
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Serialization\JsonClassInfo.cs`（`AddProperty`）、`JsonSerializerOptions.cs`
- **根因分类**: compat（反射成员类型未装箱）
- **涉及组件**: `java/csharptojava-compat/.../PropertyInfo.java`（`getPropertyType`）、`FieldInfo.java`（`getFieldType`）
- **分析**: Java 反射对基本类型属性返回 `boolean.class`/`int.class`，而转换器把 C# `typeof(bool)`/`typeof(int)` 映射为 `Boolean.class`/`Integer.class`；`s_defaultSimpleConverters` 用包装类做键，基本类型查不到后落入 `ObjectConverterFactory`，其产物 `ObjectDefaultConverter` 与基本类型不兼容而抛异常。修复为 `PropertyInfo.getPropertyType()`/`FieldInfo.getFieldType()` 经 `TypeHelper.toWrapperType` 装箱。
- **状态**: ✅ Fixed（Red→Green：`ReflectionMemberTypeTest`；生成项目 `ObjectDefaultConverter ... not compatible` 归零）

---

## Iteration 15 — uint/ulong 比较被双重包装，isInRangeInclusive 恒 false（NumberHandling 64 挂 + 大量 reader 错误）
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\JsonHelpers.java:53`
- **行号**: 53（`isInRangeInclusive(int,int,int)`）
- **错误信息**: `NumberHandlingTests` 静态初始化 `setNumberHandling` → `ArgumentOutOfRangeException`（64 个测试全挂）；后续大量 `JsonReaderException`
- **代码片段**（修复前）:
  ```java
  return Integer.compareUnsigned(UnsignedMath.compareUInt32(X, Y), 0) <= 0;
  ```
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\JsonHelpers.cs:53`（`(uint)(value - lowerBound) <= (uint)(upperBound - lowerBound)`）
- **根因分类**: Transformer（`ContainsUIntCast` 排除逻辑失效）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`（`ContainsUIntCast`）
- **分析**: 无符号预处理器把比较重写为 `UnsignedMath.CompareUInt32(..., (uint)…) <= 0`；`ContainsUIntCast` 的 `TakeWhile(ancestor => ancestor != node)` 把“包含该 (uint) 转换的调用表达式”本身排除在祖先遍历之外，导致回退分支再次把 `compareUInt32(...)` 包进 `Integer.compareUnsigned(..., 0)`，把 -1/0/1 结果当无符号值，范围内判断恒 false。修复为祖先遍历包含操作数节点（停在比较节点），调用表达式内的 `(uint)` 转换不再触发重包。
- **状态**: ✅ Fixed（Red→Green：`UnsignedTypeRewriterUnitTests.IsInRangeInclusiveUIntOverload_CompareCall_NotDoubleWrapped`；重新转换后 `JsonHelpers.java` 为单层 `UnsignedMath.compareUInt32(...) <= 0`，NumberHandlingTests 类初始化恢复，Utf8JsonReaderTests 错误从 1633 降到 440）

---

## Iteration 16 — ReadOnlyMemory<byte> 切片是复制而非视图，异步流读取写入副本后丢失（SpanTests.readFromStream 等）
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\dotnet\text\json\Serialization\Tests\SpanTests.java:67`（`readFromStream`）
- **行号**: 67（`deserializeAsync_0tp(stream, ...)`）
- **错误信息**: `JsonReaderException: '0x 0' is an invalid start of a value`（buffer 全 0）；另有 `toByteArray() requires a byte-backed memory`
- **对应 C# 文件**: `System.Text.Json.JsonSerializer.ReadFromStreamAsync`（`stream.ReadAsync(buffer.AsMemory(bytesInBuffer), ...)`）
- **根因分类**: compat（ReadOnlyMemory/MemoryExtensions）
- **涉及组件**: `java/csharptojava-compat/.../ReadOnlyMemory.java`、`MemoryExtensions.java`、`ArrayBufferWriter.java`
- **分析**: `MemoryExtensions.asMemory(byte[], start, length)` 和 `ReadOnlyMemory(byte[], start, length)` 把字节切片复制成新 `Integer[]`，`StreamWrapper.readAsync` 经 `getSpan()` 写入的是副本，主 buffer 永远是 0。改为 byte[] 视图（与 ReadOnlySpan 一致），并让 `getSpan()`/`toArray()`/`toByteArray()` 支持 byte[] 与 Object[]（ArrayBufferWriter 因泛型擦除用 Object[] 缓冲）后端。
- **状态**: ✅ Fixed（Red→Green：`ReadOnlyMemoryByteCtorTest` 视图/`toByteArray` 用例；SpanTests 的 `'0x 0'` 与 `toByteArray` 错误消失）

---

## Iteration 16 — 非注解类型调用 getAnnotationsByType 抛 ClassCastException（JsonConverterAttribute 集群）
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\JsonSerializerOptions.java:401`（`getAttributeThatCanHaveMultiple` → `FieldInfo.getCustomAttributes`）
- **行号**: `FieldInfo.java:71`（`field.getAnnotationsByType((Class) attributeType)`）
- **错误信息**: `ClassCastException: class [Ldotnet.text.json.Serialization.JsonConverterAttribute; cannot be cast to class [Ljava.lang.annotation.Annotation;`（CustomConverterTests 约 16 处）
- **代码片段**:
  ```java
  @Override
  public Object[] getCustomAttributes(Class<?> attributeType, boolean inherit) {
      return field.getAnnotationsByType((Class) attributeType); // 非注解类 → ClassCastException
  }
  ```
- **对应 C# 文件**: `System.Reflection.FieldInfo.GetCustomAttributes(Type, bool)` 语义；转换器生成的 `JsonConverterAttribute` 是普通类而非 Java 注解
- **根因分类**: compat
- **涉及组件**: `java/csharptojava-compat/.../FieldInfo.java`、`ConstructorInfo.java`、`TypeMemberInfo.java`、`TypeHelper.java`（`asCustomAttributeProvider`）
- **分析**: Java `getAnnotationsByType` 要求类型是注解；转换器生成的自定义属性类是普通类，调用即抛 `ClassCastException`。修复为 `attributeType == null || !attributeType.isAnnotation()` 时返回空数组（属性元数据未建模的类不再崩溃）。
- **状态**: ✅ Fixed（Red→Green：`MemberInfoCustomAttributesTest` 3 例；注：`[JsonConverter]` 成员属性的完整元数据支持仍未建模，相关用例会从 ClassCastException 转为断言失败，留待后续迭代）

---

## Agent4 前置根因 — `bool?` 条件访问比较被生成成 null 拆箱（NPE 群，约 220 处）
- **Java 文件**: `d:\cs-java-81512\system-text-json\src\main\java\dotnet\text\json\Serialization\IEnumerableConverterFactoryHelpers.java:219`
- **行号**: 219（`if ((stackType != null ? stackType.isAssignableFrom(type) : null) == true)`）
- **错误信息**: `NullPointerException: Cannot invoke "java.lang.Boolean.booleanValue()"`（`isNonGenericStackOrQueue` → `IEnumerableConverterFactory.createConverter` → `JsonClassInfo.addProperty` 链，ArrayTests/CollectionTests/CustomConverterTests/DictionaryTests/ImmutableCollectionsTests 等约 220 处）
- **对应 C# 文件**: `D:\csharp-sdk\json\src\System\Text\Json\Serialization\Converters\Collection\IEnumerableConverterFactoryHelpers.cs`（`stackType?.IsAssignableFrom(type) == true`）
- **根因分类**: Transformer（BinaryExpressionTransformer 对提升的 `bool? == bool` 直接发射 Java `==`/`!=`，对 `Boolean` 做拆箱比较）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs`
- **分析**: `Type? stackType` 的条件访问被转换为 `(stackType != null ? stackType.isAssignableFrom(type) : null)`，`== true` 再被发射成 Java `==`；当 `stackType` 为 null（该程序集不可解析）时 false 分支是 `null`，`== true` 对 null 拆箱抛 NPE，导致所有需要该转换器的类型初始化/序列化路径连锁失败。修复：在字符串转换与 IR 两条路径中识别 `bool? ==/!= bool 字面量` 与 `bool? ==/!= bool?`，统一发射 null 安全的 `Objects.equals(x, Boolean.TRUE/FALSE)` / `!Objects.equals(...)`（bool? == true 语义 = HasValue && Value）。
- **验证**: Red→Green `NullableBoolComparisonTests` 3 例（字段 bool?==true、bool?!=false、`stackType?.IsAssignableFrom(type)==true`）；全量 `dotnet test -c Release` 2811 通过，无回归。
- **状态**: ✅ Fixed

---

## Agent8 — InlineData/属性常量表达式被当成原始 C# 文本（约 400 个 JUnit 参数转换错误）
- **Java 文件**: `d:\cs-java-81512\system-text-json-tests\src\test\java\...\BitStackTests.java` 等
- **错误信息**: `Failed to convert String "int.MaxValue / 32 + 1" to type int`；`"Threshold - 3"`；`"System.Byte.MaxValue + 4"`；`"ClassWithExtensionPropertyNoGenericParameters.class"` to Class；`No built-in converter for source type SampleEnumByte ... target type long`
- **对应 C# 文件**: `D:\csharp-sdk\json\tests\...` 中 `[InlineData(...)]` 常量表达式参数
- **根因分类**: Transformer（`MethodTransformer.cs` InlineData→@CsvSource 回退到 `expr.ToString()`）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`、`src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`、`src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs`
- **分析**: JUnit `@CsvSource` 只能把字符串转成原始类型/String/enum；C# 属性参数是编译期常量表达式，被原样输出成 C# 文本后 JUnit 无法求值。修复：非常量字面量/`typeof(X)`/正则枚举转数值参数等 InlineData 参数改走 `BuildInlineDataDataMethod`（`@MethodSource`）用常规表达式转换；同时修复嵌套泛型 `typeof` 的 `BuildNestedJavaTypeReference`（点号在泛型实参内导致 Substring 越界）、负 uint/long 字面量缺 `L` 后缀、以及 data method 行按目标参数类型做窄化/枚举 `getValue()` 适配。
- **验证**: 新增 `InlineDataCsvMappingTests` 7 例（常量表达式/typeof/嵌套泛型 typeof/const 标识符/负 long/枚举转数值/长表达式转 int），全量 `dotnet test` 2805 通过；重新转换 1080/1080 成功；`mvn test` 编译通过（原 `Utf8JsonReaderTests.java:5724 整数太大` 消失），BitStackTests 参数错误清零，ValueTests/JsonReaderStateAndOptionsTests 的 InlineData 参数错误清零。
- **状态**: ✅ Fixed（InlineData 集群；MemberData 行内枚举→数值参数仍遗留，属后续迭代）

---

## Agent8b — MemberData 行内枚举/数值参数未按目标参数类型转换（EnumTests 参数转换错误）
- **Java 文件**: `d:\cs-java-81319-8\system-text-json-tests\src\test\java\...\EnumTests.java`（`getToString_ExceedMaxCapacity` 等 MemberData 数据方法）
- **错误信息**: `No built-in converter for source type SampleEnumByte/SampleEnumUInt16/... and target type long`；`No built-in converter for source type java.lang.Long and target type int`
- **对应 C# 文件**: `D:\csharp-sdk\json\tests\...` 中 `[MemberData(nameof(...))]` + `yield return new object[] { SomeEnum.A, ... }`
- **根因分类**: Transformer（MemberData 适配器缺失）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`（`BuildMemberDataAdapterMethod`/`ConvertMemberDataRowValue`）
- **分析**: 只有存在尾部默认参数时才生成 MemberData 适配器，行内枚举实例/盒装 Number 与目标数值参数类型不匹配，JUnit 无法转换。修复：任何含数值参数（int/long/short/byte/float/double）的测试方法都生成适配器，逐参数用 `EnumHelper.toInt32/toInt64` 或 Number 转换解包；无默认参数时 `firstDefaultIdx` 不再为 -1。
- **验证**: 新增 `MemberDataPropertySourceTests.MemberData_EnumValueToNumericParameter_UsesEnumHelperAdapter`；全量 `dotnet test` 2808 通过；重新转换 1080/1080 成功；`mvn test` 后 surefire 报告中 `No built-in converter` / `Failed to convert String` 全部归零（BitStackTests、ValueTests、JsonReaderStateAndOptionsTests、ExtensionDataTests、PropertyVisibilityTests、EnumTests 的参数转换错误消失）。
- **状态**: ✅ Fixed（参数转换集群；EnumTests 其余 52 fail/112 err 属枚举语义/序列化行为，留待枚举集群）
