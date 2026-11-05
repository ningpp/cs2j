package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a Java class emitted from a C# struct (value type).
 *
 * <p>C# value types use field-wise <c>ValueType.Equals</c> unless they override
 * <c>Equals</c>; the generated Java class only has that behaviour when the
 * converter emits an <c>equals</c> override. Test assertions such as
 * <c>Assert.AreEqual(expectedStruct, actualStruct)</c> must therefore fall back
 * to a field-wise comparison for unannotated {@code equals}. This runtime
 * marker lets compat distinguish structs from reference types (which keep
 * reference identity semantics).</p>
 */
@Target(ElementType.TYPE)
@Retention(RetentionPolicy.RUNTIME)
public @interface CompatValueType {
}
