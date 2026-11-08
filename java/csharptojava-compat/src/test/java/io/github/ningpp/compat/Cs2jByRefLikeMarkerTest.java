package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * A C# {@code ref struct} lowered by ref-struct-lowering keeps its observable
 * {@code Type.IsByRefLike} behaviour through the marker interface.
 */
class Cs2jByRefLikeMarkerTest {

    private static final class LoweredRefStruct implements Cs2jByRefLikeMarker {
    }

    private static final class OrdinaryStruct {
    }

    @Test
    void loweredRefStructIsByRefLike() {
        assertTrue(TypeHelper.getIsByRefLike(LoweredRefStruct.class));
    }

    @Test
    void ordinaryTypeIsNotByRefLike() {
        assertFalse(TypeHelper.getIsByRefLike(OrdinaryStruct.class));
        assertFalse(TypeHelper.getIsByRefLike(String.class));
    }

    @Test
    void nullIsNotByRefLike() {
        assertFalse(TypeHelper.getIsByRefLike(null));
    }
}
