# xml-error-analysis.md

## Iteration 1 — cannot find symbol (join)
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/XmlEventCache.java`
- **行号**: 80
- **错误信息**: `找不到符号 符号: 方法 join() 位置: 类型为dotnet.xml.StringConcat的变量 _singleText`
- **代码片段**:
  ```java
  if (_singleText.getCount() != 0) {
      writer.writeString(_singleText.join());
      return;
  }
  ```
- **对应 C# 文件**: `System/Xml/Core/XmlEventCache.cs` 与 `System/Xml/Core/StringConcat.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs` (L643-655)
- **分析**: `TransformMemberInvocation` 把所有无参 `.GetResult()` 调用都改写成 `.join()`，未限定接收者类型。`StringConcat.GetResult()` 是普通取值方法，应生成 `.getResult()`，却被错写成 `.join()`。
- **状态**: ✅ Fixed

## Iteration 2 — cannot find symbol (variable e0)
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/XmlConvert.java`
- **行号**: 990
- **错误信息**: `找不到符号 符号: 变量 e0 位置: 类 dotnet.xml.XmlConvert`
- **代码片段**:
  ```java
  public static boolean isNegativeZero(double value) {
      if (value == 0 && doubleToInt64Bits(value) == doubleToInt64Bits(-e0)) {
          return true;
      }
      return false;
  }
  ```
- **对应 C# 文件**: `System/Xml/XmlConvert.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/LiteralExpressionTransformer.cs` (StripLeadingZeros)
- **分析**: C# 将 `-0e0` 解析为对字面量 `0e0` 的一元取负。`StripLeadingZeros` 未识别科学计数法，把 `0e0` 的尾数零全部去掉，输出 `e0`，再带上负号变成 `-e0`。
- **状态**: ✅ Fixed

## Iteration 3 — cannot find symbol (Decimal.createFrom_long)
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/XmlSqlBinaryReader.java`
- **行号**: 3763
- **错误信息**: `找不到符号 符号: 方法 createFrom_long(long) 位置: 类 io.github.ningpp.compat.Decimal`
- **代码片段**:
  ```java
  case XSD_UNSIGNEDLONG:
  return Decimal.createFrom_long(valueAsULong());
  ```
- **对应 C# 文件**: `System/Xml/BinaryXml/XmlBinaryReader.cs` (L3720-3721)
- **根因分类**: 类型映射缺失
- **涉及组件**: `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/Decimal.java`
- **分析**: C# `new decimal(ulong)` 被映射为 `Decimal.createFrom_long(long)`，以保留无符号 64 位值（Java long 无法直接表示大于 Long.MAX_VALUE 的 ulong）。但 compat 库 `Decimal` 中未实现该静态工厂方法，导致生成的 Java 代码编译失败。
- **状态**: ✅ Fixed

## Iteration 4 — 一元运算符 '++' 操作数类型错误（枚举）
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/Xsl/XsltOld/Compiler.java`
- **行号**: 589
- **错误信息**: `一元运算符 '++' 的操作数类型dotnet.xml.Xsl.XsltOld.ScriptingLanguage错误`
- **代码片段**:
  ```java
  public void addScript(String source, ScriptingLanguage lang, String ns, String fileName, int lineNumber) {
      validateExtensionNamespace(ns);
      for (ScriptingLanguage langTmp = ScriptingLanguage.JScript; langTmp.ordinal() <= ScriptingLanguage.CSharp.ordinal(); langTmp++) {
      CSharpHashtable typeDecls = _typeDeclsByLang[langTmp.getValue()];
  ```
- **对应 C# 文件**: `System/Xml/Xsl/XsltOld/Compiler.cs` (L785)
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/UnaryExpressionTransformer.cs`
- **分析**: C# 枚举支持 `++`/`--`（底层整数值 ±1），但 Java 枚举不支持一元 `++`/`--`。`BinaryExpressionTransformer` 已将条件 `langTmp <= ScriptingLanguage.CSharp` 改写为 `.ordinal()` 比较，但 `UnaryExpressionTransformer` 对 `langTmp++` 未做枚举特化，直接输出 `langTmp++`，导致编译失败。
- **状态**: ✅ Fixed

## Iteration 5 — no suitable constructor (StringBuilder 4-arg)
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/Xsl/XsltOld/RecordBuilder.java`
- **行号**: 493
- **错误信息**: `对于StringBuilder(java.lang.String,int,int,int), 找不到合适的构造器`
- **代码片段**:
  ```java
  newComment = new StringBuilder(comment, begin, index, 2 * comment.length());
  ```
- **对应 C# 文件**: `System/Xml/Xsl/XsltOld/RecordBuilder.cs`
- **根因分类**: Transformer
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/ObjectCreationTransformer.cs`
- **分析**: C# `StringBuilder(string, int, int, int)` 构造函数在 Java 中没有直接等价物；Java `StringBuilder` 没有 4 参数构造函数。转换器原本按普通构造函数处理，直接输出 4 个参数，导致编译失败。
- **状态**: ✅ Fixed

## Iteration 6 — cannot find symbol (MethodInfo.GetBaseDefinition / ParameterInfo.IsOptional)
- **Java 文件**: `system-private-xml/src/main/java/dotnet/xml/Xsl/XsltOld/XsltCompileContext.java`
- **行号**: 122
- **错误信息**: `找不到符号 符号: 方法 getBaseDefinition() 位置: 类 java.lang.reflect.Method`
- **代码片段**:
  ```java
  for (int i = 0; i < length; i++) {
      if (StringHelper.compare(name, methods[i].getName(), false) == 0) {
          if (!publicOnly || methods[i].getBaseDefinition().getIsPublic()) {
              methods[free++] = methods[i];
          }
      }
  }
  ```
- **对应 C# 文件**: `System/Xml/Xsl/XsltOld/XsltCompileContext.cs` (L119, L972)
- **根因分类**: Transformer / 类型映射缺失
- **涉及组件**: `src/CSharpToJava.Core/Transformers/Expression/Transformers/IdentifierExpressionTransformer.cs`, `src/CSharpToJava.Core/Transformers/Expression/Transformers/InvocationExpressionTransformer.cs`, `java/csharptojava-compat/src/main/java/io/github/ningpp/compat/ReflectionHelper.java`
- **分析**: `System.Reflection.MethodInfo` 映射为 `java.lang.reflect.Method`，但 C# 的 `GetBaseDefinition()` 和 `IsPublic` 在 Java `Method` 上不存在；同样 `ParameterInfo.IsOptional` 在 `java.lang.reflect.Parameter` 上不存在。转换器对这几个反射成员没有特殊处理，直接按默认命名规则输出 `getBaseDefinition()` 和 `getIsPublic()`，导致编译失败。
- **状态**: 🔄 In Progress
