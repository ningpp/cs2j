package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a member whose C# type was {@code sbyte[]}.
 *
 * <p>C# {@code sbyte} maps to Java {@code byte}, so {@code sbyte[]} and {@code byte[]} both
 * become {@code byte[]} - but System.Text.Json serializes them differently: {@code byte[]} is a
 * Base64 string and {@code sbyte[]} is a JSON array of numbers. Without this marker the member
 * lookup answers {@code byte[].class} for both, the byte-array converter is chosen, and reading
 * {@code "MySByteArray":[8]} fails with "Cannot get the value of a token type 'StartArray' as a
 * string". The member lookup returns {@code int[].class} for a marked member so the array
 * converter uses the integer element converter, which matches the C# semantics.</p>
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD, ElementType.FIELD})
public @interface CompatSByteArray {
}
