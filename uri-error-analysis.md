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

## Iteration 4 — compat dotnet.system.Uri 类路径冲突 + WebSockets 测试 getAbsoluteUri 找不到符号 [build]
- **Java 文件**: system-private-uri-functional-tests/.../WebSocketsUriParserTest.java（36-59 行等，找不到符号 getAbsoluteUri/getScheme/...）
- **行号**: 36 起批量
- **错误信息**: 找不到符号 方法 getAbsoluteUri() 位置: 类型为dotnet.system.Uri的变量 uri
- **代码片段**:
  `java
  dotnet.system.Uri uri = new dotnet.system.Uri(input);
  Assert.equal(input, uri.getAbsoluteUri());   // ← 符号解析到 compat 桩类
  `
- **对应 C# 文件**: tests/FunctionalTests/WebSocketsUriParserTest.cs
- **根因分类**: 类型映射缺失（compat 库同名类遮蔽）
- **涉及组件**: java/csharptojava-compat/src/main/java/dotnet/system/Uri.java、UriKind.java（WPF 用最小桩）；生成的测试模块 pom 中 csharptojava-compat 依赖排在 system-private-uri 之前
- **分析**: compat jar 内含 dotnet/system/Uri.class（XAML 生成代码用最小桩，仅有 toString/getOriginalString），与转换生成的完整 dotnet.system.Uri 类路径相同；javac 按 classpath 顺序先命中 compat 桩类，导致所有属性 getter 找不到符号。删除 compat 中 dotnet.system 包下的 Uri/UriKind 桩类（Application.loadComponent 参数改为 Object）后冲突消除。
- **修复**: 删除 compat 的 dotnet/system/Uri.java 与 UriKind.java（含 package-info），Application.loadComponent(Window, Object)
- **验证**: compat mvn install 1286+2 测试通过；converter 全量 dotnet test 3377 通过
- **状态**: ✅ Fixed

## Iteration 5 — 单标签域名 HostNameType 返回 Basic [test]
- **Java 文件**: WebSocketsUriParserTest/IdnHostNameValidationTest 等（Expected: Dns, Actual: Basic/Unknown）
- **行号**: WebSocketsUriParserTest.java:40 等
- **错误信息**: Assert.equal() failure: Values differ — Expected: Dns, Actual: Basic
- **代码片段**:
  `java
  // DomainNameHelper.isValidByIri 的 label 内容检查被双倍递增破坏
  MemorySegment _ptrPost5 = curPos;
  curPos = (advance(curPos));
  MemorySegment _ptrPost6 = curPos;
  curPos = (advance(curPos));          // ← curPos++ 被执行两次
  `
- **对应 C# 文件**: src/System/DomainNameHelper.cs（IsValidByIri 的 (*curPos++ < 0xA0) && !IsValidDomainLabelCharacter(*(curPos - 1), ...)）
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Expression/Transformers/BinaryExpressionTransformer.cs (522-538 行)
- **分析**: 比较运算符（<,>,<=,>=）的 uint 掩码检测用字符串 Contains("& 0xFFFFFFFFL") 判断，先对操作数做了一次完整 Transform 再在标准路径再次 Transform —— 含 *ptr++ 的操作数被物化两次（两个 _ptrPost 临时变量 + 两次 curPos 递增）。isValidByIri 的 label 检查中 curPos 跳过偶数位字符并读到标签终止符（':' 等），单标签域名校验失败 → 落入 BasicHostType。
- **修复**: 掩码检测改为语法级 ContainsUIntCast（子树含 (uint) 转换），不再对操作数重复 Transform；语义级 uint/ulong 分支保留
- **测试**: PostIncrementInsideComparisonWithReadBack_AdvancesExactlyOnce（先红后绿，PointerPostIncrementShortCircuitTests）
- **验证**: 全量 dotnet test 2687+691 通过；trace 副本运行确认 ws/http 单标签 host → Dns
- **状态**: ✅ Fixed

## Iteration 6 — ArgumentException ParamName 顺序 + IRI 扩展超长不抛 [test]
- **Java 文件**: UriBuilderTests.java（Expected: value/uri/uriString, Actual: 消息文本/null）、IriTest.java:435（Expected UriFormatException, 未抛）
- **行号**: UriBuilderTests.java:125 等、IriTest.java:435
- **错误信息**:
  1. Assert.equal() failure: Expected: value, Actual: Invalid URI: The URI scheme is not valid.（paramName 与 message 颠倒）
  2. Assert.throws() failure: No exception thrown — Expected: dotnet.system.UriFormatException（IRI 扩展长度检查未触发）
- **代码片段**:
  `java
  // UriBuilder.java:217 — C# 参数顺序 (message, paramName) 保持原样
  throw new ArgumentException(SystemPrivateUriSR.getNet_uri_BadScheme(), "value");
  // compat 原构造器把第一个字符串当 paramName → 颠倒
  `
- **对应 C# 文件**: src/System/UriBuilder.cs、src/System/IriHelper.cs（EscapeUnescapeIri）
- **根因分类**: 类型映射缺失（compat 构造器顺序）+ compat 语义（Encoding 替换字符）
- **涉及组件**:
  - java/csharptojava-compat/.../ArgumentException.java（构造器应为 (message, paramName)）
  - java/csharptojava-compat/.../ArgumentNullException.java、ArgumentOutOfRangeException.java（super 调用顺序跟随修正）
  - java/csharptojava-compat/.../Encoding.java（默认 encoder 替换字符应为 U+FFFD）
- **分析**: (1) .NET ArgumentException(string message, string paramName) —— compat 写成了 (paramName, message)，ParamName 取到消息文本；(2) Java CharsetEncoder 默认替换为 '?'（1 字节），.NET 默认 EncoderReplacementFallback 为 U+FFFD（UTF-8 下 3 字节 EF BF BD）；IriHelper 中孤立代理项按 9 字符转义（3 字节），compat 只产出 1 字节 → 扩展后长度不足 65535 → SizeLimit 不抛。
- **修复**: ArgumentException 构造器顺序改为 (message, paramName) 并同步修正子类 super 调用与单参构造器；Encoding 默认 encoderReplacement 改为可编码时用 "\uFFFD"（ASCII 等自动回退 '?'），getBytes(String)/getByteCount(String) 统一走 encoder 路径
- **测试**: compat EncodingSurrogateTest（孤立代理项 → EF BF BD 3 字节；合法代理对 → 4 字节）
- **验证**: compat mvn install 全部通过；mvn clean install -e → BUILD SUCCESS（functional 608 + unit 28 + extended 49 测试 0 失败）
- **状态**: ✅ Fixed
