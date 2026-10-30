# Java 转换验证计划（csharp-sdk / AGL）

日期：2026-10-30
状态：进行中

## 目标

SDK 与 AGL 转成 Java 后：

1. `mvn clean test-compile -e` 无编译错误；
2. `mvn clean install -e` 单元测试全部通过。

Java 前提条件（已满足）：两棵 `--unified-preprocessed-src` 树
（`D:\cs\cs-sdk-e756c1f2\.cs2j-preprocessed-src`、`D:\cs\cs-agl-e756c1f2\.cs2j-preprocessed-src`）
`dotnet build -c Release` 0 error、`dotnet test -c Release` 0 失败
（SDK 17,724 通过；AGL 613/613）。

## 环境

- Maven 3.9.9 / JDK 28-ea；本地仓库 `D:\mvnrepo`（settings.xml 注释了 localRepository，
  由 `mvn help:evaluate` 确认为 d:\mvnrepo）。
- compat 源码：`D:\code\cs2j\java\csharptojava-compat`（1.0-SNAPSHOT，已安装）。
- AGL 依赖 SDK 的 `io.github.ningpp:dotnet-*:0.0.1-SNAPSHOT`，验证 AGL 前先安装 SDK：
  `cd <sdk-java-root>; mvn clean install -e`（或安装步骤的 skipTests 变体用于编译期验证）。

## 复现命令

```powershell
# 生成（用户命令 + fix 后的 CLI）
dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s D:\csharp-sdk\csharp-sdk-without-json.sln -d d:\cs\cs-sdk-<rev> `
  --no-verify-build --no-verify-tests --unified-preprocessed-src

dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project `
  -s E:\agl-master\GraphLayout\agl.slnx -d d:\cs\cs-agl-<rev> `
  --no-verify-build --no-verify-tests --unified-preprocessed-src `
  --extra-deps io.github.ningpp:dotnet-xml:0.0.1-SNAPSHOT,io.github.ningpp:dotnet-text-regularexpressions:0.0.1-SNAPSHOT

# 验证
cd <java-root>; mvn clean test-compile -e    # 编译
cd <sdk-java-root>; mvn clean install -e     # SDK 测试 + 安装
cd <agl-java-root>; mvn clean install -e     # AGL 测试（先装 SDK）
```

## 工作树

- 基线（用户已有）：`D:\cs\cs-sdk-e756c1f2`、`D:\cs\cs-agl-e756c1f2`
- fix #1 验证树：`D:\cs\cs-sdk-fix1`、`D:\cs\cs-agl-fix1`
- 后续 fix 使用新的 `cs-sdk-fixN` / `cs-agl-fixN`，避免改动基线目录；最终验证回到基线目录重新生成。

## SDK javac 错误清单（fix #1 后，`D:\cs\_logs\sdk-fix1-mvn.log`）

| 类别 | 数量 | 位置 | 根因/修复 |
| --- | --- | --- | --- |
| 找不到符号 `set(String)` | 78 | XmlTextReaderImpl / XPathDocumentBuilder / Preprocessor / XmlSchemaEnumerationFacet 等 | `ResidualExtensionCallRewriter.RewriteOneArgumentSetValue` 全局把单参 `.setValue(` 改成 `.set(`，误伤用户 `SetValue` 方法；修复中（fix #2：ByRef 写点直接发 `set`，删除全局改写） |
| `contains(Object)` 弱访问 | 2 | `MS.Internal.Xml.XPath.StringFunctions` | 私有 helper `Contains` 被改造成 compat 接口方法；需改名或跳过并补 public 桥（fix #3） |
| 缺少返回值 | 2 | `dotnet.xml.schema.XmlSchemaCollection.add(Object)` | `void Add(T)` 被改成 `int add(Object)`，体内早退 `return;` 未改为 `return 0;`（fix #3） |
| 测试模块类型不匹配 | 28（首次 inventory） | dotnet-text-regularexpressions-tests | CSharpHashSet→Set、Capture/Group/MatchCollection→CSharpICollection、CSharpHashSet(char[])（fix #4+） |

## 修复记录

| # | 问题 | Commit | 验证 |
| --- | --- | --- | --- |
| 1 | StringBuilder.Length 写入被发成 `length(v)`（读映射当 setter） | `68f4ff0f` | StringBuilder 5/5；SDK 预处理器树 build/test 绿；AGL 预处理器树 build/test 绿；AGL `mvn clean test-compile -e` BUILD SUCCESS；SDK 仅剩上表后三类错误；单元套件 14 个既有失败不变 |
| 2 | 全局 `.setValue(`→`.set(` 误伤用户方法 | 待提交 | 单元：4 个真实回归用例转绿（ObjectsImport 5 个为陈旧断言，另有 5 个既有失败）；ByRef 过滤 20/22 通过（余 1 既有）；全量 4123/4142（10 失败均既有/陈旧）
| 3 | 集合 compat 桥：void→非 void 遗留裸 return；私有 helper 被擦成接口形状导致弱访问 | 待提交 | 单元：新增 4 测试；CompatCollectionConformance 6/7（余 1 陈旧断言）；全量 4127/4146（同 10 个既有失败） |
