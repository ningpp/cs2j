package io.github.ningpp.compat;

import java.lang.annotation.ElementType;
import java.lang.annotation.Retention;
import java.lang.annotation.RetentionPolicy;
import java.lang.annotation.Target;

/**
 * Runtime-visible annotation mirroring C# [DebuggerBrowsable(state)]. Emitted by the
 * converter on property getter methods; read back by PropertyInfo.getCustomAttributes.
 * The value is the .NET DebuggerBrowsableState ordinal (0=Never, 1=Collapsed, 2=RootHidden).
 *
 * <p>C#'s {@code DebuggerBrowsableAttribute} is legal on a FIELD and on a PROPERTY
 * ({@code AttributeTargets.Field | AttributeTargets.Property}). A Java field is often the
 * only declaration available to carry a ported property's state, so both METHOD and FIELD
 * are accepted here; {@code PropertyInfo} reads the accessors first and then the backing
 * field, so either placement is read back.</p>
 */
@Retention(RetentionPolicy.RUNTIME)
@Target({ElementType.METHOD, ElementType.FIELD})
public @interface DebuggerBrowsable {
    int value();
}
