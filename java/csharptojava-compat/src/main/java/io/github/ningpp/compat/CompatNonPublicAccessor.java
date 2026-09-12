package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks ONE accessor of a C# property that is not public.
 *
 * <p>C# reflects properties with
 * {@code GetProperties(Instance | Public | NonPublic | DeclaredOnly)} and then keeps a property only
 * when a PUBLIC accessor exists — {@code GetMethod?.IsPublic || SetMethod?.IsPublic}. The converted
 * Java class cannot express that: a C# {@code internal string P { get; private set; }} becomes a
 * Java interface of public methods, so {@code Modifier.isPublic} is true for every accessor and the
 * property is cached and serialized where .NET would ignore it. Measured on
 * {@code PropertyVisibilityTests.ClassWithNonPublicProperties}: the expected payload is {@code {}}
 * but the write produced {@code {"MyInt":0,"MyString":null,"GetMyFloat":0.0,"GetMyDouble":0.0}}.</p>
 *
 * <p>{@link CompatNonPublicProperty} already covers the case where BOTH accessors are non-public.
 * This marker carries the per-accessor fact the mixed case needs, so {@code PropertyInfo}'s
 * accessor lookups can mirror {@code PropertyInfo.GetGetMethod()} / {@code GetSetMethod()}, which
 * return a public accessor only.</p>
 */
@Retention(RetentionPolicy.RUNTIME)
@Target(ElementType.METHOD)
public @interface CompatNonPublicAccessor {
}
