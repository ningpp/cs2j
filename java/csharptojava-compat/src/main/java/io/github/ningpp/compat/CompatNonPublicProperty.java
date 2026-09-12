package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Marks a property whose C# accessors are ALL non-public (for example
 * private string MyString { get; set; }).
 *
 * .NET enumerates properties with GetProperties(Instance | Public | NonPublic | DeclaredOnly)
 * but caches one only when a public accessor exists (GetMethod?.IsPublic || SetMethod?.IsPublic),
 * so such a property is invisible to the serializer. The converted Java class still exposes the
 * accessors as methods, so the generated serializability test would accept it and deserialize into
 * a member .NET leaves untouched (PropertyVisibilityTests.privateSetter). The marker lets the
 * member lookup answer with the C# semantics while the accessors stay reachable, which is what
 * lets the SDK's "non-public member annotated with [JsonInclude] is invalid" validation fire.
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD, ElementType.FIELD})
public @interface CompatNonPublicProperty {
}
