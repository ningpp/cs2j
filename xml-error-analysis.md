# cs-xml 转换错误分析日志

## Iteration 0 — goto-preprocessor-error
- **阶段**: C# → Java 转换（goto 预处理）
- **Java 文件**: N/A（转换未生成 Java 文件）
- **出错信息**: `System\Xml\Xsl\XPathConvert.cs: goto/label syntax remains after preprocessing`
- **代码片段**:
  ```csharp
  #else
      goto LDone;
  }
  #endif
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Xsl\XPathConvert.cs` 等 4 个文件
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs`
- **分析**: `StateMachineBuilder` 重写了 `VisitMethodDeclaration`、`VisitConstructorDeclaration`、`VisitOperatorDeclaration` 等，但缺少 `VisitConversionOperatorDeclaration`。`XPathConvert.cs` 中 `FloatingDecimal` 的 `explicit operator double` 等方法体含 goto/label，因此未被状态机改写，残留 goto 被 `ProjectGotoPreprocessor` 检测为 Error，导致转换失败。
- **状态**: ✅ Fixed

## Iteration 1 — goto-preprocessor-error (nested switch labels)
- **阶段**: C# → Java 转换（goto 预处理）
- **Java 文件**: N/A（转换未生成 Java 文件）
- **出错信息**:
  - `[Error] System\Xml\Schema\DtdParser.cs: goto/label syntax remains after preprocessing`
  - `[Error] System\Xml\Schema\DtdParserAsync.cs: goto/label syntax remains after preprocessing`
  - `[Error] System\Xml\Schema\Inference\Infer.cs: goto/label syntax remains after preprocessing`
- **代码片段** (`DtdParser.cs`):
  ```csharp
  default:
      if (needWhiteSpace && !_whitespaceSeen && _scanningFunction != ScanningFunction.ParamEntitySpace)
      {
          Throw(_curPos, SR.Xml_ExpectingWhiteSpace, ParseUnexpectedToken(_curPos));
      }
      _tokenStartPos = _curPos;
  SwitchAgain:
      switch (_scanningFunction)
      {
          ...
          case ScanningFunction.ParamEntitySpace:
              _whitespaceSeen = true;
              _scanningFunction = _savedScanningFunction;
              goto SwitchAgain;
          ...
      }
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Schema\DtdParser.cs`、`d:\csharpxml\System\Xml\Schema\DtdParserAsync.cs`、`d:\csharpxml\System\Xml\Schema\Inference\Infer.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/GotoEliminator/StateMachineBuilder.cs`
- **分析**: `StateMachineBuilder.SplitToBlocks`/`Flatten` 只展平方法体顶层的 `BlockSyntax`，不识别嵌套在 `switch` 节中的 `LabeledStatementSyntax`。`DtdParser.cs` 的 `SwitchAgain` 等标签位于外层 switch 的 `default` 节内，`goto SwitchAgain` 甚至位于内层 switch 中；`Infer.cs` 的大量标签（如 `DEC_PART:`、`EXPONENT:`）也都位于 switch 节内部。这些标签未被收录到状态机的 `labelToIndex`，导致相应 `goto` 未被重写，预处理后被 `ProjectGotoPreprocessor` 检测为 Error。
- **状态**: ✅ Fixed

## Iteration 2 — System.Globalization.CompareOptions 未映射
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/MS/Internal/Xml/XPath/StringFunctions.java:[16,28]`
- **出错信息**: `程序包System.Globalization不存在`
- **代码片段** (`StringFunctions.java`):
  ```java
  import System.Globalization.CompareOptions;
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\XPath\Internal\StringFunctions.cs`（`using System.Globalization;`）
- **根因分类**: TypeMapping / Compat
- **涉及组件**: `config/TypeMappings.json`、`java/csharptojava-compat/.../CompareOptions.java`
- **分析**: `System.Globalization.CompareOptions` 没有类型映射，转换器保留了原始命名空间导入，而 Java 中不存在 `System.Globalization` 包。同时 compat 库缺少 `CompareOptions` 类型，导致 `CompareOptions.Ordinal` 等枚举常量无法解析。
- **修复**:
  1. 在 compat 库新增 `io.github.ningpp.compat.CompareOptions`（使用 `int` 常量，以支持 C# 中的位运算和 `int` 转换）。
  2. 在 `TypeMappings.json` 中补充 `System.Globalization.CompareOptions` 与 `CompareOptions` 到 compat 的映射。
  3. 添加单元测试 `ProjectCompareOptionsStaticMember_UsesCompatImportOnly` 验证导入被正确替换。
- **状态**: ✅ Fixed

## Iteration 3 — System.Uri 外部依赖缺失
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/XmlTextReaderImpl.java:[11,21]` 等
- **出错信息**: `找不到符号`（`dotnet.system.Uri`、`dotnet.system.UriKind`）
- **代码片段** (`XmlTextReaderImpl.java`):
  ```java
  import dotnet.system.Uri;
  import dotnet.system.UriKind;
  ```
- **对应 C# 文件**: 多个 `System.Uri`/`UriKind` 的使用（如 `d:\csharpxml\System\Xml\XmlTextReaderImpl.cs`）
- **根因分类**: CompatibilityPack / Maven 依赖
- **涉及组件**: `src/CSharpToJava.Core/Pipeline/Compatibility/Packs.cs`
- **分析**: `SystemUriPack` 的 `IsApplicable` 仍检测旧的 `dotnet.uri` 包，而 `System.Uri` 的实际 TypeMapping 输出到 `dotnet.system` 包，导致生成的 POM 缺少 `io.github.ningpp:system-private-uri` 依赖。
- **修复**: 将 `SystemUriPack.IsApplicable` 的检测字符串改为 `dotnet.system.Uri`，使引用 `System.Uri` 的模块自动生成对 `system-private-uri` 的 Maven 依赖。
- **状态**: ✅ Fixed

## Iteration 4 — 泛型类静态字段引用类级类型参数
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/Xsl/Runtime/XmlQuerySequence.java:[50,42]`
- **出错信息**: `无法从静态上下文中引用非静态 类型变量 T`
- **代码片段** (`XmlQuerySequence.java`):
  ```java
  public class XmlQuerySequence<T> implements CSharpGenericIList<Object>, Iterable<Object> {
      private final Class<?> tClass;
      public static final XmlQuerySequence<T> Empty = new XmlQuerySequence<T>(tClass);
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Xsl\Runtime\XmlQuerySequence.cs`
  ```csharp
  public class XmlQuerySequence<T> : IList<T>, System.Collections.IList
  {
      public static readonly XmlQuerySequence<T> Empty = new XmlQuerySequence<T>();
      ...
  }
  ```
- **根因分类**: Transformer / 类型参数擦除
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/FieldTransformer.cs`
- **分析**: C# 允许泛型类的静态字段使用类级类型参数 `T`（每个闭合构造类型拥有独立的静态字段实例）。Java 不允许在静态上下文中引用外层类的类型参数，因此 `public static final XmlQuerySequence<T> Empty` 及其初始化器 `new XmlQuerySequence<T>(tClass)` 均不合法。转换器为处理 `new T[]` 而引入的运行时 `Class<?>` 字段 `tClass` 是实例成员，在静态字段初始化器中无法访问。
- **修复方向**: 将依赖类级类型参数的静态字段转换为泛型静态方法（例如 `public static <T> XmlQuerySequence<T> Empty(Class<?> tClass)`），并在调用点补充运行时类参数。
- **状态**: 🔄 In Progress

## Iteration 5 — HybridDictionary 未映射
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/Xsl/XsltOld/Compiler.java:[57,13]`
- **出错信息**: `找不到符号`（`类 HybridDictionary`）
- **代码片段** (`Compiler.java`):
  ```java
  private CSharpObjStack _stylesheets;
      // Current import stack
  private HybridDictionary _documentURIs = new HybridDictionary();
      // import/include documents, who is here has its URI in this.documentURIs
  private NavigatorInput _input;
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Xsl\XsltOld\Compiler.cs:76`
- **根因分类**: TypeMapping 缺失
- **涉及组件**: `config/TypeMappings.json`
- **分析**: `System.Collections.Specialized.HybridDictionary` 没有类型映射，转换器保留了原始 C# 类型名，Java 端不存在 `HybridDictionary` 类，导致编译失败。
- **修复**: 在 `config/TypeMappings.json` 中补充 `System.Collections.Specialized.HybridDictionary` → `CSharpHashtable` 映射，复用 compat 中已有的 `CSharpHashtable` 类型。
- **状态**: ✅ Fixed

## Iteration 6 — MethodBase 未映射
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/extensions/ExtensionMethods.java:[58,49]` 等
- **出错信息**: `找不到符号`（`类 MethodBase`）
- **代码片段** (`ExtensionMethods.java`):
  ```java
  private static MethodBase filterMethodBases(MethodBase[] methodBases, Class[] parameterTypes, String methodName) {
      if (methodBases == null || StringHelper.isNullOrEmpty(methodName)) {
          return null;
      }
      var matchedMethods = filterMethodBases_ProceduralLinq1(methodBases, methodName, methodBases);
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Extensions\ExtensionMethods.cs:31`
- **根因分类**: TypeMapping 缺失 / Compat 库接口不完整
- **涉及组件**: `config/TypeMappings.json`、`java/csharptojava-compat/.../MemberInfo.java`
- **分析**: `System.Reflection.MethodBase` 没有类型映射，转换器保留了原始 C# 类型名；同时 compat 的 `MemberInfo` 接口缺少 `getParameters()` 默认方法，即使映射为 `MemberInfo` 后，`_linqitem.getParameters()` 仍无法通过编译。
- **状态**: ✅ Fixed

## Iteration 7 — xunit abstractions 未映射
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/modulecore/src/test/java/OLEDB/Test/ModuleCore/XmlInlineDataDiscoverer.java:[22,49]` 等
- **出错信息**: `找不到符号`（`类 IDataDiscoverer`、`程序包 csharp.xunit.Abstractions 不存在`）
- **代码片段** (`XmlInlineDataDiscoverer.java`):
  ```java
  public class XmlInlineDataDiscoverer implements IDataDiscoverer {
      private static Class toRuntimeType(csharp.xunit.Abstractions.ITypeInfo typeInfo) {
          var reflectionTypeInfo = (typeInfo instanceof IReflectionTypeInfo ? (IReflectionTypeInfo)(typeInfo) : null);
  ```
- **对应 C# 文件**: `d:\csharpxml\Tests\Common\ModuleCore\XunitRunner.cs:16`
- **根因分类**: TypeMapping 缺失 / Compat 库缺失
- **涉及组件**: `config/TypeMappings.json`、`java/csharptojava-compat/...`
- **分析**: 
  - 初步分析：`Xunit.Abstractions` 与 `Xunit.Sdk` 中的类型没有类型映射与 Java 实现，转换器按命名空间映射生成 `csharp.xunit.Abstractions.*` 后无法解析。
  - 补充根因：xunit 2.9.3 中 `IDataDiscoverer` 实际位于 `Xunit.Sdk` 命名空间（程序集 `xunit.core`），而非 `Xunit.Abstractions`。原 TypeMappings 仅配置了 `Xunit.Abstractions.IDataDiscoverer`，导致实际编译解析出的 `Xunit.Sdk.IDataDiscoverer` 无法命中映射，生成的 `implements IDataDiscoverer` 缺少对应 import。其余类型（`ITypeInfo`、`IMethodInfo`、`IAttributeInfo`、`DataAttribute` 等）命名空间正确。
- **修复**:
  - 在 `config/TypeMappings.json` 中新增 `Xunit.Sdk.IDataDiscoverer → IDataDiscoverer` 映射，import 指向 `csharp.xunit.Sdk.IDataDiscoverer`。
  - 在 `java/csharptojava-compat` 中新增 `csharp.xunit.Sdk.IDataDiscoverer` 接口（复用 `csharp.xunit.Abstractions` 中的参数类型）。
  - 新增红测试 `ResolvedXunitCoreInterface_Implemented_MappedType_AddsImport`，使用真实 `xunit.core.dll` 引用复现 `Xunit.Sdk.IDataDiscoverer` 解析后 import 缺失问题。
- **状态**: ✅ Fixed

## Iteration 8 — StringCollection 未映射
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/serialization/ImportContext.java:[73,12]`
- **出错信息**: `找不到符号`（`类 StringCollection`）
- **代码片段** (`ImportContext.java`):
  ```java
  public StringCollection getWarnings() {
      return getCache().getWarnings();
  }
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Serialization\ImportContext.cs:13`
- **根因分类**: TypeMapping 缺失
- **涉及组件**: `config/TypeMappings.json`
- **分析**: C# 中 `System.Collections.Specialized.StringCollection` 没有类型映射。转换器按命名空间映射生成 `System.Collections.Specialized.StringCollection` 后，Java 端不存在该类型，导致编译失败。
- **修复**: 在 `config/TypeMappings.json` 中新增 `System.Collections.Specialized.StringCollection` → `CSharpList<String>` 映射，并添加单元测试 `StringCollectionTypeMappingTests.StringCollection_FieldAndReturn_MappedToCSharpListOfString` 验证转换后不再出现 `StringCollection`，且生成正确 import。
- **状态**: ✅ Fixed
