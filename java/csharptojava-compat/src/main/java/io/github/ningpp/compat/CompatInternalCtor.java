package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a constructor that was {@code internal} in C#.
 *
 * <p>Java has no {@code internal}, and the converter maps it to {@code public} so that callers in
 * other packages stay reachable. That is right for ordinary code but wrong for the runtime's
 * accessibility questions: .NET's <em>collection converter</em> refuses a type whose only
 * parameterless constructor is not public, and
 * {@code TypeHelper.getConstructorInfo(type, Public | Instance)} is where that question is asked.
 * Without this marker {@code getConstructorInfo} sees Java's {@code public} modifier, answers
 * "found", and the read succeeds where .NET throws {@code NotSupportedException}.</p>
 *
 * <p>Measured: {@code CollectionTests.read_Generic_NoPublicConstructor_Throws} — the
 * {@code *InternalConstructor} parameter cases failed ("No exception thrown") while the
 * {@code *PrivateConstructor} cases had already passed, which is exactly this gap.</p>
 */
@Retention(RetentionPolicy.RUNTIME)
@Target(ElementType.CONSTRUCTOR)
public @interface CompatInternalCtor {
}
