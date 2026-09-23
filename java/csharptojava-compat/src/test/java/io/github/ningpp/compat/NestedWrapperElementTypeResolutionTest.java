package io.github.ningpp.compat;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

import java.lang.reflect.Type;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;

/**
 * Deserializing a closed generic whose element is a USER-DEFINED collection wrapper
 * (GenericListWrapper&lt;StringListWrapper&gt;, GenericStackWrapper&lt;StringStackWrapper&gt;, ...)
 * creates the OUTER converter and then the INNER wrapper's converter in ONE registration
 * session. Both creations ask for the arguments of the same erased base/interface
 * (CSharpList / CSharpICollection):
 *
 * <ul>
 *   <li>the outer query must be answered from the outer wrapper's declaration
 *       (OuterWrapper&lt;InnerWrapper&gt; → element InnerWrapper);</li>
 *   <li>the inner query must be answered from the INNER wrapper's OWN declaration
 *       (InnerWrapper extends CSharpList&lt;String&gt; → element String).</li>
 * </ul>
 *
 * The consumed-layer repeat cache in {@code TypeHelper.getGenericArguments} answered the
 * inner query by repeating the OUTER answer, so the inner wrapper's converter was built for
 * element type InnerWrapper and the reader died on the first string element
 * ("The JSON value could not be converted to class ...StringListWrapper", path $[0][0]).
 * The inner wrapper's own concrete declaration must outrank that repeat cache.
 */
public class NestedWrapperElementTypeResolutionTest {

    public static class OuterListWrapper<T> extends CSharpList<T> {
    }

    public static class InnerStringListWrapper extends CSharpList<String> {
    }

    public static class OuterStackWrapper<T> extends CSharpStack<T> {
    }

    public static class InnerStringStackWrapper extends CSharpStack<String> {
    }

    @AfterEach
    public void clearRegistrations() {
        TypeHelper.clearRegistrations();
    }

    /** The exact query sequence the converter factory performs for GenericListWrapper&lt;StringListWrapper&gt;. */
    @Test
    public void innerWrapperElementResolvesFromItsOwnDeclaration_notFromTheOuterLayer() {
        TypeHelper.clearRegistrations();
        TypeHelper.beginTypeRegistration();
        try {
            Type outer = TypeHelper.parameterized(OuterListWrapper.class, InnerStringListWrapper.class);
            TypeHelper.registerParameterizedType(outer);
            TypeHelper.registerConcreteType(OuterListWrapper.class);

            // Outer converter creation: the erased base of OuterListWrapper<T> is CSharpList.
            // The factory asks isAssignableFrom first (this publishes the concrete type), then
            // resolves the element.
            TypeHelper.isAssignableFrom(CSharpGenericIterable.class, OuterListWrapper.class);
            assertArrayEquals(new Class<?>[] { InnerStringListWrapper.class },
                TypeHelper.getGenericArguments(CSharpList.class),
                "the outer wrapper's element must be the inner wrapper");

            // Inner converter creation for InnerStringListWrapper: same raw base queried again,
            // but now the CONCRETE type is InnerStringListWrapper whose declaration names String.
            TypeHelper.isAssignableFrom(CSharpGenericIterable.class, InnerStringListWrapper.class);
            assertArrayEquals(new Class<?>[] { String.class },
                TypeHelper.getGenericArguments(CSharpList.class),
                "the inner wrapper's own CSharpList<String> declaration must decide its element type");
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    /** Same sequence for the ICollection-shaped converters (CSharpStack implements CSharpICollection). */
    @Test
    public void innerStackWrapperElementResolvesFromItsOwnDeclaration() {
        TypeHelper.clearRegistrations();
        TypeHelper.beginTypeRegistration();
        try {
            Type outer = TypeHelper.parameterized(OuterStackWrapper.class, InnerStringStackWrapper.class);
            TypeHelper.registerParameterizedType(outer);
            TypeHelper.registerConcreteType(OuterStackWrapper.class);

            TypeHelper.isAssignableFrom(CSharpGenericIterable.class, OuterStackWrapper.class);
            assertArrayEquals(new Class<?>[] { InnerStringStackWrapper.class },
                TypeHelper.getGenericArguments(CSharpICollection.class),
                "the outer stack wrapper's element must be the inner wrapper");

            TypeHelper.isAssignableFrom(CSharpGenericIterable.class, InnerStringStackWrapper.class);
            assertArrayEquals(new Class<?>[] { String.class },
                TypeHelper.getGenericArguments(CSharpICollection.class),
                "the inner stack wrapper's own CSharpStack<String> declaration must decide its element type");
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    /**
     * The repeat cache exists for repeated queries WITHIN one converter creation
     * (KeyValuePair reads key and value from one layer). When no concrete subtype declares
     * the raw class, the repeat must still answer.
     */
    @Test
    public void repeatCacheStillAnswersWhenNoConcreteSubtypeDeclaresTheRaw() {
        TypeHelper.clearRegistrations();
        TypeHelper.beginTypeRegistration();
        try {
            TypeHelper.registerParameterizedType(TypeHelper.parameterized(
                CSharpDictionary.class, String.class, Integer.class));
            TypeHelper.registerConcreteType(CSharpDictionary.class);

            TypeHelper.isAssignableFrom(CSharpGenericIterable.class, CSharpDictionary.class);
            assertArrayEquals(new Class<?>[] { String.class, Integer.class },
                TypeHelper.getGenericArguments(CSharpDictionary.class));

            // A second query within the same creation (the value converter): the layer was
            // consumed, the concrete type declares nothing new, so the repeat cache answers.
            assertArrayEquals(new Class<?>[] { String.class, Integer.class },
                TypeHelper.getGenericArguments(CSharpDictionary.class),
                "the consumed layer must still answer when no subtype declaration applies");
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }

    /** Plain nested closed generics (CSharpList&lt;CSharpList&lt;Integer&gt;&gt;) keep session-layer priority. */
    @Test
    public void plainNestedListsStillConsumeSessionLayersOuterToInner() {
        TypeHelper.clearRegistrations();
        TypeHelper.beginTypeRegistration();
        try {
            Type inner = TypeHelper.parameterized(CSharpList.class, Integer.class);
            TypeHelper.registerParameterizedType(TypeHelper.parameterized(CSharpList.class, inner));
            TypeHelper.registerConcreteType(CSharpList.class);

            TypeHelper.isAssignableFrom(CSharpGenericIterable.class, CSharpList.class);
            assertArrayEquals(new Class<?>[] { CSharpList.class },
                TypeHelper.getGenericArguments(CSharpList.class));

            TypeHelper.isAssignableFrom(CSharpGenericIterable.class, CSharpList.class);
            assertArrayEquals(new Class<?>[] { Integer.class },
                TypeHelper.getGenericArguments(CSharpList.class),
                "the second session layer (Integer) must be consumed for the inner list");
        } finally {
            TypeHelper.endTypeRegistration();
        }
    }
}
