package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.lang.reflect.Constructor;
import java.lang.reflect.Modifier;
import java.util.concurrent.ConcurrentHashMap;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Dictionary key-type / key-conversion failures in the generated System.Text.Json
 * port, reproduced at the compat level. Each test mirrors the exact TypeHelper
 * query sequence a failing downstream test performs.
 *
 * <ul>
 *   <li>{@code DictionaryTests.dictionaryOfTOnlyWithStringPoco} — the converter
 *       factory asks the ERASED {@code CSharpGenericIDictionary} for its arguments
 *       while the only visible concrete type declares them one level deeper
 *       ({@code class ClosedDict extends OpenDict<Poco>} where
 *       {@code OpenDict<T> implements CSharpGenericIDictionary<String, T>}). The
 *       shallow walk answered with the intermediate class's OWN arguments
 *       ({@code [Poco]}, length 1) and the factory died with
 *       {@code genericArgs[1]} → ArrayIndexOutOfBoundsException.</li>
 *   <li>{@code CustomConverterTests.enumFail} / {@code intStringKeyValuePairConverter}
 *       — the generated converter calls {@code options.getConverter(CSharpKeyValuePair.class)}
 *       with the raw class literal; with the TypeReference session's dictionary layer
 *       already consumed, the query fell to the type-parameter bounds and built a
 *       KeyValuePair converter for (Object, Object). The dictionary then stored
 *       JsonElement keys and every {@code getChecked(...)} threw KeyNotFoundException.</li>
 *   <li>{@code DictionaryKeyPolicyTests.nullNamePolicy} /
 *       {@code CustomConverterTests.guidToEntityConverter} — the same options
 *       instance first serializes via the raw class (converter built from erased
 *       bounds and cached under the raw class), then deserializes via a TypeReference
 *       session; the generated cache hit returned the erased (Object-keyed)
 *       converter, so the session registration was never consulted.</li>
 *   <li>{@code DictionaryTests.implementsDictionary_DictionaryOfString} — .NET's
 *       IImmutableDictionary extends IReadOnlyDictionary, but the compat interface
 *       does not, so a wrapper implementing only IImmutableDictionary missed the
 *       IReadOnlyDictionary converter branch and fell to the IEnumerable converter
 *       (JsonException instead of the expected UnsupportedOperationException).</li>
 * </ul>
 *
 * <p>{@code DictionaryTests.dictionaryWith_ObjectWithNoParameterlessCtor_AsValue_Throws}
 * is covered here as a documented limitation only: javac gives the implicit default
 * constructor of a private class the same runtime shape as an explicit private
 * constructor, so compat cannot bind one and reject the other.</p>
 */
class DictionaryKeyTypeResolutionTest {

    enum MyEnum { One }

    // ------------------------------------------------------------------
    // FIX: deep subtype argument resolution (dictionaryOfTOnlyWithStringPoco)
    // ------------------------------------------------------------------

    /**
     * Mirrors DictionaryThatOnlyImplementsIDictionaryOfStringTValue&lt;TValue&gt;
     * exactly, including the second interface the transpiler emits.
     */
    static abstract class OpenDict<TValue>
            implements CSharpGenericIDictionary<String, TValue>, Iterable<CSharpKeyValuePair<String, TValue>> {
        final ConcurrentHashMap<String, TValue> inner = new ConcurrentHashMap<>();
        @Override public void add(String key, TValue value) { inner.put(key, value); }
        @Override public TValue put(String key, TValue value) { return inner.put(key, value); }
        @Override public TValue get(String key) { return inner.get(key); }
        @Override public boolean getIsReadOnly() { return false; }
        @Override public int getCount() { return inner.size(); }
        @Override public CSharpGenericEnumerator<CSharpKeyValuePair<String, TValue>> iterator() {
            java.util.List<CSharpKeyValuePair<String, TValue>> entries = new java.util.ArrayList<>();
            inner.forEach((k, v) -> entries.add(new CSharpKeyValuePair<>(k, v)));
            return CSharpGenericEnumerator.from(entries.iterator());
        }
    }

    /** Mirrors DictionaryThatOnlyImplementsIDictionaryOfStringPoco. */
    static final class ClosedDict extends OpenDict<Integer> {
    }

    static final class Poco {
    }

    @Test
    void erasedDictionaryInterfaceResolvesArgumentsDeclaredByIntermediateSupertype() {
        // The exact question sequence IEnumerableConverterFactory performs on the
        // raw-class path: canConvert auto-registers the concrete type, then the
        // CSharpGenericIDictionary branch asks genericArgumentsInLayers(null, iface).
        assertTrue(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, ClosedDict.class));
        try {
            assertArrayEquals(new Class<?>[] { String.class, Integer.class },
                TypeHelper.getGenericArguments(CSharpGenericIDictionary.class));
            // The call that raised ArrayIndexOutOfBoundsException at index [1]:
            // the old walk answered [Integer] (length 1) here.
            assertArrayEquals(new Class<?>[] { String.class, Integer.class },
                TypeHelper.genericArgumentsInLayers(null, CSharpGenericIDictionary.class));
        } finally {
            TypeHelper.clearRegistrations();
        }
    }

    // ------------------------------------------------------------------
    // FIX: session-context arity fallback for raw KeyValuePair (enumFail,
    // intStringKeyValuePairConverter)
    // ------------------------------------------------------------------

    @Test
    void rawKeyValuePairResolvesFromPendingSessionLayerOfMatchingArity() {
        TypeHelper.beginTypeRegistration();
        try {
            TypeHelper.registerParameterizedType(
                TypeHelper.parameterized(CSharpDictionary.class, MyEnum.class, Integer.class));
            // The generated factory consumed the dictionary layer for the dictionary
            // converter; the converter's constructor then asks for the raw pair type.
            TypeHelper.getGenericArguments(CSharpDictionary.class);
            assertArrayEquals(new Class<?>[] { MyEnum.class, Integer.class },
                TypeHelper.getGenericArguments(CSharpKeyValuePair.class));
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    @Test
    void rawKeyValuePairResolvesFromConsumedSessionLayerOfMatchingArity() {
        TypeHelper.beginTypeRegistration();
        try {
            TypeHelper.registerParameterizedType(
                TypeHelper.parameterized(CSharpDictionary.class, Integer.class, String.class));
            TypeHelper.getGenericArguments(CSharpDictionary.class);
            TypeHelper.getGenericArguments(CSharpDictionary.class); // repeat query: consumed layer
            assertArrayEquals(new Class<?>[] { Integer.class, String.class },
                TypeHelper.getGenericArguments(CSharpKeyValuePair.class));
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    /** Arity mismatch must NOT leak an unrelated session layer (List<String> ≠ Pair<K,V>). */
    @Test
    void sessionFallbackRequiresMatchingArity() {
        TypeHelper.beginTypeRegistration();
        try {
            TypeHelper.registerParameterizedType(
                TypeHelper.parameterized(CSharpList.class, String.class));
            TypeHelper.getGenericArguments(CSharpList.class);
            Class<?>[] pairArgs = TypeHelper.getGenericArguments(CSharpKeyValuePair.class);
            // No 2-arg layer in the session: the erased bounds answer (Object) stands.
            assertArrayEquals(new Class<?>[] { Object.class, Object.class }, pairArgs);
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    // ------------------------------------------------------------------
    // FIX: session start drops erased cache entries (nullNamePolicy,
    // guidToEntityConverter)
    // ------------------------------------------------------------------

    /** Mirrors the generated JsonSerializerOptions converter/type-info cache fields. */
    static final class FakeOptions {
        private final ConcurrentHashMap<Class, Object> _converters = new ConcurrentHashMap<>();
        private final ConcurrentHashMap<Class, Object> _classes = new ConcurrentHashMap<>();
    }

    @Test
    void sessionRegistrationDropsErasedCacheEntriesForRegisteredRawTypes() {
        FakeOptions options = new FakeOptions();
        Object erasedConverter = new Object();
        Object erasedClassInfo = new Object();
        options._converters.put(CSharpDictionary.class, erasedConverter);
        options._classes.put(CSharpDictionary.class, erasedClassInfo);
        // An entry for an unrelated type must survive the drop.
        Object other = new Object();
        options._converters.put(CSharpList.class, other);

        TypeHelper.beginTypeRegistration();
        try {
            TypeHelper.registerParameterizedType(
                TypeHelper.parameterized(CSharpDictionary.class, String.class, Integer.class));
            TypeHelper.dropSessionTypesFromCache(options);
            assertNull(options._converters.get(CSharpDictionary.class));
            assertNull(options._classes.get(CSharpDictionary.class));
            assertNotNull(options._converters.get(CSharpList.class));
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    @Test
    void dropSessionTypesFromCacheIgnoresObjectsWithoutCacheFields() {
        TypeHelper.beginTypeRegistration();
        try {
            TypeHelper.registerParameterizedType(
                TypeHelper.parameterized(CSharpDictionary.class, String.class, Integer.class));
            // No exception: an object without _converters/_classes fields is not a cache.
            TypeHelper.dropSessionTypesFromCache(new Object());
            TypeHelper.dropSessionTypesFromCache(null);
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    // ------------------------------------------------------------------
    // FIX: IImmutableDictionary interface closure (implementsDictionary_DictionaryOfString)
    // ------------------------------------------------------------------

    /** Mirrors StringToStringIImmutableDictionaryWrapper: only the immutable interface. */
    static final class ImmutableDictWrapper implements IImmutableDictionary<String, String> {
        private final ConcurrentHashMap<String, String> inner = new ConcurrentHashMap<>();
        @Override public int size() { return inner.size(); }
        @Override public boolean isEmpty() { return inner.isEmpty(); }
        @Override public boolean containsKey(String key) { return inner.containsKey(key); }
        @Override public String get(String key) { return inner.get(key); }
        @Override public CSharpGenericIterable<String> getKeys() {
            return CSharpGenericIterable.from(inner.keySet());
        }
        @Override public CSharpGenericIterable<String> getValues() {
            return CSharpGenericIterable.from(inner.values());
        }
    }

    @Test
    void iImmutableDictionaryClosureIncludesReadOnlyDictionary() {
        assertTrue(TypeHelper.getAllInterfaces(ImmutableDictWrapper.class).contains(CSharpReadOnlyDict.class));
    }

    // ------------------------------------------------------------------
    // DOCUMENTED LIMITATION (not compat-fixable): explicit private ctor vs
    // compiler-generated default ctor in a non-public class.
    //
    // dictionaryWith_ObjectWithNoParameterlessCtor_AsValue_Throws (DictionaryTests
    // line 1344) needs ClassWithPrivateParameterlessConstructor (private class,
    // explicit private ctor) to NOT bind under BindingFlags.Public. But javac
    // gives the IMPLICIT default ctor of a private class the same shape — the
    // private modifier, no synthetic flag (verified by the probe below and by
    // compiling javac Probe.java directly). Runtime reflection therefore cannot
    // tell "implicitly declared ctor, public in C#" from "explicit private ctor,
    // private in C#", and narrowing matchesConstructorBindingFlags would break
    // every non-public DTO that relies on the implicit ctor. The transpiler must
    // mark explicitly declared non-public ctors (e.g. with a CompatInternalCtor
    // sibling annotation) so compat can exclude them from public-flag lookups.
    // ------------------------------------------------------------------

    /** Non-public DTO with an implicit constructor (javac-generated default ctor). */
    private static final class ImplicitCtorHolder {
        public int value;
    }

    /** Non-public class with an EXPLICIT private constructor (as emitted from C#). */
    private static final class ExplicitPrivateCtorHolder {
        private ExplicitPrivateCtorHolder() {
        }
    }

    /** Probe (regression guard for the analysis above): both ctors look identical. */
    @Test
    void implicitAndExplicitPrivateCtorsAreRuntimeIndistinguishable() throws Exception {
        Constructor<?> implicit = ImplicitCtorHolder.class.getDeclaredConstructor();
        Constructor<?> explicit = ExplicitPrivateCtorHolder.class.getDeclaredConstructor();
        assertTrue(Modifier.isPrivate(implicit.getModifiers()),
            "javac default ctor of a private class carries the private modifier");
        assertTrue(Modifier.isPrivate(explicit.getModifiers()));
        assertTrue(!implicit.isSynthetic() && !explicit.isSynthetic(),
            "neither ctor is synthetic — no runtime discriminator exists");
    }

    /** Current behavior both classes depend on; unfixable at compat level (see above). */
    @Test
    void nonPublicClassCtorBindsUnderPublicFlags_KnownAmbiguity() {
        // Implicit-ctor DTOs (the vast majority of generated non-public classes) need this:
        assertNotNull(TypeHelper.getConstructorInfo(
            ImplicitCtorHolder.class, BindingFlags.Public | BindingFlags.Instance, new Class<?>[0]));
        // ...but the same leniency also binds explicit private ctors, which is why
        // dictionaryWith_ObjectWithNoParameterlessCtor_AsValue_Throws fails today.
        assertNotNull(TypeHelper.getConstructorInfo(
            ExplicitPrivateCtorHolder.class, BindingFlags.Public | BindingFlags.Instance, new Class<?>[0]));
    }
}
