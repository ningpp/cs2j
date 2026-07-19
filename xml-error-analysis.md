# XML 转换首个错误分析

## 错误信息

```
[ERROR] /D:/cs-xml-20260716/system-private-xml/src/main/java/dotnet/xml/schema/ParticleContentValidator.java:[178,34] 对于format(boolean,java.lang.String), 找不到合适的方法
```

## 错误位置

- 生成文件：`d:\cs-xml-20260716\system-private-xml\src\main\java\dotnet\xml\schema\ParticleContentValidator.java:178`
- C# 源文件：`d:\csharpxml\System\Xml\Schema\ContentValidator.cs:1278`

## 生成代码片段

```java
String ctype = (getIsOpen() ? "Any" : "TextOnly");
System.out.println(String.format(DiagnosticsSwitches.getXmlSchemaContentModel().getEnabled(), "\t\t\tContentType:  " + ctype));
```

## C# 源码片段

```csharp
Debug.WriteLineIf(DiagnosticsSwitches.XmlSchemaContentModel.Enabled, "\t\t\tContentType:  " + ctype);
```

## 根因分析

`Debug.WriteLineIf(bool condition, string message)` 在 C# 中仅在 `condition` 为 true 时输出消息。
转换器未识别该 API，将其当作普通 `Console.WriteLine(string, object)` 处理，进而套用 `String.format(format, args...)` 包装，导致生成：

```java
System.out.println(String.format(booleanCondition, messageString));
```

Java 中不存在 `String.format(boolean, String)` 重载，因此编译失败。

## 修复方向

在 `InvocationExpressionTransformer.cs` 中增加对 `System.Diagnostics.Debug.WriteLineIf` / `WriteIf` 的专门处理，生成条件输出语句（如 `if (condition) System.out.println(message);`）或直接按 `Debug.Assert` 的处理方式注释掉（二者均为 `[Conditional("DEBUG")]`，Release 下会被 C# 编译器剔除）。
