package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * EnumHelper.tryParse must accept a Class&lt;?&gt; enum class argument: the
 * converter passes the runtime Class field (tClass is Class&lt;?&gt;), with T
 * inferred from the ObjectHolder (EnumConverter.ReadWithQuotes). A strict
 * Class&lt;T&gt; parameter makes the generated call unresolvable.
 */
public class EnumHelperTryParseWildcardClassTest {

    @Test
    public void tryParse_acceptsWildcardClass() {
        ObjectHolder<DayOfWeek> holder = new ObjectHolder<>();
        Class<?> enumClass = DayOfWeek.class;
        assertTrue(EnumHelper.tryParse("Monday", holder, enumClass));
        assertTrue(EnumHelper.tryParse("monday", true, holder, enumClass));
        assertTrue(holder.value == DayOfWeek.Monday);
    }
}
