# 错误08: 对象初始化器类型丢失为 Object (Object Initializer Type Lost)

## 受影响文件
**GeometryGraphReader (XmlReaderSettings / XmlTextReader):**
- `GeometryGraphReader.java:[141,16]` — `setIgnoreComments(boolean)` 在 Object 上找不到
- `GeometryGraphReader.java:[142,16]` — `setIgnoreWhitespace(boolean)` 在 Object 上找不到
- `GeometryGraphReader.java:[144,25]` — `new Object(InputStream)` 构造器不匹配
- `GeometryGraphReader.java:[230,27]` — `NodeType` 在 Object 上找不到
- `GeometryGraphReader.java:[326,43]` — `Name` 在 Object 上找不到
- `GeometryGraphReader.java:[345,61]` — Object 无法转换为 XmlReader
- 以及 IsEmptyElement, ReadState, Value, LineNumber, LinePosition 等大量相同错误

**GeometryGraphWriter (XmlWriterSettings):**
- `GeometryGraphWriter.java:[129,16]` — `setIndent(boolean)` 在 Object 上找不到

**DebugCurveCollection (JsonSerializerOptions):**
- `DebugCurveCollection.java:[67,16]` — `setWriteIndented(boolean)` 在 Object 上找不到

**SteinerCdt (ProcessStartInfo):**
- `SteinerCdt.java:[328-332]` — `setCreateNoWindow`, `setUseShellExecute`, `setFileName`, `setArguments` 在 Object 上找不到

## 错误信息
```
找不到符号
  符号:   方法 setIgnoreComments(boolean)
  位置: 类型为java.lang.Object的变量 _obj150
```

## 原始 C# 代码
```csharp
// GeometryGraphReader.cs
public GeometryGraphReader(Stream streamP) {
    var settings = new XmlReaderSettings { 
        IgnoreComments = false, 
        IgnoreWhitespace = true 
    };
    xmlTextReader = new XmlTextReader(streamP);
    XmlReader = XmlReader.Create(xmlTextReader, settings);
}
```

C# 使用对象初始化器语法 `new XmlReaderSettings { Prop = value }` 来创建并设置属性。

## 生成的 Java 代码
```java
public GeometryGraphReader(InputStream streamP) {
    var _obj150 = new Object();           // ← 错误：应该是 XmlReaderSettings 类型
    _obj150.setIgnoreComments(false);     // Object 上没有这个方法
    _obj150.setIgnoreWhitespace(true);    // Object 上没有这个方法
    var settings = _obj150;
    xmlTextReader = new Object(streamP);  // ← 错误：应该是 XmlTextReader 类型
    setXmlReader(XmlReader.create(xmlTextReader, settings));
}
```

## 错误原因分析

### 根本原因：类型映射缺失导致类型退化为 Object

转换器在处理 C# 对象初始化器时使用以下模式：
1. 创建对象：`var _obj = new TypeName()`
2. 设置属性：`_obj.setXxx(value)`  
3. 赋值给目标变量

但当 `XmlReaderSettings`、`XmlTextReader`、`JsonSerializerOptions`、`ProcessStartInfo` 等 .NET 框架类型没有对应的 Java 类型映射时，转换器将它们降级为 `Object`，导致所有属性访问都失败。

### 转换器缺陷
这是**类型映射不完整**的问题。转换器需要：
1. 在 `TypeMappings.json` 中添加这些 .NET 类型到 Java 等价类型的映射
2. 对于没有直接等价的类型（如 `XmlReaderSettings`），需要映射到 compat 库中的包装类

以下是缺失的映射：
- `System.Xml.XmlReaderSettings` → `io.github.ningpp.compat.XmlReaderSettings`
- `System.Xml.XmlTextReader` → `io.github.ningpp.compat.XmlTextReader`  
- `System.Text.Json.JsonSerializerOptions` → 对应的 Java JSON 库配置类
- `System.Diagnostics.ProcessStartInfo` → `ProcessBuilder` 相关

### 正确的 Java 代码
```java
// 需要 compat 库中的 XmlReaderSettings 类
public GeometryGraphReader(InputStream streamP) {
    var settings = new XmlReaderSettings();
    settings.setIgnoreComments(false);
    settings.setIgnoreWhitespace(true);
    xmlTextReader = new XmlTextReader(streamP);
    setXmlReader(XmlReader.create(xmlTextReader, settings));
}
```
