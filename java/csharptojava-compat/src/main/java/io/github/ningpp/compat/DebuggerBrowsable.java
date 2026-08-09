package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Runtime-visible annotation mirroring C# [DebuggerBrowsable(state)]. Emitted by the
 * converter on property getter methods; read back by PropertyInfo.getCustomAttributes.
 * The value is the .NET DebuggerBrowsableState ordinal (0=Never, 1=Collapsed, 2=RootHidden).
 */
@Retention(RetentionPolicy.RUNTIME)
@Target(ElementType.METHOD)
public @interface DebuggerBrowsable {
    int value();
}
