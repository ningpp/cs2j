# 闭型 java.lang.reflect.Type 元数据链路设计(取代 ThreadLocal 层注册表)

日期:2026-09-22
状态:已实现(设计定稿)
关联:MyInt16TwoDimensionList 家族 "Cannot get the value of a token type 'StartArray' as a string";
约 18 轮 ProjectPasses 正则补丁 + TypeHelper ThreadLocal 作用域缓解的根治替代。

## 1. 问题陈述

cs2j 生成的 System.Text.Json 移植(dotnet-text-json)反序列化/序列化嵌套泛型集合成员
(如 `List<List<short>>` → `CSharpList<CSharpList<Integer>>`)时,内层元素转换器被解析成
StringConverter。根因:成员/元素类型在元数据链路
(`JsonClassInfo.addProperty` → `JsonSerializerOptions.getConverter` →
`IEnumerableConverterFactory`)中只以**擦除 Class** 传递;compat
`TypeHelper.getGenericArguments(Class)` 依赖 ThreadLocal 表
(TYPE_ARGUMENTS 待消费队列 / CONSUMED_ARGUMENTS 回放 / MEMBER_ARGUMENTS 单槽)恢复类型实参:

- 属性级转换器在元数据**构建期**逐个"注册→消费"尚能对齐;
- 但元素 ClassInfo **读取期才懒创建**(`getElementClassInfo()` → `getOrAddClass(裸 Class)`),
  此刻层链已被后续成员替换、队列已空,`getGenericArguments` 回放 CONSUMED 得到
  `[String]` → 元素转换器 = StringConverter(trace 实测
  `getGenericArguments(CSharpList) = [String] [repeat of consumed layer]`)。

ThreadLocal 作用域补丁族(PatchElementArgumentScope / PatchMemberArgumentCapture /
PatchMemberClassInfoLookup / PatchWriteElementPropertyInfo)只能缓解、且带来全量跑才复现的
顺序依赖(~28 个嵌套集合 WRITE 用例)。

## 2. 设计原则

1. **闭型来源**:反射签名即闭型。发射出的 Java getter/setter/field 保留泛型签名,
   `getter.getGenericReturnType()` 天然携带 `CSharpList<CSharpList<Integer>>` 完整嵌套闭型。
2. **显式贯穿**:闭型 `java.lang.reflect.Type` 作为显式数据存储在
   `JsonPropertyInfo`(成员声明闭型)与 `JsonClassInfo`(类信息闭型)上,沿
   `getRuntimeClassInfo()` / `getElementClassInfo()` 两条导航点显式传递;
   解析(元素/键/值类型)一律从闭型 `ParameterizedType.getActualTypeArguments()` 读取,
   **与反射顺序、调用时序无关**。
3. **硬性禁令**:该链路不新增、不依赖任何 ThreadLocal
   (TYPE_ARGUMENTS / CONSUMED_ARGUMENTS / MEMBER_ARGUMENTS / CALLSITE)。
4. **兼容**:`deserialize/serialize` 的 Class 公开 API 与 TypeReference 路径不破坏
   (其会话级 ThreadLocal 注册表属入口语义,保留);数组类型继续走 Class 路径
   (自身携带组件类型);纯 Class 场景回退旧逻辑。
5. **测试先行**:cs2j 侧仿 `StructClonePatchTests`(合成 ConversionResult + Execute +
   断言 GeneratedCode);compat 侧 JUnit 单元测试;下游再生成全量回归为最终闸。

## 3. 链路逐点

### 3.1 compat(手写运行时,io.github.ningpp.compat)

| 新 API | 作用 |
|---|---|
| `PropertyInfo.getGenericPropertyType()` | 返回成员声明闭型 `Type`(getter.getGenericReturnType() / setter 首参泛型),**不触发 registerMemberType**。 |
| `TypeHelper.declaredGenericType(MemberInfo)` | MemberInfo → 闭型(PropertyInfo 走 getGenericPropertyType,FieldInfo 走 field.getGenericType());无泛型返回 null。生成码 initialize 时经此打点,零 ThreadLocal 副作用。 |
| `TypeHelper.collectClosureLayers(Type)` | 闭型按层展开为 `LinkedHashMap<Class, Type[]>`(外层先注册;同 raw 类首层胜出),镜像"外到内消费"语义但为纯函数。 |
| `TypeHelper.genericArgumentsInLayers(Class raw, Type[] layers)` | `layers == null` → 委托旧 `getGenericArguments(raw)`(Class/TypeReference 路径,保持现状);`layers != null` → 在闭型层里找 raw 匹配层返回其实参擦除类,不查任何 ThreadLocal;再退化为子类型声明/继承链/类型参数界(全部非注册表路径)。 |
| `TypeHelper.closedCacheKey(Type)` | 闭型缓存 key:参数化 → `ClosedGenericKey(raw, args)`;非参数化 → 原 Class。`_converters` 与 `_classes` 共用,互不串扰。 |
| `TypeHelper.createConverterForClosedType(factory, closed, options)` | 反射探测工厂的闭型入口 `createConverter(Class, Type[], options)`,有则调(实参层≠null),无则回退旧 `createConverter(Class, options)`。compat 不引用生成类型。 |
| `ConcurrentHashMapHelper.getOrAddUntyped(map, key, value)` | 以**预计算闭型 key**做 `putIfAbsent` 语义(不走 converterCacheKey,零 ThreadLocal);泛型 raw 处理。 |
| `TypeHelper.substituteTypeVariables(Type, Map<String,Type>)` | 把泛型定义上的声明类型按实参绑定替换为闭型:裸 TypeVariable 查绑定(未解析返回 null);`ParameterizedType` 逐实参递归替换(任一未解析返回 null);具体类型原样返回(恒等比较跳过)。供闭型构造器成员重绑定用。 |

### 3.2 生成码补丁(ProjectPasses.cs,门控 `ClosedTypeMetadataEnabled`)

均以"锚点正则 + 不匹配即响亮告警/单测拦截"的方式写,与现有补丁同风格:

1. **JsonPropertyInfo.java**(`PatchClosedMemberType`):
   - 注入字段 `__cs2jDeclaredGenericType` + 访问器;
   - `initialize` 内 `setMemberInfo(memberInfo)` 后追加
     `setDeclaredGenericType(TypeHelper.declaredGenericType(memberInfo))`(memberInfo 为 null
     的类信息伪属性自然跳过);
   - `getRuntimeClassInfo()` 的擦除查找替换为:闭型参数化 →
     `getOptions().getOrAddClass(闭型)`,否则原 Class 路径。
   (取代 PatchMemberClassInfoLookup 的 `cachedClassInfoForMember` 探测。)
2. **JsonClassInfo.java**(`PatchClosedElementClassInfo`):
   - 注入闭型构造器 `JsonClassInfo(java.lang.reflect.Type, JsonSerializerOptions)`:
     委托旧 Class 构造器后,`__cs2jClosedType` 记闭型、以最后实参擦除类修正
     `elementType`(字典=值类型,枚举集合=元素类型),并把闭型转换器
     (`getOptions().getConverter(闭型)`)盖到 `propertyInfoForClassInfo.ConverterBase` 上,
     使类信息伪属性也携带闭型(WriteStack.push 会取它的 getRuntimeClassInfo);
   - **伪属性自举**(实施期发现):`propertyInfoForClassInfo` 是 memberInfo=null 的伪属性,
     其 `getRuntimeClassInfo()` 擦除回退会命中的正是**正在构造中的这个类信息**(ThreadLocal
     残留时是错的,自举前是死循环风险)。闭型构造器直接
     `propertyInfoForClassInfo.__cs2jSetRuntimeClassInfo(this)` 恒等自举 —— 不经缓存查找
     (构造器内 `getOrAddClass(self)` 会因"先构造后入缓存"无限递归);
   - **泛型定义成员重绑定**(实施期发现):基构造器跑在无 ThreadLocal 预热的语境里,
     `CSharpKeyValuePair<K,V>` 的 Key/Value 属性从擦除 K/V 构建、退化成 Object(实测
     Key 反序列化成 JsonElement)。闭型构造器按 `getType().getTypeParameters()` 位置映射
     构造绑定表,对 `PropertyCacheArray` 逐成员
     `substituteTypeVariables(declaredGenericType(member), binding)`:恒等跳过;替换成功则
     同步改写成员 runtimePropertyType/declaredPropertyType/converterBase,并经
     `ParameterCache` 把闭型 converterBase 传播到已绑定的构造器参数
     (`JsonParameterInfo.initialize` 从 matchingProperty 拷贝 converterBase,不同步则
     参数化构造器路径仍旧);
   - `getElementClassInfo()` 擦除查找替换为:闭型最后实参为参数化 →
     `getOrAddClass(闭型实参)`,否则原 Class 路径。
   (取代 PatchElementArgumentScope 的 push/begin scope。)
3. **JsonSerializerOptions.java**(`PatchClosedOptionLookups`):
   - 注入 `getConverter(java.lang.reflect.Type)`:非参数化委托 Class 版;参数化以
     `closedCacheKey` 查/写 `_converters`,工厂经
     `factory.getConverterInternal(闭型, this)` 创建;
   - 注入 `getOrAddClass(java.lang.reflect.Type)`:非参数化委托 Class 版;参数化以
     `closedCacheKey` 查/写 `_classes`,创建走闭型构造器。
4. **JsonConverterFactory.java**(`PatchClosedFactoryEntry`):
   - 注入 `getConverterInternal(java.lang.reflect.Type, options)` →
     `TypeHelper.createConverterForClosedType(this, closedType, options)`。
5. **IEnumerableConverterFactory.java / ObjectConverterFactory.java**
   (`PatchClosedEnumerableFactory` / `PatchClosedObjectFactory`):
   - 原 `createConverter(Class, options)` 改名加层参
     `createConverter(Class, Type[] __cs2jLayers, options)`,体内
     `TypeHelper.getGenericArguments(X)` 全部改写为
     `TypeHelper.genericArgumentsInLayers(__cs2jLayers, X)`;
   - 追加保签名 shim `createConverter(Class, options)` → 层参传 null(旧 Class 路径不变)。
6. **JsonSerializerOptions.java**(`PatchClosedMemberConverterResolution`,实施期发现):
   - `determineConverter` 的默认成员解析与工厂解包都只看 `runtimePropertyType`(擦除)。
     改为成员级提升声明 `__cs2jMemberClosedType`(memberInfo 的声明闭型,非参数化回退
     runtimePropertyType),默认解析 `getConverter(闭型)`、工厂解包
     `getConverterInternal(闭型, this)`;成员上的 JsonConverterAttribute 优先级不变。
7. **集合快速路径空值守卫**(`PatchFastPathWriteNullGuard`,实施期发现,常开不门控):
   STJ 的 `JsonConverter<T>.Write`(密封包装)在分发到具体转换器前有
   `value == null && !HandleNullOnWrite → WriteNullValue()` 守卫;移植版把该包装溶进了抽象
   `write`,ListOfT/IList/Array/Dictionary 四个快速路径直接以 null 调用转换器。闭环链把
   字典 VALUE 转换器解析准了(DateTime 而非被擦除、碰巧容忍 null 的 Object 转换器)后,
   该潜伏缺陷显形(NullTests.defaultIgnoreNullValuesOnWrite 在 DateTimeConverter.write 内
   NPE)。注入的守卫逐字镜像 `JsonConverter1.tryWrite` 的空值处理。

### 3.3 端到端(2-D 例)

读:`{"L":[[10,11],[20,21]]}`
根类信息 → 属性 L(闭型 `CSharpList<CSharpList<Integer>>`)→ push:
`L.getRuntimeClassInfo()` = `getOrAddClass(闭型外层)`
(key `CSharpList[CSharpList<Integer>]`)→ 外层 ListOfTConverter(层实参:
元素擦除 = CSharpList)→ `getElementClassInfo()` = `getOrAddClass(CSharpList<Integer>)`
→ 内层 ListOfTConverter(元素 Integer)→ `getElementClassInfo()` = Integer → IntegerConverter。
全程无 ThreadLocal;`CSharpList<String>` 成员与嵌套成员缓存 key 互异。

写:WriteStack.push → `getPolymorphicJsonPropertyInfo().getRuntimeClassInfo()`
(闭型;伪属性经自举恒等返回本类信息,见 §3.2 条 2)→
`DeclaredJsonPropertyInfo = 类信息.propertyInfoForClassInfo`(闭型构造器已盖
闭型转换器)→ 元素帧经 `getElementClassInfo()`(闭型)。

## 4. 与既有 ThreadLocal 补丁的冲突与退役

`PatchWriteElementPropertyInfo` 用 `converter.getElementType()`(擦除)反查类信息,
会**覆盖**闭型 `getElementClassInfo()` 的正确结果 —— 新链路启用后两者不可共存,
首个退役对象即它。退役顺序(每退一个重新生成下游 + 全量回归):

1. `PatchWriteElementPropertyInfo`(冲突,必须先退);
2. `PatchElementArgumentScope`(`getElementClassInfo` 闭型化后,该补丁的注入职责由
   `PatchClosedElementClassInfo` 接管:**闭型改写会消费被锚定的擦除查找语句**,因此擦除回退
   分支上的 ThreadLocal 实参作用域(入口路径语义,见 §2.4)改由闭型补丁自己再发射 ——
   包括 `__cs2jOwnerTypeArguments` 字段与构造期捕获;退役的是"作用域补丁",不是作用域本身);
3. `PatchMemberClassInfoLookup`(`getRuntimeClassInfo` 闭型化后成死代码;注意它带
   不匹配响亮告警,退役须走开关静默跳过);
4. `PatchMemberArgumentCapture`(供 `cachedClassInfoForMember`/MEMBER_DECLARED_ARGUMENTS
   的簿记,3 退后成死代码;`registerMemberType` 本体保留 —— 构建期成员转换器
   (KeyValuePair/Nullable/非嵌套)仍按现状对齐,属入口/Class 路径语义)。

退役方式:仿 Round-18 `WriteElementPropertyInfoEnabled`,编译期常量开关,默认 false,
补丁方法保留并注明"由闭型链路(见 specs/2026-09-22-closed-type-metadata-chain-design.md)取代"。

## 5. 已知边界(记录,不阻塞)

- `NullableConverterFactory`、`EnumConverterFactory` 未接闭型分支:枚举/可空值类型
  无嵌套层,Class 路径语义完整;若未来出现嵌套场景再扩展。
- `KeyValuePair<K,V>` 作为**嵌套集合元素**(非直接成员)时,其类信息经闭型构造器
  (ObjectConverterFactory 已接闭型分支)创建,实参由层解析;直接成员场景由闭型构造器的
  **泛型定义成员重绑定**(§3.2 条 2)解析,不再依赖构建期 ThreadLocal 对齐。
- `_classes`/`_converters` 混存 Class key 与 ClosedGenericKey:仅参数化闭型走闭型 key,
  Class 公开 API 路径的 key 形态不变。

## 6. 验证闸

1. cs2j 仓库 `dotnet test` 全绿(新增 ClosedTypeMetadataPatchTests 测试先行);
2. compat `mvn test` 通过后 `mvn install` 至 D:\mvnrepo;
3. 下游基线 → 再生成 → `mvn -pl dotnet-text-json-tests test
   -Dtest='dotnet.text.json.Serialization.Tests.ArrayTests'
   -Dsurefire.failIfNoSpecifiedTests=false`:MyInt16TwoDimensionList 家族错误归零、
   readNull 8/8(含 TypeReference<CSharpList<SimpleTestClass>> 路径)+ 全模块无新失败;
   最小复现(仅嵌套 List / 混排)通过且与反射顺序无关;trace 无
   `[repeat of consumed layer]` 参与元素解析、无 `CSharpList[String]` 误解析。
