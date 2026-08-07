# C# → Java 转换错误分析报告

本文档记录 `csharp-RegularExpressions` 项目转换过程中的编译错误及修复过程。

---

## Iteration 1 — 三种编译错误（已修复 ✅）

### 1.1 `ref` 返回类型未正确转换

- **Java 文件**: `d:\cs-re-20260807\src\main\java\System\Text\ValueStringBuilder.java`
- **行号**: 97, 105
- **错误信息**: `需要<标识符>` (需要标识符)
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\Common\System\Text\ValueStringBuilder.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs` (GetReturnType 方法)
- **分析**: 转换器将 C# 的 `ref char` 返回类型直接映射为 Java 的 `ref char`，但 Java 没有 `ref` 返回类型语法。
- **修复**: 在 `GetReturnType` 方法中添加对 `RefTypeSyntax` 的处理，提取内部类型并去掉 `ref` 关键字。

**状态**: ✅ Fixed

---

### 1.2 十六进制字面量 `U` 后缀未去除

- **Java 文件**: `d:\cs-re-20260807\src\main\java\dotnet\system\HexConverter.java`
- **行号**: 89-90, 165-166
- **错误信息**: `需要')'`, `需要';'`, `不是语句`
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\Common\System\HexConverter.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs` (TransformNumericLiteral 方法)
- **分析**: 对十六进制/二进制字面量（以 `0x` 或 `0b` 开头）直接原样返回，没有去除 C# 的 `U` 后缀。
- **修复**: 在 `TransformNumericLiteral` 方法中添加 `StripHexSuffix` 辅助方法来去除 `U`/`L` 后缀。

**状态**: ✅ Fixed

---

### 1.3 Java 参数名 `_` 不允许使用

- **Java 文件**: `d:\cs-re-20260807\src\main\java\csharp\xunit\ConditionalFactAttribute.java` 等
- **行号**: 46, 46, 48
- **错误信息**: `此处不允许使用下划线`
- **对应 C# 文件**: C# 测试工具类中的 discard 参数
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/ConstructorTransformer.cs` 和 `IndexerTransformer.cs`
- **分析**: C# 使用 `_` 作为 discard 参数名，但 Java 9+ 开始 `_` 是保留关键字。构造函数和索引器转换器没有调用 `EscapeJavaKeyword` 方法。
- **修复**: 在 `ConstructorTransformer.cs` 和 `IndexerTransformer.cs` 中添加 `EscapeJavaKeyword` 调用。

**状态**: ✅ Fixed

---

## Iteration 2 — 表达式体 void 方法缺少分号

- **Java 文件**: 
  - `CaptureCollection.java:[200,24]`
  - `GroupCollection.java:[213,95]`
  - `MatchCollection.java:[207,24]`
- **行号**: 200, 213, 207
- **错误信息**: `需要';'`
- **对应 C# 文件**: 
  - `CaptureCollection.cs` - `void IEnumerator.Reset() => _index = -1;`
  - `GroupCollection.cs` - `void IEnumerator.Reset() => _index = -1;`
  - `MatchCollection.cs` - `void IEnumerator.Reset() => _index = -1;`
- **代码片段**:
  ```java
  public void reset() {
      _index = -1
      _iteratorHasNext = false;
  }
  ```
- **根因分类**: Transformer 逻辑缺陷
- **分析**: 表达式体 void 方法（如 `void Reset() => _index = -1;`）在转换时缺少分号。此外还出现了不存在的字段 `_iteratorHasNext`，疑似字段初始化代码被错误地插入到方法体中。

**状态**: 🔧 待修复

---

## Iteration 3 — make-readonly 预处理 CS0206（ref 字段被改写为属性）

- **阶段**: 转换预处理（readonly-struct-maker 后的 `dotnet build` 验证），尚未进入 Maven
- **错误文件**: `d:\cs-re-20260807\.cs2j-readonly-src\src\System\Text\RegularExpressions\RegexReplacement.cs`
- **行号**: 42
- **错误信息**: `error CS0206: 非引用返回属性或索引器不能用作 out 或 ref 值`
- **代码片段**:
  ```csharp
  FourStackStrings stackStrings = default;
  var strings = new ValueListBuilder<string>(MemoryMarshal.CreateSpan(ref stackStrings.Item1!, 4));
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexReplacement.cs`（`FourStackStrings` 嵌套结构，行 96-103）
- **根因分类**: Lowering pass 不足（ReadOnlyStructMaker 资格判定缺陷）
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`、`src/CSharpToJava.Core/ReadOnlyStructMaker/CallGraphBuilder.cs`
- **分析**: `FourStackStrings` 是 `[StructLayout(Sequential)]` 的裸内存块结构（公共字段经 `ref stackStrings.Item1` 交给 `MemoryMarshal.CreateSpan`）。分析器未检查外部 ref/out 字段访问，将其归入 L3/L4 路径把公共字段改写为 get-only 属性，导致调用点 `ref stackStrings.Item1` 非法（CS0206）。`CallGraphBuilder.HasExternalRefFieldAccess`（专为 CS0206 设计、已支持 `!` 解包）存在但从未被调用（死代码）。
- **修复**: 在 `StructAnalyzer.AnalyzeCore` 的无变异方法分支（L1–L4 资格判定）中接入 `HasExternalRefFieldAccess` 守卫：非私有字段被外部 ref/out 访问时返回 NotConvertible，保持原结构不变。新增红→绿测试 `PublicFields_ExternalRefAccess_NotConverted`，全量测试 2433 通过。commit `945942fb`。

**状态**: ✅ Fixed

---

## Iteration 4 — void 表达式体属性自减产生非法裸语句（不是语句）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\RegexParser.java`
- **行号**: 2698
- **错误信息**: `不是语句`（javac: not a statement）
- **代码片段**:
  ```java
  private void popKeepOptions() {
      var _prev1 = _optionsStack.getLength();
      _optionsStack.setLength(_prev1 - 1);
      _prev1;   // ← 非法裸语句
  }
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexParser.cs`（行 2305）
  ```csharp
  private void PopKeepOptions() => _optionsStack.Length--;
  ```
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`
- **分析**: `IsDiscardedValueContext` 只识别 `ExpressionStatementSyntax` 和 for 递增器，未覆盖表达式体成员（父节点为 `ArrowExpressionClauseSyntax`）。void 表达式体方法中的属性 `--` 被误判为“值消费上下文”，走 hoist 路径产生临时变量 `_prev1`，其结果又被 `MethodTransformer` 当作语句输出，形成非法裸语句。
- **修复**: 扩展 `IsDiscardedValueContext` 识别 void 方法/局部函数表达式体、set/init 访问器表达式体、void 委托 lambda；并把 postfix/prefix 中基于 `node.Parent is ExpressionStatementSyntax` 的守卫全部改用该谓词。新增红→绿测试 `VoidExpressionBodyPropertyIncrementTests`（2 个），全量测试 2435 通过。

**状态**: ✅ Fixed

---

## Iteration 5 — ReadOnlyMemory<T> 类型映射缺失（找不到符号）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\System\Text\SegmentStringBuilder.java`（及 `Capture.java` 等）
- **行号**: 40, 65, 78, 92 …
- **错误信息**: `找不到符号: 类 ReadOnlyMemory`
- **代码片段**:
  ```java
  private ReadOnlyMemory<Character>[] _array;   // 类不存在，无 import
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\SegmentStringBuilder.cs`（`ReadOnlyMemory<char>[]`）
- **根因分类**: 类型映射缺失（TypeMappings.json）+ compat 库缺类
- **涉及组件**: `config/TypeMappings.json`、`java/csharptojava-compat/src/main/java/io/github/ningpp/compat/`（新增 `ReadOnlyMemory.java`，扩展 `MemoryExtensions.java`）
- **分析**: 转换器无 `System.ReadOnlyMemory<T>`/`System.Memory<T>` 映射，compat 库也无对应类；生成代码直接输出未解析的 `ReadOnlyMemory<Character>` 导致 javac 找不到符号。成员调用（`MemoryExtensions.asMemory`、getter 命名）转换器已能正确产出，只缺类型本身。
- **修复**: 新增 compat `ReadOnlyMemory<T>`（String/数组双底层，getSpan/getLength/isEmpty/slice/toArray/toString）；`MemoryExtensions` 增加 `asMemory` 四个重载；TypeMappings.json 增加 `System.Memory\`1`/`System.ReadOnlyMemory\`1` → `io.github.ningpp.compat.ReadOnlyMemory` 映射；`mvn clean install` 重装 compat。新增红→绿测试 `ReadOnlyMemoryMappingTests`。

**状态**: ✅ Fixed

---

## Iteration 6 — IList<T> + IList 双接口显式实现擦除后重复定义

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\CaptureCollection.java`
- **行号**: 136, 137, 142
- **错误信息**: `已在类中定义了方法 contains(java.lang.Object) / indexOf(java.lang.Object) / remove(java.lang.Object)`
- **代码片段**:
  ```java
  public boolean contains(Object item) { ... }   // ← IList<Capture>.Contains 参数被擦除为 Object
  ...
  public boolean contains(Object value) { ... }  // ← IList.Contains 显式实现，签名冲突
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\CaptureCollection.cs`（同时实现 `IList<Capture>` 与 `IList`，两者都有显式 Contains/IndexOf/Remove）
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs`（`AddCollectionInterfaceBridgeMethods`）
- **分析**: 后置桥接 pass 为满足 Java Collection 契约，把 `contains(Capture)/indexOf(Capture)/remove(Capture)` 的参数原地擦除为 `Object`，但擦除后未检查类中是否已存在同擦除签名的方法（来自非泛型 IList 的显式实现），产生重复定义。且 Java 不允许仅返回类型不同的同签名重载（`boolean remove(Object)` vs `void remove(Object)`）。
- **修复**: 在 `AddCollectionInterfaceBridgeMethods` 中：① 每次参数擦除后立即移除同擦除签名重复；② pass 末尾统一扫描（`RemoveAllErasedSignatureDuplicates`）：同签名同返回类型保留先出者；返回类型混合且仅一个非 void 时保留非 void（Collection 契约优先）；多个非 void 冲突留给既有访问器改名 pass。新增红→绿测试 `GenericAndNonGenericIListCollisionTests`，全量测试 2437 通过。

**状态**: ✅ Fixed

---

## Iteration 7 — WeakReference<T> 泛型缺失（类型不带有参数）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\Regex.java`
- **行号**: 50（另 RegexReplacement.java:106）
- **错误信息**: `类型io.github.ningpp.compat.WeakReference不带有参数`
- **代码片段**:
  ```java
  public WeakReference<RegexReplacement> _replref;   // compat 类为非泛型
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\Regex.cs`（`WeakReference<RegexReplacement>`，`TryGetTarget`/`SetTarget`）
- **根因分类**: 类型映射缺失（`System.WeakReference\`1` 无映射）+ compat 类非泛型
- **涉及组件**: `config/TypeMappings.json`、`java/csharptojava-compat/.../WeakReference.java`
- **分析**: 转换器只有非泛型 `System.WeakReference` 映射；生成代码直接输出 `WeakReference<T>` 而 compat 类不接受类型参数。
- **修复**: compat `WeakReference` 改为泛型 `WeakReference<T>`（保留 getIsAlive/getTarget/setTarget，新增 `tryGetTarget(ObjectHolder<T>)`；raw 用法经擦除仍兼容）；TypeMappings.json 新增 `System.WeakReference\`1` 映射；重装 compat。新增红→绿测试 `WeakReferenceGenericMappingTests`。

**状态**: ✅ Fixed

---

## Iteration 8 — 枚举常量 value 与合成字段冲突

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\ExceptionArgument.java`
- **行号**: 45
- **错误信息**: `已在枚举 ExceptionArgument 中定义了变量 value`
- **代码片段**:
  ```java
  value(17),          // C# 枚举常量
  ...
  private final int value;   // 转换器合成字段，同名冲突
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\ThrowHelper.cs`（`enum ExceptionArgument { ..., value }`）
- **根因分类**: Transformer 逻辑缺陷（EnumTransformer 合成字段未防命名冲突）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/EnumTransformer.cs`
- **分析**: EnumTransformer 为所有枚举生成 `value`/`unmappedValue` 底层字段与 getValue/fromValue；Java 枚举常量与字段同命名空间，C# 枚举成员恰名为 `value` 时冲突。
- **修复**: 枚举成员名与合成字段名冲突时，合成字段改用 `$value`/`$unmappedValue`（`$` 在 Java 合法、C# 不可能出现），ctor/getValue/fromValue/fromValueUnchecked 方法体同步插值。新增红→绿测试 `EnumValueMemberCollisionTests`，全量测试通过。

**状态**: ✅ Fixed

---

## Iteration 9 — 双 IEnumerator<T> 实现的 getCurrent() 重复定义

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\GroupCollection.java`
- **行号**: 218, 225
- **错误信息**: `已在类 GroupCollection.Enumerator 中定义了方法 getCurrent()`
- **代码片段**:
  ```java
  public Group getCurrent() { ... }                              // IEnumerator<Group>.Current
  public CSharpKeyValuePair<String, Group> getCurrent() { ... }  // IEnumerator<KeyValuePair<...>>.Current
  public Object getCurrent() { return getCurrent(); }            // 非泛型桥接，自递归
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\GroupCollection.cs`（`Enumerator : IEnumerator<Group>, IEnumerator<KeyValuePair<string, Group>>`）
- **根因分类**: Transformer 逻辑缺陷（显式接口访问器冲突消解不完备）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs`（`ResolveExplicitInterfacePropertyConflicts`）
- **分析**: 既有冲突消解只处理“1 个显式访问器 vs 类属性访问器”；多个显式访问器同擦除签名（不同 T 的 IEnumerator<T>.Current）时直接跳过，Java 不允许同参数签名仅返回类型不同的重载。
- **修复**: 新增 `ResolveSameSignatureAccessorCollisions` 后置扫描：同签名组内存在带类型变体时删除返回 Object 的非泛型桥接（协变返回可满足契约）；其余重复保留先出者、后续者改名为 `name$Class[N]` 并重写类内调用点。新增红→绿测试 `DualGenericEnumeratorCurrentCollisionTests`，全量测试通过。

**状态**: ✅ Fixed

---

## Iteration 10 — #if DEBUG 调试专用代码被转换（System.Reflection 缺失）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\RegexAssemblyCompiler.java`
- **行号**: 27, 28, 36…
- **错误信息**: `程序包System.Reflection不存在`
- **代码片段**:
  ```java
  import System.Reflection.MethodAttributes;   // 不存在的 Java 包
  import System.Reflection.CallingConventions;
  import System.Reflection.TypeAttributes;
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexAssemblyCompiler.cs`（整个文件包在 `#if DEBUG` 内，依赖 System.Reflection.Emit）
- **根因分类**: 语义模型信息丢失（预处理符号策略）
- **涉及组件**: `src/CSharpToJava.Core/Pipeline/ProjectConversionPipeline.cs`
- **分析**: MSBuild 以 Debug 配置加载源工程，编译定义 DEBUG 符号，导致 `#if DEBUG` 调试专用代码（IL 生成器，Java 无对应域）进入转换。转换器其余部分均按 Release 语义处理（如剔除 Debug.Assert），预处理符号策略不一致。
- **修复**: `ConvertProjectAsync(CSharpCompilation…)` 入口新增 `StripDebugPreprocessorSymbol`：检测到 DEBUG 符号时以去除 DEBUG 的解析选项重新解析所有语法树（保留 TRACE 等其他符号），重建编译。`#if DEBUG` 区域不再参与转换；陈旧产物由输出清单机制自动清理。新增红→绿测试 `DebugConditionalExclusionTests`，全量测试 2441 通过。

**状态**: ✅ Fixed

---

## Iteration 11 — 表达式体 getter 前置语句泄漏污染后续成员（非法的表达式开始）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\MatchCollection.java`
- **行号**: 33
- **错误信息**: `非法的表达式开始`
- **代码片段**:
  ```java
  private Regex _regex = /* Object initializer — consider moving to constructor. */
  if (_groupcoll == null) _groupcoll = new GroupCollection(this, _caps);   // ← 来自 Match 类的泄漏语句
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\Match.cs`（行 376：`public override GroupCollection Groups => _groupcoll ??= new GroupCollection(this, _caps);`）
- **根因分类**: 语义模型信息丢失（ConversionContext 残留状态）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs`、`.../Expression/Transformers/AssignmentTransformer.cs`（`TransformCoalesceAssignment`）
- **分析**: `??=` 转换为前置语句 `if (x == null) x = ...` + 表达式结果；表达式体 getter 路径不消费前置语句，残留在共享 `ConversionContext` 中，跨成员甚至跨文件被下一个字段声明的 `DrainFieldPreStatements` 吸收，污染无关类的字段初始化器。
- **修复**: `PropertyTransformer` 在表达式体 getter 转换后检查 pending 前置/后置语句，有则转为块体（pre 语句 + return；后置语句用 `_ret` 暂存），保证语句留在 getter 内部。新增红→绿测试 `GetterPreStatementLeakTests`，全量测试 2442 通过。

**状态**: ✅ Fixed

---

## Iteration 12 — Reflection 标志枚举无类型映射（程序包 System.Reflection 不存在）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\RegexLWCGCompiler.java`
- **行号**: 24
- **错误信息**: `程序包System.Reflection不存在`
- **代码片段**:
  ```java
  import io.github.ningpp.compat.MemberInfo;
  import io.github.ningpp.compat.MethodInfo;
  import System.Reflection.MethodAttributes;      // ← 非法 import
  import System.Reflection.CallingConventions;    // ← 非法 import
  ```
- **对应 C# 文件**: `d:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexLWCGCompiler.cs`（及 RegexCompiler.cs）
- **根因分类**: 类型映射缺失
- **涉及组件**: `config/TypeMappings.json`、`java/csharptojava-compat/src/main/java/io/github/ningpp/compat/{MethodAttributes,CallingConventions,TypeAttributes,ModuleBuilder,TypeBuilder,MethodBuilder,DynamicMethod}.java`
- **分析**: `MethodAttributes`/`CallingConventions`/`TypeAttributes` 无映射，转换器原样保留 `System.Reflection.X` 引用。且这些是标志枚举，转换器把 const 标志组合降级为 int 位运算，故 Java enum 风格的 compat 类不可用，必须改为 int 常量类（仿 BindingFlags 模式）。
- **修复**: 重写三个 compat 枚举为 int 常量类；`ModuleBuilder.defineType`/`TypeBuilder.defineMethod`/`defineDefaultConstructor` 参数与 `MethodBuilder.getAttributes()` 返回类型改为 int，`DynamicMethod` 新增 int 版构造器重载；`TypeMappings.json` 新增三条映射。const 标志字段被常量折叠为字面量（如 `Attribs = 22`），非 const 用法映射为 compat 常量位运算。新增红→绿测试 `ReflectionFlagEnumMappingTests`，全量测试 2443 通过。

**状态**: ✅ Fixed

---

## Iteration 13 — compat Span 缺少 getLength()/tryCopyTo()，ArrayPool 未映射

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\System\Collections\Generic\ValueListBuilder.java`
- **行号**: 63
- **错误信息**: `找不到符号: 方法 getLength()，位置: 类型为 io.github.ningpp.compat.Span<T> 的变量 _span`（后续还有 ArrayPool 未映射、`T[] 无法转换为 Span<T>`、tryCopyTo 缺失）
- **代码片段**:
  ```java
  if (pos >= _span.getLength()) { grow(); }          // Span 无 getLength()
  T[] array = ArrayPool.getShared().rent(...);       // ArrayPool 未映射
  boolean success = _span.tryCopyTo(array);          // Span 无 tryCopyTo
  _span = _arrayFromPool = array;                    // T[] 隐式转 Span 未包装
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\Common\System\Collections\Generic\ValueListBuilder.cs`
- **根因分类**: 类型映射缺失
- **涉及组件**: `java/csharptojava-compat/.../Span.java`、`config/TypeMappings.json`（ArrayPool）、转换器数组→Span 隐式转换处理
- **分析**: compat `Span` 只有 `length()`，而 `ReadOnlySpan` 同时有 `length()`/`getLength()`，转换器将 `_span.Length` 转为 `getLength()`；`ArrayPool<T>` 无映射；`_span = array` 的 T[]→Span 隐式转换未包装为 `new Span<>(array)`。
- **修复**: 五处联合修复：① compat `Span` 新增 `getLength()`/`tryCopyTo(T[])`；② 新建 compat `ArrayPool<T>`（getShared/rent/returnValue）并在 TypeMappings.json 添加 `System.Buffers.ArrayPool`1` 映射；③ AssignmentTransformer 新增 T[]→Span/ReadOnlySpan 赋值包装 `new Span<>(array)`；④ `Array.Empty<T>()` 泛型元素改为 `(T[]) new Raw[0]` 避免非法泛型数组创建；⑤ `StringHelper.createString` 泛型化 `<TState>`。新增红→绿测试 `SpanArrayCompatClusterTests`（3 用例），全量测试 2446 通过。

**状态**: ✅ Fixed

---

## Iteration 14 — ArrayPool.getShared().rent() 链式泛型推断失败（Object[] 无法转换为 T[]）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\System\Collections\Generic\ValueListBuilder.java`
- **行号**: 80
- **错误信息**: `不兼容的类型: java.lang.Object[]无法转换为T[]`（同簇：SegmentStringBuilder Object[]→ReadOnlyMemory<Character>[]；ValueStringBuilder Object[]→char[]）
- **代码片段**:
  ```java
  T[] array = ArrayPool.getShared().rent(_span.getLength() * 2);   // 链式推断 T=Object
  _arrayToReturnToPool = ArrayPool.getShared().rent(initialCapacity); // char[] 目标
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\Common\System\Collections\Generic\ValueListBuilder.cs`、`...\System\Text\ValueStringBuilder.cs`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`、compat `ArrayPool.java`
- **分析**: Iter13 新建的 compat `ArrayPool.getShared()` 是泛型静态方法，`ArrayPool.getShared().rent(n)` 链式调用在 javac 中无法从赋值目标反推 T（推断为 Object）；且 `ArrayPool<char>` 在 Java 中无法表达（泛型不支持 char）。需发射类型见证 `ArrayPool.<T>getShared()`，char 路由到 char[] 专用池。
- **修复**: InvocationExpressionTransformer 拦截 `ArrayPool<T>.Shared.Rent/Return`：引用类型发射 `ArrayPool.<JavaT>getShared().rent/returnValue(...)`，char 发射 `ArrayPool.rentSharedCharArray/returnSharedCharArray(...)`；compat ArrayPool 新增 char[] 共享池。同步补齐 compat API：Span 新增 char[] 构造器/存储、`copyTo(Span)`/`copyTo(char[])`/`fill(T)`；MemoryMarshal 新增 `getReference(Span<T>)`。新增红→绿测试 `ArrayPoolTypeWitnessTests`（3 用例），全量测试 2449 通过。

**状态**: ✅ Fixed

---

## Iteration 15 — Span 缺 tryCopyTo(Span)；MemoryExtensions.asSpan(String) 返回 ReadOnlySpan<Integer> 与 Character 体系不兼容

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\System\Text\ValueStringBuilder.java`
- **行号**: 120
- **错误信息**: `不兼容的类型: Span<Character>无法转换为Character[]`（同簇 [149/178]: `Span<Character>无法转换为Span<Integer>`）
- **代码片段**:
  ```java
  if (_chars.slice(0, _pos).tryCopyTo(destination)) {   // Span 只有 tryCopyTo(T[])
  MemoryExtensions.asSpan(s).copyTo(_chars.slice(index)); // asSpan(String)→ReadOnlySpan<Integer>
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\System\Text\ValueStringBuilder.cs`（TryCopyTo(Span<char>)、s.AsSpan().CopyTo(...)）
- **根因分类**: 类型映射缺失（compat API 与转换器 char→Character 映射不一致）
- **涉及组件**: `java/csharptojava-compat/.../Span.java`、`.../MemoryExtensions.java`
- **分析**: 转换器将 C# `char`/`Span<char>` 统一映射为 `Character`/`Span<Character>`，但 compat `MemoryExtensions.asSpan(String/char[])` 早期按 byte→Integer 风格返回 `ReadOnlySpan<Integer>`；compat `Span` 也缺 `tryCopyTo(Span<T>)` 重载（C# TryCopyTo(Span<T>)）。
- **修复**: compat `MemoryExtensions.asSpan(String/char[])` 改为返回 `ReadOnlySpan<Character>`（byte[] 重载保持 Integer 不变）；compat `Span` 新增 `tryCopyTo(Span<T>)` 重载；同步修正既有 compat 测试 `MemoryMarshalTest`。新增红→绿 compat JUnit 测试 `SpanCharCompatTest`（红：编译失败复现项目错误 → 绿：mvn test 通过）。

**状态**: ✅ Fixed

---

## Iteration 16 — fixed(byte* p = span) 指针回传模式不可编译（MemorySegment.ofArray(ReadOnlySpan)、IntPtr、命名元组字段）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\system\HexConverter.java`
- **行号**: 166
- **错误信息**: `对于ofArray(ReadOnlySpan<Integer>), 找不到合适的方法`（同簇：`找不到符号 类 IntPtr`、`找不到符号 变量 Ptr/Length`）
- **代码片段**:
  ```java
  MemorySegment __base1 = MemorySegment.ofArray(bytes);          // span 不是数组
  Tuple.of((IntPtr)(bytesPtr), bytes.getLength(), casing)        // IntPtr 未降级
  var ros = new ReadOnlySpan<Integer>(args.Ptr, args.Length);    // 命名元组字段未降级
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\Common\System\HexConverter.cs`（ToString：fixed + string.Create + 元组携带指针 + lambda 内重建 ReadOnlySpan<byte>）
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `StatementTransformer.SwitchAndResource.cs`（fixed）、`TypeOperationTransformer.cs`（cast）、`IdentifierExpressionTransformer.cs`（元组）、`ObjectCreationTransformer.cs`（span 构造）、compat `Span/ReadOnlySpan`
- **分析**: 转换器 FFM 降级假设 fixed 源是数组/字符串；span 源直接传入 ofArray；(IntPtr) 指针 cast 未当 no-op；命名元组字段（Ptr/Length/casing）只支持 ItemN 形式；`new ReadOnlySpan<byte>((byte*)ptr, len)` 无重建路径。
- **修复**: ① fixed 源为 byte span 时追加 `.toByteArray()`；② (IntPtr)/(UIntPtr) 指针 cast 作 no-op；③ 命名元组字段降级为 vavr `_N()`（三处路径）；④ Span/ReadOnlySpan 指针+长度构造器发射 `fromByteSegment(segment, len)`；compat 新增 `toByteArray()`/`fromByteSegment()`。新增红→绿测试 `FixedPointerSpanRoundTripTests`，全量测试 2450 通过。

**状态**: ✅ Fixed

---

## Iteration 17 — out _ 弃元参数发射 new Object[1]；byte[] 返回 ReadOnlySpan<byte> 未包装

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\system\HexConverter.java`
- **行号**: 194
- **错误信息**: `不兼容的类型: java.lang.Object[]无法转换为IntHolder`（同簇 [249]: `byte[]无法转换为ReadOnlySpan<Integer>`）
- **代码片段**:
  ```java
  return tryDecodeFromUtf16(chars, bytes, new Object[1]);              // out _ 应发射类型化 holder
  public static ReadOnlySpan<Integer> getCharToHexLookup() { return new byte[] {...}; } // 隐式转换未包装
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\Common\System\HexConverter.cs`（`TryDecodeFromUtf16(.., out _)`、`CharToHexLookup => new byte[] {...}`）
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/ArgumentTransformer.cs`、`.../Utilities/ExpressionTransformerHelpers.cs`（AdaptExpressionToTargetTypeCore）
- **分析**: out 弃元分支忽略参数类型一律发射 `new Object[1]`；数组→Span 隐式转换只在简单赋值路径包装（Iter13），表达式体/返回值等 Adapt 路径未覆盖。
- **修复**: ① out 弃元按 parameterSymbol 类型发射类型化 holder（out int → `new io.github.ningpp.compat.IntHolder()`）；② AdaptExpressionToTargetTypeCore 新增 T[]→Span/ReadOnlySpan 包装（带防双重包装守卫，避免 Encoding.Preamble 的 asSpan 桥接被再包）。新增红→绿测试 `OutDiscardAndArraySpanAdaptationTests`（2 用例），全量测试 2452 通过。

**状态**: ✅ Fixed

---

## Iteration 18 — new ReadOnlySpan<>(byte[]) 菱形推断失败（缺 byte[] 构造器）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\system\HexConverter.java`
- **行号**: 249
- **错误信息**: `无法推断io.github.ningpp.compat.ReadOnlySpan<>的类型参数`
- **代码片段**:
  ```java
  public static ReadOnlySpan<Integer> getCharToHexLookup() {
      return new ReadOnlySpan<>(new byte[] { (byte)0xFF, ... });   // 无 byte[] 构造器可匹配
  }
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\Common\System\HexConverter.cs`（`CharToHexLookup => new byte[] {...}`）
- **根因分类**: 类型映射缺失（compat API 缺口）
- **涉及组件**: `java/csharptojava-compat/.../ReadOnlySpan.java`
- **分析**: Iter17 的 Adapt 包装发射 `new ReadOnlySpan<>(byteArr)`，但 compat ReadOnlySpan 只有 T[]/ReadOnlySpan/Span 构造器；byte[] 实参无可用构造器，菱形推断失败（Span 已有 byte[] 构造器，ReadOnlySpan 遗漏）。
- **修复**: compat ReadOnlySpan 新增 `ReadOnlySpan(byte[])` 与 `ReadOnlySpan(byte[], int, int)` 构造器（存储 byte[]，get() 按无符号 Integer 返回，与既有 byte[] 分支一致）。新增红→绿 compat JUnit 测试 `ReadOnlySpanByteCtorTest`（红：复现推断失败 → 绿：mvn test 通过）。

**状态**: ✅ Fixed

---

## Iteration 19 — IList&lt;T&gt;+IReadOnlyList&lt;T&gt;+IList 三重实现的擦除冲突簇

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\CaptureCollection.java`
- **行号**: 33
- **错误信息**: `不是抽象的, 并且未覆盖Collection中的抽象方法add(Object)`（同簇：get(int) 返回类型冲突、iterator() 返回 CSharpEnumerator 不兼容、同名疑符冲突）
- **代码片段**:
  ```java
  public class CaptureCollection implements CSharpGenericIList<Object>, CSharpReadOnlyList<Capture>, Iterable<Object> {
  public int add(Object value) { ... }        // 与泛型接口擦除后 add(Object) 冲突
  public Object get(int index) { return this.get(index); }  // 与 Capture get(int) 同签名不同返回
  public CSharpEnumerator iterator() { return new Enumerator(this); } // 类型不兼容
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\System\Text\RegularExpressions\CaptureCollection.cs`（及 GroupCollection.cs）：`IList<Capture>, IReadOnlyList<Capture>, IList`
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs`
- **分析**: 非泛型 IList 映射为 CSharpGenericIList<Object> 与类型化接口共存时，boolean add(Object)/Object get(int) 契约与类型化成员及显式 int IList.Add 实现在擦除后不可共存；桥接方法在冲突消解 pass 之后添加导致时序遗漏；iterator() 返回非泛型 CSharpEnumerator 与类型化枚举器不兼容。
- **修复**: ① 同时实现泛型集合接口时抑制非泛型 IList/ICollection/IEnumerable 的 implements 条目；② 桥接后重跑同签名冲突消解；③ 新 pass `RemoveNonGenericClashingMembers` 删除擦除冲突的非泛型 Object 成员并恢复 get$Class→get；④ 可迭代元素探测扩展 CSharpGenericIList/CSharpReadOnlyList，Iterable 桥接取类型化元素，iterator() 返回非泛型时包装 CSharpEnumerator.from()。新增红→绿测试 `TripleListInterfaceCollisionTests`（项目级管道，基线红确认），全量测试 2453 通过。

**状态**: ✅ Fixed

---

## Iteration 20 — Regex.java 找不到符号 Timeout / RuntimeFeature

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\Regex.java`
- **行号**: 71（Timeout.InfiniteTimeSpan）、105（RuntimeFeature.getIsDynamicCodeCompiled()）
- **错误信息**: `找不到符号 变量 Timeout` / `找不到符号 变量 RuntimeFeature`（compat 短名已发射但缺 import）
- **代码片段**:
  ```java
  public static final CSharpTimeSpan InfiniteMatchTimeout = Timeout.InfiniteTimeSpan;   // 缺 import Timeout
  if (RuntimeFeature.getIsDynamicCodeCompiled() && useOptionC()) { ... }              // 缺 import RuntimeFeature
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\System\Text\RegularExpressions\Regex.cs`（`RuntimeFeature.IsDynamicCodeCompiled`）与 `Regex.Timeout.cs`（`Timeout.InfiniteTimeSpan`）
- **根因分类**: 类型映射缺失 + Transformer 逻辑缺陷
- **涉及组件**: `config/TypeMappings.json`、`compat Timeout.java/RuntimeFeature.java`、`src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`（`MapStaticTypeReceiver`）
- **分析**: ① BCL 静态类型 `System.Threading.Timeout`、`System.Runtime.CompilerServices.RuntimeFeature` 无映射，访问落到未定义符号；② 补映射后，静态**属性**接收者经 `MapStaticTypeReceiver` 用 `MapType` 返回 compat 短名，但该函数只取名不注册 TypeMappings 的 required imports，导致 `RuntimeFeature.getIsDynamicCodeCompiled()` 缺 import。真实 FQN 是 `System.Runtime.CompilerServices.RuntimeFeature`（非 `System.Runtime.RuntimeFeature`）。Timeout 字段访问走 field 路径已自带 import，故仅属性路径缺。
- **修复**: ① TypeMappings.json 新增 Timeout 与 CompilerServices.RuntimeFeature 两条映射（含 compat imports）；② 新建 compat `Timeout`（InfiniteTimeSpan/Infinite）与 `RuntimeFeature`（getIsDynamicCodeCompiled/getIsDynamicCodeSupported）；③ `MapStaticTypeReceiver` 返回映射名时补注册该类型的 required imports（display + simple name 双 key）。新增测试 `TimeoutAndRuntimeFeatureMappingTests`（单文件 Timeout + 项目级 RuntimeFeature import + Registry 配置断言），全量测试 2456+7+570 通过。

**状态**: ✅ Fixed

---

## Iteration 21 — RegexRunner.java 未报告的异常 RegexMatchTimeoutException（受检异常）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\RegexRunner.java`
- **行号**: 314
- **错误信息**: `未报告的异常错误 dotnet.text.regularexpressions.RegexMatchTimeoutException; 必须对其进行捕获或声明以便抛出`
- **代码片段**:
  ```java
  throw new RegexMatchTimeoutException(runtext, runregex.pattern, CSharpTimeSpan.fromMilliseconds((long) (_timeout)));  // 受检异常未声明 throws
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\System\Text\RegularExpressions\RegexRunner.cs`（`TossTimeout`）；异常类定义 `RegexMatchTimeoutException.cs`（`: System.TimeoutException`）
- **根因分类**: 类型映射错误（基类映射到受检异常）
- **涉及组件**: `config/TypeMappings.json`
- **分析**: C# `RegexMatchTimeoutException : System.TimeoutException`，而 TypeMappings 把 `System.TimeoutException` 映射为受检的 `java.util.concurrent.TimeoutException`，导致转换产物 `extends TimeoutException` 成为受检异常，`throw` 处必须捕获/声明。C# 异常均为非受检，基类应映射到 compat 非受检链（`io.github.ningpp.compat.TimeoutException extends SystemException extends RuntimeException`）。
- **修复**: 将 TypeMappings.json 中 `System.TimeoutException` 的 import 由 `java.util.concurrent.TimeoutException` 改为 `io.github.ningpp.compat.TimeoutException`。新增红→绿测试 `TimeoutExceptionUncheckedMappingTests`（单文件转换 + Registry 配置断言），全量测试 2458+7+570 通过。

**状态**: ✅ Fixed

---

## Iteration 22 — Match.java 找不到符号 方法 Reset()（空条件调用未小写化）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\Match.java`
- **行号**: 74
- **错误信息**: `找不到符号 方法 Reset() 位置: 类型为 GroupCollection 的变量 _groupcoll`
- **代码片段**:
  ```java
  if (_groupcoll != null) { _groupcoll.Reset(); }   // 声明是 reset()，调用却用 Reset()
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\System\Text\RegularExpressions\Match.cs`（`_groupcoll?.Reset();`）；声明 `GroupCollection.cs`（`internal void Reset() => _groups = null;`）
- **根因分类**: Transformer 逻辑缺陷（Lowering）
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Statement/StatementTransformer.ExpressionAndReturn.cs`（`TransformConditionalAccessStatement`）
- **分析**: 语句级空条件调用 `_groupcoll?.Reset();` 被降级为 `if (_groupcoll != null) { ... }`，但 `TransformConditionalAccessStatement` 对成员绑定调用只取原始 C# 名做关键字转义，未套用普通调用的 camelCase 小写化，导致发射 `Reset()` 而声明已是 `reset()`。
- **修复**: 在空条件语句调用的通用分支套用与普通方法调用一致的 well-known 重命名 + 首字母小写（GetHashCode→hashCode 等）。新增红→绿测试 `ConditionalAccessMethodCasingTests`，Java/Project 全量测试无回归（2459+7）。

**状态**: ✅ Fixed

---

## Iteration 23 — Match.java 找不到符号 setIndex/setLength（private protected setter 被降为 private）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\Match.java`
- **行号**: 214、215
- **错误信息**: `找不到符号 方法 setIndex(int)/setLength(int) 位置: 类 Match`
- **代码片段**:
  ```java
  setIndex(interval[0]);    // 基类 Capture 的 setter 被生成为 private，子类不可见
  setLength(interval[1]);
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\System\Text\RegularExpressions\Capture.cs`（`public int Index { get; private protected set; }`）；调用点 `Match.cs`（`tidy`）
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/PropertyTransformer.cs`（`GetSingleAccessModifier`）
- **分析**: `private protected set` 产生 Private|Protected 双标志，`GetSingleAccessModifier` 按“private > protected”优先级错误保留 Private，setter 成为 private，子类 Match 无法调用。
- **修复**: `GetSingleAccessModifier` 新增 Private+Protected 组合判定，映射为 Protected（private protected 对派生类可见，最接近 Java protected）。新增红→绿测试 `PrivateProtectedSetterTests`，Java/Project 全量测试无回归（2460+7）。

**状态**: ✅ Fixed

---

## Iteration 24 — Regex.java RegexParser.parse 实参不匹配（CultureInfo 被误剥离）

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\Regex.java`
- **行号**: 128
- **错误信息**: `无法将类 RegexParser 中的方法 parse 应用到给定类型；需要 String,int,CultureInfo 找到 String,int`
- **代码片段**:
  ```java
  RegexTree tree = RegexParser.parse(pattern, roptions);   // 丢失第 3 个 culture 实参
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\System\Text\RegularExpressions\Regex.cs`（`RegexParser.Parse(pattern, roptions, culture ?? (...))`）
- **根因分类**: Transformer 逻辑缺陷
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`（parse 剥离逻辑）
- **分析**: “从 Parse 剥离末尾 CultureInfo/NumberStyles”优化以 `originalMethodName == "Parse"` 为条件过于宽泛；`RegexParser.Parse` 仅因名为 Parse 且末参是 CultureInfo（IFormatProvider）就被 `parseStripCount=1` 剥掉，但其 culture 是语义必需参数。
- **修复**: 将 Parse 剥离限定为“真正的数值解析接收者”（`TryGetParseHelperMethod` 命中）或 DateTime/DateTimeOffset（其 compat parse 无 culture 重载）；RegexParser 等项目类 Parse 不再剥离。新增红→绿测试 `CoalesceArgumentTests`，Java/Project 全量测试无回归（2461+7）。

**状态**: ✅ Fixed

---

## Iteration 25 — Regex.java CSharpHashtable(CSharpIDictionary) 找不到合适的构造器

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\Regex.java`
- **行号**: 169、177
- **错误信息**: `对于 CSharpHashtable(io.github.ningpp.compat.CSharpIDictionary), 找不到合适的构造器`
- **代码片段**:
  ```java
  caps = _coalesce1 != null ? _coalesce1 : new CSharpHashtable(value);   // value 为 CSharpIDictionary
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\System\Text\RegularExpressions\Regex.cs`（`Caps`/`CapNames` setter 中 `value as Hashtable ?? new Hashtable(value)`）
- **根因分类**: 类型映射缺失（compat API 缺口）
- **涉及组件**: `java/csharptojava-compat/.../CSharpHashtable.java`
- **分析**: C# `new Hashtable(IDictionary)` 拷贝构造被正确转换为 `new CSharpHashtable(CSharpIDictionary)`，但 compat CSharpHashtable 仅有 ()/int/Map/comparer 构造器，缺 `CSharpIDictionary` 拷贝构造器。
- **修复**: compat CSharpHashtable 新增 `CSharpHashtable(CSharpIDictionary)`，通过 `entrySet()` 逐项拷贝。新增红→绿 compat JUnit 测试 `CSharpHashtableIDictionaryCtorTest`（红：编译失败 → 绿：mvn test 通过），compat 全量测试通过。

**状态**: ✅ Fixed

---

## Iteration 26 — Regex.java asSpan(String[]) 找不到合适的方法

- **Java 文件**: `d:\cs-re-20260807\dotnet-text-regularexpressions\src\main\java\dotnet\text\regularexpressions\Regex.java`
- **行号**: 244
- **错误信息**: `对于 asSpan(java.lang.String[]), 找不到合适的方法`
- **代码片段**:
  ```java
  result = MemoryExtensions.asSpan(capslist).toArray();   // capslist 为 String[]
  ```
- **对应 C# 文件**: `D:\csharp-RegularExpressions\src\System\Text\RegularExpressions\Regex.cs`（`capslist.AsSpan().ToArray()`）
- **根因分类**: 类型映射缺失（compat API 缺口）
- **涉及组件**: `java/csharptojava-compat/.../MemoryExtensions.java`
- **分析**: compat MemoryExtensions.asSpan 仅有 String/byte[]/char[] 重载，缺通用 `T[]` 重载，故 `string[]` 的 `AsSpan()`（转 `asSpan(capslist)`）无匹配。
- **修复**: compat MemoryExtensions 新增泛型 `asSpan(T[])` 返回 `Span<T>`（基本类型 char[]/byte[] 因更具体重载仍优先绑定）。新增红→绿 compat JUnit 测试 `MemoryExtensionsAsSpanArrayTest`，compat 全量测试通过。注：RegexCharClass 的 `asSpan(String,int)`（2 参）为另一签名缺口，另行迭代。

**状态**: ✅ Fixed

