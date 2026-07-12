# C# → Java 转换迭代修复记录

## Iteration 1 — Self-Reference in POM

- **Java 文件**: D:\xml202607\system-private-xml\pom.xml
- **行号**: 60
- **错误信息**: `'dependencies.dependency.[io.github.ningpp:system-private-xml:0.0.1-SNAPSHOT]' for io.github.ningpp:system-private-xml:0.0.1-SNAPSHOT is referencing itself. @ line 60, column 21`
- **代码片段**:
  ```xml
  <dependency>
      <groupId>io.github.ningpp</groupId>
      <artifactId>system-private-xml</artifactId>
      <version>0.0.1-SNAPSHOT</version>
  </dependency>
  ```
- **对应 C# 文件**: D:\csharpxml\System.Private.Xml.csproj
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Pipeline/Compatibility/Packs.cs (SystemXmlPack) + src/CSharpToJava.Core/Pipeline/Planning/WorkspacePlanBuilder.cs
- **分析**: SystemXmlPack 注册了 Maven 依赖 io.github.ningpp:system-private-xml:0.0.1-SNAPSHOT（用于任何引用 dotnet.xml 命名空间的项目）。但是 System.Private.Xml.csproj 的 RootNamespace 就是 dotnet.xml，转换后模块名为 system-private-xml，导致该模块在依赖列表中引用了自己。
- **状态**: � 待修复

