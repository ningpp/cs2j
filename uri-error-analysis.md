# URI ReadOnly 迭代修复分析记录

## Iteration 1 — CS0305 泛型缺参 [build]
- **C# 文件**: d:\cs-uri-812\.cs2j-readonly-src\src\Common\System\Collections\Generic\ArrayBuilder.cs
- **行号**: 68, 140, 151, 182, 183
- **错误信息**: error CS0305: 使用泛型 类型"ArrayBuilder<T>"需要 1 个类型参数
- **代码片段**:
  ```csharp
  public ArrayBuilder Add(T item)          // ← 应为 ArrayBuilder<T>
  ...
  public ArrayBuilder With_array(T[] _array) => new ArrayBuilder(_array: _array, _count: this._count);
  ```
- **对应原始 C# 文件**: src\Common\System\Collections\Generic\ArrayBuilder.cs
- **根因分类**: 方法迁移
- **涉及组件**:
  - src\CSharpToJava.Core\ReadOnlyStructMaker\MethodMigrator.cs (83, 93 行生成裸 struct 名)
  - src\CSharpToJava.Core\ReadOnlyStructMaker\ReadOnlyStructRewriter.cs (602, 606, 782 行)
  - src\CSharpToJava.Core\ReadOnlyStructMaker\ReadOnlyStructMaker.cs (With_ 检测)
  - src\CSharpToJava.Core\ReadOnlyStructMaker\CallSiteUpdater.cs (383 行 default())
- **分析**: 泛型 struct `ArrayBuilder<T>` 的 void 方法迁移/WithXxx 生成把返回类型与对象创建写成裸 `ArrayBuilder`（缺 `<T>`），产生 CS0305。所有生成类型处需携带类型参数。
- **修复**:
  1. `MethodMigrator` 新增 `TypeSyntax? structType` 参数，替代裸 `IdentifierName(structName)`
  2. `ReadOnlyStructRewriter` 新增 `StructTypeSyntax(node)` helper（有 TypeParameterList 时生成 `GenericName`），用于 WithXxx 返回类型、`new ArrayBuilder<T>(...)`、setter 版 WithXxx
  3. `ReadOnlyStructMaker` With_ 方法检测同时接受 `GenericNameSyntax`（并修复 `&&`/`||` 优先级引入的匹配错误）
  4. `CallSiteUpdater` 新增 `_structTypeParams` 字典，`default(...)` 生成完整泛型类型
- **测试**: `GenericStruct_MigratedMethodsKeepTypeParameter`、`GenericStruct_SetterWithMethod_KeepsTypeParameter`（先红后绿）
- **验证**: 全量 dotnet test 2683+691+7 = 3381 通过，0 失败
- **状态**: ✅ Fixed

## Iteration 2 — Segments 空数组 [test]
- **C# 文件**: d:\cs-uri-812\.cs2j-readonly-src\tests\ExtendedFunctionalTests\UriTests.cs
- **行号**: 88（`Assert.Equal<int>(ss.Length, 3)`，Expected 0 / Actual 3）
- **错误信息**: Assert.Equal() Failure: Values differ — Expected: 0, Actual: 3（uri.Segments 返回空数组）
- **代码片段**:
  ```csharp
  var pathSegments = new ArrayBuilder<string>();
  ...
  pathSegments.Add(path.Substring(current, (next - current) + 1));  // ← 未被改写为赋值
  ...
  segments = pathSegments.ToArray();
  ```
- **对应原始 C# 文件**: src\System\Uri.cs（Segments getter）
- **根因分类**: CallSite
- **涉及组件**: src\CSharpToJava.CLI\ProjectReadOnlyStructPreprocessor.cs (933-950, 1095 行)
- **分析**: 项目级 `UpdateCrossFileCallSites` 扫描重写后树时只识别 `IdentifierNameSyntax` 返回类型；Iteration 1 修复后泛型 struct 的迁移方法返回 `ArrayBuilder<T>`（GenericNameSyntax），Add 未被登记为已迁移方法 → `migratedMethodToStruct.Count == 0` → 整个调用点更新 pass 直接 return → `pathSegments.Add(...)` 结果被丢弃，Segments 恒为空。且诊断 StructName 为 "ArrayBuilder<T>"（ToDisplayString 带泛型参数），与扫描端裸标识符 "ArrayBuilder" 不匹配导致门禁失配，整轮扫描被跳过。
- **修复**:
  1. `UpdateCrossFileCallSites` 改为 internal 可测试
  2. 返回类型/out 参数类型/WithXxx 检测同时接受 `GenericNameSyntax`
  3. 门禁容忍带泛型参数的 key（"ArrayBuilder<T>"），并在构建 `globalMigratedMethods` 时规范化 key（剥离 `<...>`）
- **测试**: `GenericMigratedStruct_VoidCallSite_UpdatedToAssignment`（先红后绿；跨文件 pass 输出无空格 `pathSegments=pathSegments.Add(`）
- **验证**: 全量 dotnet test 2684+691+7 = 3382 通过，0 失败；`make-readonly` Release 自检 build+test 全部成功（686 测试 0 失败）
- **状态**: ✅ Fixed

## Iteration 3 — ArgumentOutOfRangeException ctor + getObjectData super 调用 [build]
- **Java 文件**: system-private-uri/src/main/java/dotnet/system/UriParser.java:220、Uri.java:4693、UriFormatException.java:34
- **行号**: 220 / 4693 / 34
- **错误信息**:
  1. ` 对于ArgumentOutOfRangeException(java.lang.String,int,java.lang.String), 找不到合适的构造器 `（构造器 io.github.ningpp.compat.ArgumentOutOfRangeException(String,String,Object) 不适用; int无法转换为java.lang.String）
  2. ` UriFormatException.java: 找不到符号 方法 getObjectData(io.github.ningpp.compat.__suppress__,io.github.ningpp.compat.__suppress__) `
- **代码片段**:
  `java
  // UriParser.java:220
  throw new ArgumentOutOfRangeException("components", components, SystemPrivateUriSR.getNet_uri_NotJustSerialization());
  // UriFormatException.java:33-35
  public void getObjectData(__suppress__ serializationInfo, __suppress__ streamingContext) {
      super.getObjectData(serializationInfo, streamingContext);
  }
  // Uri.java:340-341（ISerializable.GetObjectData 自转发，无限递归）
  public void getObjectData(__suppress__ serializationInfo, __suppress__ streamingContext) {
      getObjectData(serializationInfo, streamingContext);
  }
  `
- **对应 C# 文件**: src/System/UriExt.cs:331、src/System/UriScheme.cs:121、src/System/Uri.cs:430-433、src/System/UriFormatException.cs:33-36
- **根因分类**: 类型映射缺失（compat 构造器顺序）+ Transformer（__suppress__ 方法体转发调用未剥离）
- **涉及组件**:
  - java/csharptojava-compat/.../ArgumentOutOfRangeException.java（缺 C# 顺序 3 参构造器 (paramName, actualValue, message)）
  - src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs（suppressed-param 方法体保留 super.getObjectData/自递归调用）
- **分析**: C# 
ew ArgumentOutOfRangeException(paramName, actualValue, message) 保持参数顺序映射到 Java，但 compat 只声明了 (paramName, message, actualValue) 顺序的构造器，int 无法转 String 导致编译失败；同时带 __suppress__ 参数的方法体中的 ase.GetObjectData(info,ctx) 转发被原样输出为 super.getObjectData(...)，而 compat 基类（FormatException 等）没有该方法；自转发调用则生成无限递归（可编译但行为错误）。
- **修复**:
  1. compat ArgumentOutOfRangeException 新增构造器 (String paramName, Object actualValue, String message)
  2. MethodTransformer 对带 __suppress__ 参数的方法：剥离把 suppressed 参数作为实参透传的调用语句（super.getObjectData/自递归 getObjectData），结构化语句与 raw 语句均处理，嵌套 block 递归处理；表达式体则退化为空块
- **测试**: GetObjectData_BaseForwarding_StripsSuperCall、GetObjectData_SelfForwarding_StripsRecursiveCall（先红后绿，SuppressTypeTests）
- **验证**: 全量 dotnet test 2686+691 = 3377 通过；compat mvn install（1286 测试通过）
- **状态**: ✅ Fixed
