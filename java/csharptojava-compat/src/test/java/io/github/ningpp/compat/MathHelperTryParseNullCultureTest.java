package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

/// Reproduces Regex.java: 'MathHelper.tryParseUInt(name, style, null, holder)' —
/// a null culture argument was ambiguous between the NumberFormatInfo and Locale
/// overloads. Those redundant delegating overloads were removed, leaving only the
/// CultureInfo overload, so a null culture now resolves unambiguously.
class MathHelperTryParseNullCultureTest {
    @Test
    void tryParseUIntWithNullCultureIsUnambiguous() {
        IntHolder holder = new IntHolder();
        boolean ok = MathHelper.tryParseUInt("123", NumberStyles.Integer, null, holder);
        assertTrue(ok);
    }

    @Test
    void tryParseULongWithNullCultureIsUnambiguous() {
        LongHolder holder = new LongHolder();
        boolean ok = MathHelper.tryParseULong("123", NumberStyles.Integer, null, holder);
        assertTrue(ok);
    }
}
