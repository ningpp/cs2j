using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.RegularExpressions;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the closed <c>java.lang.reflect.Type</c> metadata chain patches
/// (docs/superpowers/specs/2026-09-22-closed-type-metadata-chain-design.md): member and element
/// types must flow through the serializer metadata chain as closed types with no ThreadLocal
/// registry involved, replacing the PatchElementArgumentScope/PatchMemberArgumentCapture/
/// PatchMemberClassInfoLookup/PatchWriteElementPropertyInfo scope patches.
/// </summary>
public class ClosedTypeMetadataPatchTests
{
    private static ProjectPassState CreateState(ConversionResult result)
    {
        var compilation = CSharpCompilation.Create("TestAssembly");
        var library = Cs2jLibraryFactory.CreateFromCompilation(compilation);
        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
        var context = new ConversionContext(options, new CSharpToJava.TypeMapping.TypeMappingRegistry((string?)null));

        var state = new ProjectPassState
        {
            Library = library,
            Context = context,
            Compilation = compilation,
        };
        state.Results.Add(result);
        return state;
    }

    private static string Execute(string fileName, string code)
    {
        var result = new ConversionResult { FileName = fileName, GeneratedCode = code };
        var state = CreateState(result);
        new ProjectStructClonePatchPass().Execute(state);
        return result.GeneratedCode ?? string.Empty;
    }

    // ------------------------------------------------------------------ JsonPropertyInfo.java

    private const string JsonPropertyInfoSnippet = """
        package dotnet.text.json;

        import io.github.ningpp.compat.CompatNonPublicAccessor;
        import io.github.ningpp.compat.MemberInfo;

        public abstract class JsonPropertyInfo {
            public static JsonPropertyInfo createIgnoredPropertyPlaceholder(MemberInfo memberInfo, JsonSerializerOptions options) {
                JsonPropertyInfo jsonPropertyInfo = new dotnet.text.json.JsonPropertyInfo1<Byte>(io.github.ningpp.compat.CSharpSByte.class);
                jsonPropertyInfo.setOptions(options);
                jsonPropertyInfo.setMemberInfo(memberInfo);
                jsonPropertyInfo.determinePropertyName();
                jsonPropertyInfo.setIsIgnored(true);
                return jsonPropertyInfo;
            }
            public void initialize(Class parentClassType, Class declaredPropertyType, Class runtimePropertyType, int runtimeClassType, MemberInfo memberInfo, dotnet.text.json.Serialization.JsonConverter converter, dotnet.text.json.Serialization.JsonIgnoreCondition ignoreCondition, Integer parentTypeNumberHandling, JsonSerializerOptions options) {
                setParentClassType(parentClassType);
                setDeclaredPropertyType(declaredPropertyType);
                setRuntimePropertyType(runtimePropertyType);
                ClassType = runtimeClassType;
                setMemberInfo(memberInfo);
                setConverterBase(converter);
                setOptions(options);
            }
            public JsonClassInfo getRuntimeClassInfo() {
                if (_runtimeClassInfo == null) {
                _runtimeClassInfo = getOptions().getOrAddClass(getRuntimePropertyType());
                }
                return _runtimeClassInfo;
            }
            public Class getRuntimePropertyType() {
                return runtimePropertyType;
            }
        }
        """;

    [Fact]
    public void JsonPropertyInfo_DeclaredClosedTypeIsStampedAtInitialize()
    {
        var patched = Execute("JsonPropertyInfo.java", JsonPropertyInfoSnippet);

        Assert.Contains("__cs2jDeclaredGenericType = io.github.ningpp.compat.TypeHelper.declaredGenericType(memberInfo);", patched);
        Assert.Contains("private java.lang.reflect.Type __cs2jDeclaredGenericType;", patched);
        Assert.Contains("import io.github.ningpp.compat.TypeHelper;", patched);
        // The static factory's jsonPropertyInfo.setMemberInfo(memberInfo) call must NOT be
        // stamped: the instance-field assignment is illegal in a static context. The stamp
        // belongs inside the instance initialize method, whose bare call it follows.
        int staticFactoryStart = patched.IndexOf("createIgnoredPropertyPlaceholder", StringComparison.Ordinal);
        int initializeStart = patched.IndexOf("public void initialize", StringComparison.Ordinal);
        int stampIndex = patched.IndexOf("__cs2jDeclaredGenericType = io.github.ningpp.compat.TypeHelper.declaredGenericType", StringComparison.Ordinal);
        Assert.True(initializeStart >= 0 && staticFactoryStart >= 0 && staticFactoryStart < initializeStart,
            "the snippet must contain both shapes, factory first, mirroring the generated file");
        Assert.True(stampIndex > initializeStart,
            "the stamp must be inside the instance initialize, not in the static factory");
    }

    [Fact]
    public void JsonPropertyInfo_RuntimeClassInfoResolvesThroughTheClosedType()
    {
        var patched = Execute("JsonPropertyInfo.java", JsonPropertyInfoSnippet);

        Assert.Contains("getOptions().getOrAddClass(__cs2jClosedType)", patched);
        // The erased lookup survives only as the non-parameterized fallback, renamed so the
        // retired ThreadLocal scope patches can no longer match it.
        Assert.Contains("Class __cs2jErasedRuntimeType = getRuntimePropertyType();", patched);
    }

    [Fact]
    public void JsonPropertyInfo_PatchIsIdempotent()
    {
        var once = Execute("JsonPropertyInfo.java", JsonPropertyInfoSnippet);
        var twice = Execute("JsonPropertyInfo.java", once);

        Assert.Equal(once, twice);
    }

    // ------------------------------------------------------------------ JsonParameterInfo.java

    private const string JsonParameterInfoSnippet = """
        package dotnet.text.json;

        import io.github.ningpp.compat.CompatNonPublicAccessor;
        import io.github.ningpp.compat.CompatNonPublicProperty;
        import io.github.ningpp.compat.PropertyInfo;

        public abstract class JsonParameterInfo {
            @CompatNonPublicProperty private JsonClassInfo _runtimeClassInfo;
            public void initialize(Class runtimePropertyType, int runtimeClassType, Integer parentTypeNumberHandling, PropertyInfo propertyInfo, JsonPropertyInfo matchingProperty, JsonSerializerOptions options) {
                setRuntimePropertyType(runtimePropertyType);
                ClassType = runtimeClassType;
                setMatchingProperty(matchingProperty);
                setPosition(ReflectionHelper.getPosition(parameterInfo));
                setOptions(options);
            }
            public static JsonParameterInfo createIgnoredParameterPlaceholder(JsonSerializerOptions options) {
                JsonParameterInfo _obj2 = new dotnet.text.json.JsonParameterInfo1(io.github.ningpp.compat.CSharpSByte.class);
                _obj2.setRuntimePropertyType(CSharpSByte.class);
                _obj2.setPosition(ReflectionDecoder.defaultPosition());
                return _obj2;
            }
            public JsonClassInfo getRuntimeClassInfo() {
                if (_runtimeClassInfo == null) {
                _runtimeClassInfo = getOptions().getOrAddClass(getRuntimePropertyType());
                }
                return _runtimeClassInfo;
            }
            @CompatNonPublicProperty
            @CompatNonPublicAccessor
            public Class getRuntimePropertyType() {
                return runtimePropertyType;
            }
        }
        """;

    [Fact]
    public void JsonParameterInfo_DeclaredClosedTypeIsStampedAtInitialize()
    {
        var patched = Execute("JsonParameterInfo.java", JsonParameterInfoSnippet);

        Assert.Contains("__cs2jDeclaredGenericType = (matchingProperty != null ? matchingProperty.getDeclaredGenericType() : null);", patched);
        Assert.Contains("private java.lang.reflect.Type __cs2jDeclaredGenericType;", patched);
        Assert.Contains("import io.github.ningpp.compat.TypeHelper;", patched);
        // The static placeholder's _obj2.setRuntimePropertyType(CSharpSByte.class) must not be
        // stamped; only the bare initialize call is (initialize comes first, mirroring the file).
        int placeholderStart = patched.IndexOf("createIgnoredParameterPlaceholder", StringComparison.Ordinal);
        int initializeStart = patched.IndexOf("public void initialize", StringComparison.Ordinal);
        int stampIndex = patched.IndexOf("__cs2jDeclaredGenericType = (matchingProperty != null", StringComparison.Ordinal);
        Assert.True(initializeStart >= 0 && placeholderStart >= 0 && initializeStart < placeholderStart,
            "the snippet must contain both shapes, initialize first");
        Assert.True(stampIndex > initializeStart && stampIndex < placeholderStart,
            "the stamp must be inside the instance initialize, not the static placeholder");
    }

    [Fact]
    public void JsonParameterInfo_AccessorsLandBeforeTheAccessorAnnotations()
    {
        var patched = Execute("JsonParameterInfo.java", JsonParameterInfoSnippet);

        // The generated accessor is decorated with @CompatNonPublicProperty/@CompatNonPublicAccessor.
        // The injected accessors must go ABOVE that annotation block; landing between the
        // annotations and the method retargets the annotations onto the injected field, which
        // does not compile (annotation target mismatch). After the raw string's dedent the
        // accessor sits at 4-space indentation.
        Assert.Contains(
            "@CompatNonPublicProperty\n    @CompatNonPublicAccessor\n    public Class getRuntimePropertyType() {",
            patched.Replace("\r\n", "\n"));
        int fieldIndex = patched.IndexOf("private java.lang.reflect.Type __cs2jDeclaredGenericType;", StringComparison.Ordinal);
        int annotationIndex = patched.IndexOf("@CompatNonPublicProperty\n    @CompatNonPublicAccessor", StringComparison.Ordinal);
        Assert.True(fieldIndex >= 0 && annotationIndex > fieldIndex,
            "the injected field must precede the accessor's annotation block");
        Assert.Contains("Class __cs2jErasedRuntimeType = getRuntimePropertyType();", patched);
    }

    [Fact]
    public void JsonParameterInfo_RuntimeClassInfoResolvesThroughTheClosedType()
    {
        var patched = Execute("JsonParameterInfo.java", JsonParameterInfoSnippet);

        Assert.Contains("getOptions().getOrAddClass(__cs2jClosedType)", patched);
    }

    [Fact]
    public void JsonParameterInfo_PatchIsIdempotent()
    {
        var once = Execute("JsonParameterInfo.java", JsonParameterInfoSnippet);
        var twice = Execute("JsonParameterInfo.java", once);

        Assert.Equal(once, twice);
    }

    // ------------------------------------------------------------------ JsonClassInfo.java

    private const string JsonClassInfoSnippet = """
        package dotnet.text.json;

        import io.github.ningpp.compat.CompatNonPublicAccessor;

        public class JsonClassInfo {
            public JsonClassInfo(Class type, JsonSerializerOptions options) {
                setType(type);
                setOptions(options);
                dotnet.text.json.Serialization.JsonConverter converter = getConverter(getType(), null, null, null, getOptions());
                setPropertyInfoForClassInfo(createPropertyInfoForClassInfo(getType(), null, converter, getOptions()));
            }
            public JsonClassInfo getElementClassInfo() {
                if (_elementClassInfo == null && getElementType() != null) {
                _elementClassInfo = getOptions().getOrAddClass(getElementType());
                }
                return _elementClassInfo;
            }
            public Class getElementType() {
                return elementType;
            }
        }
        """;

    [Fact]
    public void JsonClassInfo_ClosedConstructorIsInjected()
    {
        var patched = Execute("JsonClassInfo.java", JsonClassInfoSnippet);

        Assert.Contains("public JsonClassInfo(java.lang.reflect.Type __cs2jClosedType, JsonSerializerOptions options)", patched);
        Assert.Contains("io.github.ningpp.compat.TypeHelper.toRawClass(__cs2jClosedType)", patched);
        // The class-info property must carry the closed converter so write-stack pushes that
        // read its getRuntimeClassInfo() stay on the closed chain.
        Assert.Contains("__cs2jWithClosedConverter", patched);
        // The write stack resolves nested element frames through the class-info property's
        // getRuntimeClassInfo(); priming it with this class info itself keeps that hop closed
        // instead of falling back to the erased class info (whose ThreadLocal resolution
        // bakes a wrong element converter into nested writes).
        Assert.Contains("getPropertyInfoForClassInfo().__cs2jSetRuntimeClassInfo(this);", patched);
    }

    [Fact]
    public void JsonClassInfo_ElementClassInfoResolvesThroughTheClosedElement()
    {
        var patched = Execute("JsonClassInfo.java", JsonClassInfoSnippet);

        Assert.Contains("java.lang.reflect.Type __cs2jClosedElement = __cs2jClosedElementType();", patched);
        Assert.Contains("getOptions().getOrAddClass(__cs2jClosedElement)", patched);
        // The erased lookup survives only as the fallback, renamed so the retired ThreadLocal
        // scope patches can no longer match it.
        Assert.Contains("Class __cs2jErasedElement = getElementType();", patched);
        Assert.DoesNotContain("_elementClassInfo = getOptions().getOrAddClass(getElementType());", patched);
    }

    [Fact]
    public void JsonClassInfo_EntryPathElementScopeSurvivesTheClosedRewrite()
    {
        var patched = Execute("JsonClassInfo.java", JsonClassInfoSnippet);

        // The Class/TypeReference ENTRY path builds class infos through the legacy constructor,
        // so its element lookups take the erased fallback and still rely on the ThreadLocal
        // argument scope (entry semantics, spec §2.4). The closed rewrite consumes the anchor
        // statement, so it must re-emit the scope itself — otherwise the scope silently
        // disappears from the generated file and nested same-raw entries mis-resolve
        // (e.g. readGenericIReadOnlyCollectionOfGenericIReadOnlyCollection).
        Assert.Contains(
            "TypeHelper.beginElementArgumentsScope(getType(), __cs2jErasedElement, TypeHelper.elementArgumentsOfOwner(__cs2jOwnerTypeArguments))",
            patched);
        Assert.Contains("TypeHelper.pushArgumentContext(getType(), __cs2jErasedElement, __cs2jOwnerTypeArguments)", patched);

        // The closed branch runs inside the scope-protected block: after the try, before the finally.
        int tryIndex = patched.IndexOf("try {", StringComparison.Ordinal);
        int closedLookupIndex = patched.IndexOf("getOptions().getOrAddClass(__cs2jClosedElement)", StringComparison.Ordinal);
        int finallyIndex = patched.IndexOf("finally {", StringComparison.Ordinal);
        Assert.True(tryIndex >= 0 && finallyIndex > tryIndex && closedLookupIndex > tryIndex && closedLookupIndex < finallyIndex,
            "the closed element lookup must be inside the scope-protected try block");

        // The closed patch owns the capture and the field: exactly one of each, no duplicate
        // injection from the (guarded) scope patch.
        Assert.Equal(1, Regex.Matches(patched, "captureDeclaredTypeArguments").Count);
        Assert.Equal(1, Regex.Matches(patched, "private java.lang.reflect.Type\\[\\] __cs2jOwnerTypeArguments").Count);
        Assert.Contains("__cs2jOwnerTypeArguments = TypeHelper.captureDeclaredTypeArguments(type);", patched);
    }

    [Fact]
    public void JsonClassInfo_PatchIsIdempotent()
    {
        var once = Execute("JsonClassInfo.java", JsonClassInfoSnippet);
        var twice = Execute("JsonClassInfo.java", once);

        Assert.Equal(once, twice);
    }

    // ---------------------------------------------- fast-path write null guard (port bug fix)

    // STJ's JsonConverter<T>.Write (a sealed wrapper) guards null values with writeNullValue
    // before dispatching to the converter; the port dissolved that wrapper into the abstract
    // write, so the collection fast paths call converters with null and crash on primitive
    // writers (NullTests.defaultIgnoreNullValuesOnWrite: CSharpDictionary<String,
    // CSharpDateTime> with a null value NPEs inside DateTimeConverter.write). The closed-type
    // chain resolves dictionary VALUE converters precisely (DateTime instead of the erased
    // null-tolerant Object converter), which surfaced the latent bug.
    private const string FastPathEnumerableConverterSnippet = """
        package dotnet.text.json.Serialization.Converters;

        public class ListOfTConverter {
            public boolean onWriteResume(dotnet.text.json.Utf8JsonWriter writer, Object value, dotnet.text.json.JsonSerializerOptions options, io.github.ningpp.compat.ObjectHolder<WriteStack> state) {
                JsonConverter1<TElement> elementConverter = getElementConverter(state);
                if (elementConverter.getCanUseDirectReadOrWrite() && state.value.Current.NumberHandling == null) {
                elementConverter.write(writer, list.get(index), options);
                }
                return true;
            }
        }
        """;

    [Fact]
    public void FastPathWriteNullGuard_IsInjectedOnEnumerableFastPath()
    {
        var patched = Execute("ListOfTConverter.java", FastPathEnumerableConverterSnippet);

        Assert.Contains("if (list.get(index) == null && !elementConverter.getHandleNullOnWrite()) {", patched);
        Assert.Contains("writer.writeNullValue();", patched);
        // The original call survives as the else branch.
        Assert.Contains("elementConverter.write(writer, list.get(index), options);", patched);
    }

    [Fact]
    public void FastPathWriteNullGuard_IsInjectedOnDictionaryFastPath()
    {
        var patched = Execute("DictionaryOfTKeyTValueConverter.java", FastPathEnumerableConverterSnippet
            .Replace("ListOfTConverter", "DictionaryOfTKeyTValueConverter")
            .Replace("list.get(index)", "enumerator.getCurrent().getValue()")
            .Replace("elementConverter", "valueConverter"));

        Assert.Contains("if (enumerator.getCurrent().getValue() == null && !valueConverter.getHandleNullOnWrite()) {", patched);
        Assert.Contains("valueConverter.write(writer, enumerator.getCurrent().getValue(), options);", patched);
    }

    [Fact]
    public void FastPathWriteNullGuard_PatchIsIdempotent()
    {
        var once = Execute("ListOfTConverter.java", FastPathEnumerableConverterSnippet);
        var twice = Execute("ListOfTConverter.java", once);

        Assert.Equal(once, twice);
    }

    // ---------------------------------------------- deserialize entry registry reset

    // The serialize(Class, TValue, JsonSerializerOptions) entry already resets the per-thread
    // type-argument registry (PatchSerializeRegistryReset). The deserialize entries never did:
    // an earlier operation's pending layers poisoned later shared-options resolutions —
    // ArrayTests.readObjectArray left SimpleTestClass's warm-up chains pending and
    // DictionaryTests.hashtable / iDictionary_Keys then failed only in full-suite order
    // (String instead of the erased JsonElement the erased converter produces). Only the
    // shared-default path resets: the TypeReference flow publishes its session registration
    // BEFORE invoking the entry with an explicit (fresh) options instance and must not be wiped.
    private const string JsonSerializerDeserializeSnippet = """
        package dotnet.text.json;

        public class JsonSerializer {
            public static <TValue> TValue deserialize(ReadOnlySpan<Integer> utf8Json, JsonSerializerOptions options, Class<?> tValueClass) {
                if (options == null) {
                options = JsonSerializerOptions.s_defaultOptions;
                }
                return deserialize_0tp(utf8Json, tValueClass, options);
            }
            public static <TValue> TValue deserialize(String json, JsonSerializerOptions options, Class<?> tValueClass) {
                if (json == null) {
                throw new ArgumentNullException("json");
                }
                return deserialize(json, TypeHelper.toWrapperType(tValueClass), options, tValueClass);
            }
            public static <TValue> TValue deserialize(io.github.ningpp.compat.ObjectHolder<Utf8JsonReader> reader, JsonSerializerOptions options, Class<?> tValueClass) {
                return deserialize_0tp(reader, tValueClass, options);
            }
        }
        """;

    [Theory]
    [InlineData("ReadOnlySpan<Integer> utf8Json")]
    [InlineData("String json")]
    [InlineData("io.github.ningpp.compat.ObjectHolder<Utf8JsonReader> reader")]
    public void DeserializeEntries_ResetTheRegistryForSharedDefaultOptionsOnly(string firstParameter)
    {
        var patched = Execute("JsonSerializer.java", JsonSerializerDeserializeSnippet);

        string expected = "deserialize(" + firstParameter + ", JsonSerializerOptions options, Class<?> tValueClass) {\n"
            + "        if (options == null) {\n"
            + "        // __cs2j: reset the shared-options registry\n"
            + "        TypeHelper.clearRegistrations();\n"
            + "        }\n";
        Assert.Contains(expected, patched.Replace("\r\n", "\n"));
        // Explicit options (the TypeReference flow's fresh instance) must not be reset.
        Assert.Equal(3, Regex.Matches(patched, "TypeHelper\\.clearRegistrations\\(\\)").Count);
    }

    [Fact]
    public void DeserializeEntries_PatchIsIdempotent()
    {
        var once = Execute("JsonSerializer.java", JsonSerializerDeserializeSnippet);
        var twice = Execute("JsonSerializer.java", once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void JsonClassInfo_RebindsGenericDefinitionMembersToTheClosure()
    {
        var patched = Execute("JsonClassInfo.java", JsonClassInfoSnippet);

        // Members declared with the raw class's type parameters (Key/Value of
        // CSharpKeyValuePair<K,V>) are resolved by the base constructor through erased
        // reflection; the closed constructor must rebind them to the closure's arguments and
        // re-resolve their converters, then propagate to bound constructor parameters.
        Assert.Contains("java.lang.reflect.TypeVariable<?>[] __cs2jTypeParams = getType().getTypeParameters();", patched);
        Assert.Contains("io.github.ningpp.compat.TypeHelper.substituteTypeVariables(__cs2jDeclaredType, __cs2jBinding)", patched);
        Assert.Contains("__cs2jMember.setConverterBase(getOptions().getConverter(__cs2jClosedMemberType));", patched);
        Assert.Contains("io.github.ningpp.compat.TypeHelper.consumeMemberTypeArguments(__cs2jClosedMemberType);", patched);
        Assert.Contains("__cs2jBoundParam.setRuntimePropertyType(__cs2jClosedMemberClass);", patched);
        // Concrete members (no type parameter in the declaration) must stay untouched.
        Assert.Contains("__cs2jClosedMemberType == __cs2jDeclaredType", patched);
    }

    // ------------------------------------------------------------------ JsonSerializerOptions.java

    private const string JsonSerializerOptionsSnippet = """
        package dotnet.text.json;

        public class JsonSerializerOptions {
            private ConcurrentHashMap<Class, dotnet.text.json.Serialization.JsonConverter> _converters = new ConcurrentHashMap<Class, dotnet.text.json.Serialization.JsonConverter>();
            private ConcurrentHashMap<Class, JsonClassInfo> _classes = new ConcurrentHashMap<Class, JsonClassInfo>();
            public dotnet.text.json.Serialization.JsonConverter getConverter(Class typeToConvert) {
                return null;
            }
            public JsonClassInfo getOrAddClass(Class type) {
                return null;
            }
            public dotnet.text.json.Serialization.JsonConverter determineConverter(Class parentClassType, Class runtimePropertyType, io.github.ningpp.compat.MemberInfo memberInfo) {
                dotnet.text.json.Serialization.JsonConverter converter = null;
                // Priority 1: attempt to get converter from JsonConverterAttribute on property.
                if (memberInfo != null) {
                dotnet.text.json.Serialization.JsonConverterAttribute converterAttribute = (dotnet.text.json.Serialization.JsonConverterAttribute)(getAttributeThatCanHaveMultiple(parentClassType, dotnet.text.json.Serialization.JsonConverterAttribute.class, memberInfo));
                if (converterAttribute != null) {
                converter = getConverterFromAttribute(converterAttribute, runtimePropertyType, parentClassType, memberInfo);
                }
                }
                if (converter == null) {
                converter = getConverter(runtimePropertyType);
                }
                if (converter instanceof dotnet.text.json.Serialization.JsonConverterFactory factory) {
                converter = factory.getConverterInternal(runtimePropertyType, this);
                }
                return converter;
            }
        }
        """;

    [Fact]
    public void JsonSerializerOptions_ClosedConverterLookupIsInjected()
    {
        var patched = Execute("JsonSerializerOptions.java", JsonSerializerOptionsSnippet);

        Assert.Contains("public dotnet.text.json.Serialization.JsonConverter getConverter(java.lang.reflect.Type __cs2jClosedType)", patched);
        Assert.Contains("io.github.ningpp.compat.TypeHelper.closedCacheKey(__cs2jClosedType)", patched);
        Assert.Contains("getConverterInternal(__cs2jClosedType, this)", patched);
        // The injection must not swallow the anchor's own signature.
        Assert.Contains("public dotnet.text.json.Serialization.JsonConverter getConverter(Class typeToConvert)", patched);
    }

    [Fact]
    public void JsonSerializerOptions_ClosedClassInfoLookupIsInjected()
    {
        var patched = Execute("JsonSerializerOptions.java", JsonSerializerOptionsSnippet);

        Assert.Contains("public JsonClassInfo getOrAddClass(java.lang.reflect.Type __cs2jClosedType)", patched);
        Assert.Contains("new JsonClassInfo(__cs2jClosedType, this)", patched);
        // The injection must not swallow the anchor's own signature.
        Assert.Contains("public JsonClassInfo getOrAddClass(Class type)", patched);
    }

    [Fact]
    public void JsonSerializerOptions_DetermineConverterResolvesThroughTheMemberClosedType()
    {
        var patched = Execute("JsonSerializerOptions.java", JsonSerializerOptionsSnippet);

        // The default member converter must resolve through the member's declared closed type
        // (the write stack bakes DeclaredJsonPropertyInfo's converter into element writes, so
        // an erased resolution here poisons every nested write).
        Assert.Contains(
            "java.lang.reflect.Type __cs2jMemberClosedType = (memberInfo != null ? io.github.ningpp.compat.TypeHelper.declaredGenericType(memberInfo) : null);",
            patched);
        Assert.Contains("converter = getConverter(__cs2jMemberClosedType);", patched);
        Assert.Contains("converter = factory.getConverterInternal(__cs2jMemberClosedType, this);", patched);
        // Serving the member from the closure must retire its pending warm-up registration,
        // otherwise the chain leaks into unrelated later Class-path lookups.
        Assert.Contains("io.github.ningpp.compat.TypeHelper.consumeMemberTypeArguments(__cs2jMemberClosedType);", patched);
        Assert.Equal(2, Regex.Matches(patched, "consumeMemberTypeArguments\\(__cs2jMemberClosedType\\)").Count);
        // Attribute converters keep priority and the original erased resolution only survives
        // inside the closed-type fallback.
        Assert.Contains("getConverterFromAttribute(converterAttribute, runtimePropertyType, parentClassType, memberInfo)", patched);
        Assert.Contains("__cs2jMemberClosedType = runtimePropertyType;", patched);
    }

    // ------------------------------------------------------------------ JsonConverterFactory.java

    private const string JsonConverterFactorySnippet = """
        package dotnet.text.json.Serialization;

        public abstract class JsonConverterFactory {
            public JsonConverter getConverterInternal(Class typeToConvert, dotnet.text.json.JsonSerializerOptions options) {
                JsonConverter converter = createConverter(typeToConvert, options);
                if (converter == null) {
                ThrowHelper.throwInvalidOperationException_SerializerConverterFactoryReturnsNull(getClass());
                }
                return converter;
            }
            public final Object readCoreAsObject(io.github.ningpp.compat.ObjectHolder<dotnet.text.json.Utf8JsonReader> reader, dotnet.text.json.JsonSerializerOptions options, io.github.ningpp.compat.ObjectHolder<ReadStack> state) {
                return null;
            }
        }
        """;

    [Fact]
    public void JsonConverterFactory_ClosedEntryIsInjected()
    {
        var patched = Execute("JsonConverterFactory.java", JsonConverterFactorySnippet);

        Assert.Contains("public dotnet.text.json.Serialization.JsonConverter getConverterInternal(java.lang.reflect.Type __cs2jClosedType, dotnet.text.json.JsonSerializerOptions options)", patched);
        Assert.Contains("io.github.ningpp.compat.TypeHelper.createConverterForClosedType(this, __cs2jClosedType, options)", patched);
        // The injection must not swallow the anchor's own signature.
        Assert.Contains("public final Object readCoreAsObject(", patched);
    }

    // ------------------------------------------------------------------ IEnumerableConverterFactory.java

    private const string IEnumerableConverterFactorySnippet = """
        package dotnet.text.json.Serialization.Converters;

        import io.github.ningpp.compat.TypeHelper;

        public class IEnumerableConverterFactory extends dotnet.text.json.Serialization.JsonConverterFactory {
            public dotnet.text.json.Serialization.JsonConverter createConverter(Class typeToConvert, dotnet.text.json.JsonSerializerOptions options) {
                Class elementType = null;
                Class actualTypeToConvert;
                if (TypeHelper.getIsArray(typeToConvert)) {
                elementType = TypeHelper.getElementType(typeToConvert);
                } else {
                if ((actualTypeToConvert = IEnumerableConverterFactoryHelpers.getCompatibleGenericBaseClass(typeToConvert, CSharpList.class)) != null) {
                elementType = TypeHelper.getGenericArguments(actualTypeToConvert)[0];
                }
                }
                return null;
            }
        }
        """;

    [Fact]
    public void IEnumerableConverterFactory_ClosedLayersRewired()
    {
        var patched = Execute("IEnumerableConverterFactory.java", IEnumerableConverterFactorySnippet);

        Assert.Contains("public dotnet.text.json.Serialization.JsonConverter createConverter(Class typeToConvert, java.util.Map<Class<?>, java.lang.reflect.Type[]> __cs2jLayers, dotnet.text.json.JsonSerializerOptions options)", patched);
        Assert.Contains("TypeHelper.genericArgumentsInLayers(__cs2jLayers, actualTypeToConvert)", patched);
        // The legacy entry point keeps its signature for the Class/TypeReference path.
        Assert.Contains("return createConverter(typeToConvert, null, options);", patched);
        Assert.DoesNotContain("TypeHelper.getGenericArguments(", patched);
    }

    // ------------------------------------------------------------------ ObjectConverterFactory.java

    private const string ObjectConverterFactorySnippet = """
        package dotnet.text.json.Serialization.Converters;

        import io.github.ningpp.compat.TypeHelper;

        public class ObjectConverterFactory extends dotnet.text.json.Serialization.JsonConverterFactory {
            public dotnet.text.json.Serialization.JsonConverter createConverter(Class typeToConvert, dotnet.text.json.JsonSerializerOptions options) {
                if (isKeyValuePair(typeToConvert)) {
                return createKeyValuePairConverter(typeToConvert, options);
                }
                return null;
            }
            private dotnet.text.json.Serialization.JsonConverter createKeyValuePairConverter(Class type, dotnet.text.json.JsonSerializerOptions options) {
                Class<?>[] __cs2jKeyValueArgs = TypeHelper.getGenericArguments(type);
                return null;
            }
        }
        """;

    [Fact]
    public void ObjectConverterFactory_ClosedLayersRewired()
    {
        var patched = Execute("ObjectConverterFactory.java", ObjectConverterFactorySnippet);

        Assert.Contains("public dotnet.text.json.Serialization.JsonConverter createConverter(Class typeToConvert, java.util.Map<Class<?>, java.lang.reflect.Type[]> __cs2jLayers, dotnet.text.json.JsonSerializerOptions options)", patched);
        Assert.Contains("TypeHelper.genericArgumentsInLayers(__cs2jLayers, type)", patched);
        Assert.Contains("return createConverter(typeToConvert, null, options);", patched);
        Assert.DoesNotContain("TypeHelper.getGenericArguments(", patched);
    }

    // ------------------------------------------------------------------ negative

    [Fact]
    public void UnrelatedFiles_AreUntouched()
    {
        var code = "public class SimpleTestClass {\n    public int getMyInt16() { return myInt16; }\n}";
        Assert.Equal(code, Execute("SimpleTestClass.java", code));
        Assert.Equal(code, Execute("List.java", code));
    }
}
