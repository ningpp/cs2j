package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Runtime-visible annotation mirroring C# [DebuggerDisplay("...")]. Emitted by the
 * converter on classes carrying the C# attribute; read back by TypeInfo.getCustomAttributes.
 */
@Retention(RetentionPolicy.RUNTIME)
@Target(ElementType.TYPE)
public @interface DebuggerDisplay {
    String value();
}
