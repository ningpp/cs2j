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

## Iteration 9 — TextWriter/PrintWriter 无参构造器
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/modulecore/src/test/java/OLEDB/Test/ModuleCore/CLTMConsole.java:[23,22]`
- **出错信息**: `对于PrintWriter(没有参数), 找不到合适的构造器`
- **代码片段** (`CLTMConsole.java`):
  ```java
  public class CLTMConsole extends PrintWriter {
      //Constructor
  public CLTMConsole() {
      }
  ```
- **对应 C# 文件**: `d:\csharpxml\Tests\Common\ModuleCore\cltmconsole.cs:16-23`
  ```csharp
  public class CLTMConsole : TextWriter
  {
      public CLTMConsole()
      {
      }
  ```
- **根因分类**: TypeMapping / Compat 库缺失
- **涉及组件**: `config/TypeMappings.json`、`java/csharptojava-compat/.../CSharpTextWriter.java`、`java/csharptojava-compat/.../StreamWriter.java`、`src/CSharpToJava.Core/Transformers/Expression/Utilities/ExpressionTransformerHelpers.cs`
- **分析**: `System.IO.TextWriter` 在 `TypeMappings.json` 中被映射为 `java.io.PrintWriter`。C# 中 `CLTMConsole` 继承抽象类 `TextWriter` 并声明无参构造器；转换后 Java 代码 `extends PrintWriter` 且构造器为空，但 `PrintWriter` 没有无参构造器，导致编译失败。此外，`TextWriter.NewLine` 被转换为 `this.getNewLine()`，而 `PrintWriter` 也不存在该方法。
- **修复**: 引入 compat 类 `CSharpTextWriter`（继承 `PrintWriter`、提供无参构造器及 `getNewLine`/`setNewLine`），将 `System.IO.TextWriter` 映射到 `CSharpTextWriter`；让 `StreamWriter` 继承 `CSharpTextWriter` 以保持多态赋值合法；修正 `StringWriter` 传向 `TextWriter` 参数时的 `new PrintWriter(...)` 包装为 `new CSharpTextWriter(...)`；添加 `TextWriterMappingTests.ClassExtendingTextWriter_WithParameterlessCtor_MapsToCSharpTextWriter` 红→绿测试。
- **状态**: ✅ Fixed

## Iteration 10 — ObjectHolder 内部类实例化参数错误
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/serialization/ReflectionXmlSerializationReader.java:[435,43]`
- **出错信息**: `类型dotnet.xml.serialization.ReflectionXmlSerializationReader.ObjectHolder不带有参数`
- **代码片段** (`ReflectionXmlSerializationReader.java`):
  ```java
  [ERROR] /D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/serialization/ReflectionXmlSerializationReader.java:[435,43] 类型dotnet.xml.serialization.ReflectionXmlSerializationReader.ObjectHolder不带有参数
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Serialization\ReflectionXmlSerializationReader.cs:2110`（嵌套非泛型类 `internal class ObjectHolder { public object Object; }`）与多处 `out object`/`out string` 参数
- **根因分类**: Transformer / 名称冲突
- **涉及组件**: `src/CSharpToJava.Core/Transformers/HolderTypeResolver.cs`、`src/CSharpToJava.Core/Lowering/LowerRefOut.cs`
- **分析**: `ReflectionXmlSerializationReader` 内部定义了非泛型嵌套类 `ObjectHolder`。转换器为 `out` 参数生成的 compat `ObjectHolder<T>` 引用若使用简单名 `ObjectHolder<T>`，会与嵌套类冲突；即使使用全限定名 `io.github.ningpp.compat.ObjectHolder<T>`，配合 `import io.github.ningpp.compat.*;` 的通配导入，Java 仍会把简单名解析到嵌套类，导致泛型参数报错。`HolderTypeResolver` 已统一生成全限定 Holder 类型（参数类型与 `new` 表达式均使用 `io.github.ningpp.compat.ObjectHolder<...>`），从而避开嵌套类冲突。
- **状态**: ✅ Fixed

## Iteration 11 — ICustomAttributeProvider 未映射
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/serialization/XmlAttributes.java:[52,26]`
- **出错信息**: `找不到符号  符号: 类 ICustomAttributeProvider`
- **代码片段** (`XmlAttributes.java`):
  ```java
  public XmlAttributes(ICustomAttributeProvider provider) {
      Object[] attrs = provider.getCustomAttributes(false);
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Serialization\XmlAttributes.cs:100`（`public XmlAttributes(ICustomAttributeProvider provider)`）
- **根因分类**: TypeMapping 缺失 / Compat 库缺失
- **涉及组件**: `config/TypeMappings.json`、`java/csharptojava-compat/.../ICustomAttributeProvider.java`
- **分析**: C# `System.Reflection.ICustomAttributeProvider` 没有类型映射，也没有对应的 Java compat 类型。生成的 Java 代码保留 `ICustomAttributeProvider` 简单名但无 import，导致编译失败。该接口在 csharpxml 中主要被 `XmlAttributes`、`SoapAttributes`、`XmlReflectionImporter`、`SoapReflectionImporter` 用作参数类型，调用方法包括 `GetCustomAttributes(bool)` 与 `GetCustomAttributes(Type, bool)`。
- **修复**:
  1. 在 compat 库新增 `io.github.ningpp.compat.ICustomAttributeProvider` 接口，提供 `getCustomAttributes(boolean)`、`getCustomAttributes(Class<?>, boolean)`、`isDefined(Class<?>, boolean)` 默认实现。
  2. 让 `MemberInfo` 继承 `ICustomAttributeProvider`；将 `MemberInfo.getCustomAttributes(boolean)` 返回类型从 `List<Object>` 改为 `Object[]`，以匹配生成代码中的 `Object[] attrs = provider.getCustomAttributes(false)`。
  3. 更新 `CustomAttributeExtensions` 将 `Object[]` 转为 `List<Object>`/Iterable。
  4. 在 `config/TypeMappings.json` 中新增 `System.Reflection.ICustomAttributeProvider` → `ICustomAttributeProvider` 映射。
  5. 添加单元测试 `ICustomAttributeProviderMappingTests.ICustomAttributeProvider_Parameter_MappedToCompatType` 验证参数类型、import 和方法调用均被正确转换。
- **状态**: ✅ Fixed

## Iteration 12 — 枚举减法操作数类型错误
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/xpath/XPathNavigator.java:[1472,45]`
- **出错信息**: `二元运算符 '-' 的操作数类型错误`
- **代码片段** (`XPathNavigator.java`):
  ```java
  public static boolean isText(XPathNodeType type) {
      return Integer.compareUnsigned(type - XPathNodeType.Text, (XPathNodeType.Whitespace - XPathNodeType.Text)) <= 0;
  }
  ```
- **对应 C# 文件**: 待定位
- **根因分类**: 待分析
- **涉及组件**: 待定位
- **分析**: C# 中枚举相减得到底层整型；Java 枚举不支持 `-` 运算符。转换器未将 `XPathNodeType.Whitespace - XPathNodeType.Text` 改写为 `(int)XPathNodeType.Whitespace - (int)XPathNodeType.Text`。
- **修复**: 在 `BinaryExpressionTransformer` 中新增 `TryTransformEnumArithmeticOperation`，对非 `Flags` 枚举操作数自动追加 `getValue()`（显式值枚举）或 `ordinal()`（简单枚举）调用；保留 `Flags` 枚举的原始行为（已映射为 `int`/`long`）。新增红测试 `ExplicitValueEnum_Subtraction_UsesGetValue` 与 `SimpleEnum_Subtraction_UsesOrdinal` 验证。
- **状态**: ✅ Fixed

## Iteration 13 — MyDict 与 CSharpDictionary.get 名称冲突
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/MS/Internal/Xml/Cache/XPathNodeInfoAtom.java` 等
- **出错信息**: `名称冲突: MyDict 中的 get(Type1) 和 CSharpDictionary 中的 get(java.lang.Object) 具有相同疑符`
- **代码片段**:
  ```java
  public class MyDict extends CSharpDictionary<Type1, Type2> {
      public Type2 get(Type1 key) { ... }
  }
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Cache\XPathNodeInfoAtom.cs` 中的嵌套字典类
- **根因分类**: Compat 库接口签名 / 泛型擦除
- **涉及组件**: `java/csharptojava-compat/.../CSharpGenericIDictionary.java`、`CSharpDictionary.java`、`CSharpSortedDict.java`、`CSharpSortedList.java`
- **分析**: `CSharpGenericIDictionary` 接口声明 `V get(Object key)`，子类 `MyDict` 转换后生成 `V get(Type1 key)`。由于 Java 泛型擦除，两个方法签名冲突，编译失败。
- **修复**: 将 compat 库中 `CSharpGenericIDictionary`、`CSharpDictionary`、`CSharpSortedDict`、`CSharpSortedList` 的 `get(Object key)` 改为 `get(K key)`，并调整 `getOrDefault` 实现。新增单元测试 `MyDictIndexerTests.MyDict_ImplementsCSharpDictionary_GetUsesKeyType` 验证转换后生成 `get(Type1 key)` 且无冲突。
- **状态**: ✅ Fixed

## Iteration 14 — Exception.Source / InnerException 无 Java 对应
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/MS/Internal/Xml/Cache/CTestBase.java` 等
- **出错信息**: `找不到符号: 方法 getSource()` / `找不到符号: 方法 getInnerException()`
- **代码片段**:
  ```java
  String s = e.getSource();
  Exception inner = e.getInnerException();
  ```
- **对应 C# 文件**: `d:\csharpxml\...` 中使用 `Exception.Source` 与 `Exception.InnerException` 的位置
- **根因分类**: API 映射缺失 / Compat 库缺失
- **涉及组件**: `java/csharptojava-compat/.../ExceptionCompat.java`、`src/CSharpToJava.Core/Transformers/Expression/Transformers/ExceptionApiRewriter.cs`、`IdentifierExpressionTransformer.cs`、`InvocationExpressionTransformer.cs`
- **分析**: .NET `Exception` 的 `Source`/`InnerException` 属性以及 `GetInnerException()` 方法在 Java `RuntimeException` 中没有直接对应。转换器最初未重写这些成员，导致生成的 Java 代码调用不存在的方法。
- **修复**: 新增 `ExceptionCompat` 辅助类，提供 `getSource(RuntimeException)` 与 `getInnerException(RuntimeException)` 静态方法。在 `IdentifierExpressionTransformer` 与 `InvocationExpressionTransformer` 中检测接收者为异常类型的 `Source`/`InnerException`/`GetInnerException()`，统一改写为 `ExceptionCompat` 调用。新增 `ExceptionCompatTests` 验证属性访问与方法调用均正确转换。
- **状态**: ✅ Fixed

## Iteration 15 — protected internal 方法跨包不可见
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/XPathNavigator.java` 等
- **出错信息**: `setSourceObject(...) 在 XmlSchemaValidationException 中是 protected 访问控制`
- **代码片段**:
  ```java
  ex.setSourceObject(this);
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\XPathNavigator.cs` 等处调用 `XmlSchemaValidationException.SetSourceObject`
- **根因分类**: 访问修饰符映射
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Member/MethodTransformer.cs`、`ConstructorTransformer.cs`
- **分析**: C# `protected internal` 表示“同一程序集或派生类可访问”，而 Java 没有直接等价修饰符。转换器原先将其映射为 `protected`，导致跨包调用方无法访问。
- **修复**: 在 `MethodTransformer` 与 `ConstructorTransformer` 的 `ConvertModifiers` 中，将同时包含 `Protected` 与 `Public`（即 `protected internal`）的结果规范化为 `public`。更新 `ProtectedInternalConstructorTests` 与新增 `ProtectedInternalMethodTests` 验证访问修饰符输出。
- **状态**: ✅ Fixed

## Iteration 16 — Type.FullName 无 Java 对应
- **阶段**: Java 编译
- **Java 文件**: 待定位
- **出错信息**: `找不到符号: 方法 getFullName()`
- **代码片段**:
  ```java
  String name = type.getFullName();
  ```
- **对应 C# 文件**: 使用 `Type.FullName` 的位置
- **根因分类**: API 映射缺失
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`
- **分析**: C# `Type.FullName` 被转换器默认映射为 `getFullName()`，但 Java `Class` 类只有 `getName()`，没有 `getFullName()`，语义也不完全一致。
- **修复**: 在 `IdentifierExpressionTransformer` 中检测 `System.Type` 的 `FullName` 属性访问，生成 `TypeHelper.getFullName(type)` 调用并自动引入 `io.github.ningpp.compat.TypeHelper` import。更新相关单元测试。
- **状态**: ✅ Fixed

## Iteration 17 — IEquatable<T>.Equals 未映射为 equalsTo
- **阶段**: Java 编译
- **Java 文件**: `/D:/cs-xml-20260716/system-private-xml/src/main/java/MS/Internal/Xml/Cache/XPathNodeInfoAtom.java`
- **出错信息**: `MS.Internal.Xml.Cache.XPathNodeInfoAtom不是抽象的, 并且未覆盖io.github.ningpp.compat.IEquatable中的抽象方法equalsTo(MS.Internal.Xml.Cache.XPathNodeInfoAtom)`
- **代码片段**:
  ```java
  public class XPathNodeInfoAtom implements IEquatable<XPathNodeInfoAtom> {
      public boolean Equals(XPathNodeInfoAtom other) { ... }
  }
  ```
- **对应 C# 文件**: `d:\csharpxml\System\Xml\Cache\XPathNodeInfoAtom.cs`
- **根因分类**: 接口映射 / 方法映射
- **涉及组件**: `config/TypeMappings.json`、`src/CSharpToJava.Core/Transformers/Type/ClassTransformer.cs`、`StructTransformer.cs`、`src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`、`src/CSharpToJava.TypeMapping/TypeMappingRegistry.cs`
- **分析**: C# `IEquatable<T>` 没有 Java 对应；转换器原先在 `ClassTransformer`/`StructTransformer` 中过滤掉了该接口，导致生成的类虽然实现了 C# 接口逻辑，但 Java 端缺少 `implements`。同时 `IEquatable<T>.Equals(T)` 未映射为 compat 接口要求的 `equalsTo(T)`。
- **修复**: 在 `config/TypeMappings.json` 中新增 `System.IEquatable<T>` → `io.github.ningpp.compat.IEquatable<T>` 映射。移除 `ClassTransformer` 与 `StructTransformer` 对 `IEquatable<T>` 的过滤，使其输出到 `implements` 列表。在 `InvocationExpressionTransformer` 中检测 `IEquatable<T>.Equals(T)` 调用并映射为 `equalsTo`；在 `TypeMappingRegistry` 中增加对构造泛型基类型（如 `System.IEquatable<T>`）的方法映射查找。新增 `IEquatableMappingTests` 与更新 `StructTransformerTests` 验证类/结构体实现接口并生成 `equalsTo` 方法。
- **状态**: ✅ Fixed

## Iteration 18 — 委托 Invoke 方法名映射错误
- **阶段**: 单元测试 / Java 编译
- **Java 文件**: N/A（单元测试先行发现）
- **出错信息**: `Action<T>.Invoke` 被转换为 `run` 而非 `accept`
- **代码片段**:
  ```java
  handler.run(42); // 应为 handler.accept(42)
  ```
- **对应 C# 文件**: 使用 `Action<T>.Invoke` 等委托调用的位置
- **根因分类**: TypeMapping 方法查找
- **涉及组件**: `src/CSharpToJava.TypeMapping/TypeMappingRegistry.cs`、`src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`
- **分析**: `TypeMappings.json` 中已配置 `System.Action`1.Invoke → accept`，但 `TypeMappingRegistry` 对构造泛型类型的方法查找未命中非反引号形式的映射（如 `System.Action`1` 与 `System.Action<T>`）。
- **修复**: 在 `TypeMappingRegistry.MapMethod` 中增加对构造泛型基类型名称（去掉 arity 反引号）的兜底查找，并优先检查接口实现方法。更新 `DelegateInvokeMappingTests` 验证 `Action.Invoke → run`、`Action<T>.Invoke → accept`、`Func<T>.Invoke → apply`。
- **状态**: ✅ Fixed

## Tooling — iterate-xml-fix.ps1 实时日志与错误处理
- **问题**: 原脚本使用 `Start-Process` 并将 stdout/stderr 重定向到日志文件，导致终端无实时输出，无法观察转换/构建进度；`LASTEXITCODE` 捕获不可靠；Maven 警告被 PowerShell 误判为错误。
- **修复**: 重构 `Invoke-Process` 函数，使用 `& $Executable @Arguments 2>&1 | Tee-Object -FilePath $LogFile` 实现控制台实时输出与日志文件同时写入；在子脚本块内设置 `$ErrorActionPreference = "Continue"` 忽略非致命 stderr 警告；直接返回 `$LASTEXITCODE`。日志路径统一放到 `$PSScriptRoot` 避免目标目录权限问题。
- **状态**: ✅ Fixed
