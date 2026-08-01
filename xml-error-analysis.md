## Iteration 1 — ClassCastException: Integer[] cannot be cast to int[]
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/schema/XmlUntypedStringConverter.java`
- **行号**: 263
- **错误信息**: `Caused by: java.lang.ClassCastException: class [Ljava.lang.Integer; cannot be cast to class [I`
- **代码片段**:
  ```java
  if (itemTypeDst == s_int32Type) {
      return (int[]) toArray(dotnet.xml.XmlConvert.splitString(value, IntegerHelper.RemoveEmptyEntries), nsResolver, Integer.class);
  }
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Schema\XmlUntypedStringConverter.cs`（第 216 行 `return ToArray<int>(...)`）
- **根因分类**: Transformer / RuntimeClassParameterHelper
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Utilities/RuntimeClassParameterHelper.cs`、`src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`
- **分析**: C# 泛型方法 `ToArray<T>()` 被显式实例化为 `ToArray<int>()`，转换器为类型参数生成运行期 `Class<?>` 实参时，通过 `ToRuntimeTypeForClassLiteral` 把 `int` 映射为 `Integer.class`。由于 `toArray` 内部用 `TypeHelper.newArrayInstance(clazz, length)` 创建数组，`Integer.class` 生成 `Integer[]`，而调用点却被推断为返回 `int[]` 并插入了 `(int[])` 强制转换，导致运行时 `ClassCastException`。
- **修复**: 在 `RuntimeClassParameterHelper.ToClassArgument` 增加 `preferPrimitiveClassLiteral` 参数：为方法级类型参数（调用点）传 `true`，直接返回 `int.class` 等原始类型 class literal；类型级参数仍传 `false`，保持 `Integer.class` 以保证泛型类型擦除后的兼容性。
- **验证**: 新增回归测试 `GenericArrayMethodInstantiatedWithPrimitive_CallSiteUsesPrimitiveClassLiteral`（28 个 GenericArrayTypeParameterTests 全部通过），重新生成 `XmlUntypedStringConverter.java` 后调用点变为 `int.class`。
- **状态**: ✅ Fixed

## Iteration 2 — ClassCastException: List of xdt:untypedAtomic does not support conversion from String to int
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/schema/XmlUntypedStringConverter.java`
- **行号**: 263（修复前）；生成后 `TypeHelper.toWrapperType(clazz)` 出现在第 301 行
- **错误信息**: `ClassCastException: Xml type 'List of xdt:untypedAtomic' does not support a conversion from Clr type 'java.lang.String' to Clr type 'int'`
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Schema\XmlUntypedStringConverter.cs`（`ToArray<T>` 的 `typeof(T) == s_int32Type` 比较）
- **根因分类**: Transformer / TypeOperationTransformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/TypeOperationTransformer.cs`、`java/csharptojava-compat/src/main/java/io/github/ningpp/compat/TypeHelper.java`
- **分析**: 上一迭代修复了调用点使用 `int.class` 创建原始数组，但 `typeof(T)` 在泛型方法内部仍被转换为运行期 `Class<?>` 参数 `clazz`。当 `T` 实例化为 `int` 时，`clazz` 为 `int.class`，而 C# 代码中的 `typeof(T) == s_int32Type` 期望与 `Integer.class` 比较，导致 `fromString` 内部类型分支失败。
- **修复**: 在 `TypeOperationTransformer` 中将类型参数 `T` 的 `typeof(T)` 转换为 `TypeHelper.toWrapperType(clazz)`（或 `TypeHelper.toWrapperType(<runtimeClassParam>)`），并把 `TypeHelper.toWrapperType` 的访问级别从 `private` 提升到 `public`。
- **验证**: 新增回归测试 `GenericArrayTypeParameterTests.TypeOfTypeParameterInGenericArrayMethod_WrapsPrimitiveClassLiteralForComparison`；`dotnet test` 2317 全部通过。
- **状态**: ✅ Fixed
- **Commit**: `3e1b66dc`

## Iteration 3 — Infinite loop in `XmlTextReaderImpl.eatWhitespaces` during `SplitTextTests`
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java`
- **行号**: 7387（`private int eatWhitespaces(StringBuilder sb)`）
- **错误信息**: 测试挂起/超时，`SplitTextTests` 出现非终止循环
- **对应 C# 文件**: `d:\csharpxml\System\Xml\XmlTextReaderImpl.cs`（`EatWhitespaces` 方法包含跨嵌套循环的 `goto`）
- **根因分类**: GotoEliminator / StateMachineBuilder
- **涉及组件**: `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs`
- **分析**: `EatWhitespaces` 被转换为嵌套状态机后，内层状态机 while 中的 `goto` 目标位于外层循环。旧逻辑仅把 `__state` 赋给外层状态并 `break`/`continue`，但 `break` 只能退出内层 `switch`，无法退出内层状态机 `while`，导致内层状态机反复执行，形成死循环。
- **修复**: 在 `StateMachineBuilder` 中新增 `TryGetGeneratedStateMachineStateName` 识别 `__cs2jBlockStateN >= 0` 形式的状态机 while；在 `GotoTransitionRewriter` 和 `NestedBlockTransitionRewriter` 中用栈跟踪嵌套状态机。当 `goto` 发生在已生成的状态机 while 内部时，生成 `BreakThroughGeneratedStateMachine`：先把外层 `__state` 设为目标状态、设置 `__exit=true`、把内层状态变量设为 `-1`，然后 `continue`，使内层状态机退出并在外层继续目标状态。
- **验证**: 新增回归测试 `GotoEliminatorTests.Eliminate_GotoFromInnerLoopToOuterLoopLabel_ExecutesReadData`；`dotnet test` 2317 全部通过；`mvn clean package -e` 全模块 SUCCESS。
- **状态**: ✅ Fixed
- **Commit**: `74d88fb7`


## Iteration 4 — cannot assign value to final variable ✅ Fixed
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/XmlTextWriter.java`
- **行号**: 274, 276, 278, 304, 306, 315, 804, 858, 925, 975, 983, 992, 998, 1031, 1049, 1179, 1185, 1188, 1278
- **错误信息**: 无法为 final 变量 defaultNs/defaultNsState/mixed/prefix/name/declared/prevNsIndex/prefixCount/xmlLang/xmlSpace 分配值
- **代码片段**:
  ```java
  // TagInfo class fields all declared final:
  private static class TagInfo implements Cloneable {
      public final String name;
      public final String prefix;
      public final String defaultNs;
      ...
  }
  // But external code assigns them:
  _stack[_top].defaultNs = _stack[_top - 1].defaultNs;
  _stack[_top].name = localName;
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Core\XmlTextWriter.cs`
- **根因分类**: ReadOnlyStructMaker 误判
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/StructAnalyzer.cs`, `src/CSharpToJava.Core/ReadOnlyStructMaker/CallGraphBuilder.cs`
- **分析**: TagInfo 和 Namespace 是 mutable struct，其 internal 字段被外部类通过数组元素访问方式赋值（如 _stack[_top].name = localName）。CheckNonMigratable 只检查 public 字段的外部赋值，遗漏了 internal 字段同样可被同一程序集/类外部修改的情况，导致误判为可迁移至 readonly struct。

## Iteration 5-6 — ReadOnlyStructMaker comprehensive fix ✅ Fixed
- **Java 文件**: 多个文件 (XmlTextWriter, XmlTextReaderImpl, LineInfo, XmlNamedNodeMap, XmlNodeReaderNavigator, XmlEventCache)
- **错误信息**: 无法为 final 变量分配值 / 不兼容的类型 / 缺少返回值 / IndexOutOfBoundsException
- **根因分类**: ReadOnlyStructMaker 多个缺陷
- **涉及组件**: StructAnalyzer.cs, CallGraphBuilder.cs, CallSiteUpdater.cs, MethodMigrator.cs, ReadOnlyStructRewriter.cs, ProjectReadOnlyStructPreprocessor.cs
- **分析**: 
  1. 外部字段赋值检测只搜索当前文件，遗漏跨文件赋值
  2. CallSiteUpdater 仅按方法名匹配，导致 List.Add 被错误转换
  3. MethodMigrator 未重写隐式字段访问为 result.field
  4. L5 方法迁移不应添加 readonly 关键字（Java 会将字段标记为 final）
  5. L1 路径缺少非 private 字段的外部赋值检查
  6. struct 内部隐式 this 方法调用未被更新
  7. 跨文件调用点未被更新（需要第二遍处理）

## Iteration 7 — NullPointerException: XsdDateTime._dt is null (constructor bare call not updated)
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/schema/XsdDateTime.java`
- **行号**: 382 (及所有使用 `_dt` 的位置)
- **错误信息**: `java.lang.NullPointerException: Cannot invoke "io.github.ningpp.compat.CSharpDateTime.ticksToLdtPublic()" because "dateTime" is null`
- **代码片段**:
  ```java
  private XsdDateTime initiateXsdDateTime(Parser parser) {
      var result = this.clone();
      result._dt = new CSharpDateTime(parser.year, parser.month, parser.day, parser.hour, parser.minute, parser.second);
      // ...
      return result.clone();
  }
  // Constructor calls it but discards return value:
  public XsdDateTime(String text, int kinds) {
      this();
      Parser parser = new Parser();
      if (!parser.parse(text, kinds)) { throw ...; }
      initiateXsdDateTime(parser);  // <-- return value discarded! _dt stays null
  }
  ```
- **对应 C# 文件**: `D:\csharpxml\System\Xml\Schema\XsdDateTime.cs` (line 145, 150, 153)
- **根因分类**: ReadOnlyStructMaker / CallSiteUpdater
- **涉及组件**: `src/CSharpToJava.Core/ReadOnlyStructMaker/CallSiteUpdater.cs`
- **分析**: C# struct `XsdDateTime` 的 `void InitiateXsdDateTime(Parser)` 方法被 MethodMigrator 正确迁移为返回 struct 类型的方法，但 CallSiteUpdater 仅处理带显式接收者的调用 (`s.Method(args)`)，未处理构造函数中的裸调用 (`InitiateXsdDateTime(parser)`)，导致返回值被丢弃，`_dt` 字段永远为 null。
- **状态**: ✅ Fixed
