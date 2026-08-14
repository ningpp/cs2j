# json814 — C#→Java 转换 (d:\csharpjson → d:\cs-json-814) 修复状态

- **转换命令**: `dotnet run -c Release --project src/CSharpToJava.CLI/CSharpToJava.CLI.csproj -- convert-project -s d:\csharpjson -d d:\cs-json-814`
- **构建命令**: `cd d:\cs-json-814 ; mvn clean package -e > mvn-build.log 2>&1`
- **日志文件**: `d:\cs-json-814\mvn-build.log`

## 当前状态（2026-08-15）

| 阶段 | 状态 |
|------|------|
| C#→Java 转换 | ✅ 582 文件全部转换，0 失败 |
| javac 编译 | ✅ 0 错误（原 1280 条 / 123 issue 全部清零） |
| mvn testCompile | ✅ 通过 |
| surefire 运行时测试 | 🔴 仍有失败（约 504 失败 + 1810 错误），待继续修复运行时语义 |

## 已完成的编译修复（摘要）

1. 局部函数提升：调用点名称按 `OriginalDefinition` 归一化；Class<T> 令牌注册按提升后唯一 Java 名键控；前向引用令牌按声明顺序排序；override/接口实现不注入 Class 参数；泛型实参令牌按实参 Java 类型精确匹配时参数化强转。
2. 非泛型 IList/IDictionary：实现映射到 CSharpIList/CSharpIDictionary，新增 `CSharpIList.from` 适配器与强转桥接；迭代器返回类型升级覆盖 CSharpDictEnumerator。
3. 属性：getter/setter 优先语义符号名（转义标识符）；`new` 隐藏属性不兼容类型重命名 `_newSlot` + `@CompatPropertyName`。
4. 语句/初始化器：字段初始化器多语句提升到构造器/静态块；using 资源预语句顺序；属性 getter 内局部函数提升；丢弃赋值 `_ = expr` 保留副作用。
5. 字面量/类型：静态构造 LINQ static；数组→IEnumerable 包装；byte 流 ToArray 产 byte[]；uint 十六进制/下划线窄化；int→long/ulong 加 L。
6. JsonSerializer 非泛型重载 `_0tp` 消歧；`Task(Action,state)`→runAsync、`Task.Start()` 空操作；`GetEnumValues`→EnumHelper；`Activator.CreateInstance`→compat；EndOfStreamException→compat RuntimeException。
7. compat 补齐：ImmutableArray.create/toImmutableArray、ConcurrentHashMapHelper.create、MapHelper.putValue(CSharpIDictionary)、Assert.throwsAny(Supplier)、Assert.equal 对象按 toString 比较、JsonSerializerSettings.setReferenceLoopHandling 等。

详细分析见 `json-error-analysis.md`（Iteration 29-57）。
