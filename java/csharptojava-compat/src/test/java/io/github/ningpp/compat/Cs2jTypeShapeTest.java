package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/** Empty marker interfaces let Java query the original C# type shape. */
class Cs2jTypeShapeTest {

    private static final class PlainStruct implements Cs2jStructMarker {
    }

    private static final class ReadonlyRecordStruct implements Cs2jStructMarker,
            Cs2jRecordMarker, Cs2jReadOnlyMarker {
    }

    private static final class LoweredRefStruct implements Cs2jStructMarker,
            Cs2jByRefLikeMarker {
    }

    private static final class OrdinaryClass {
    }

    @Test
    void structShapeIsQueryable() {
        assertTrue(Cs2jTypeShape.isStruct(PlainStruct.class));
        assertTrue(Cs2jTypeShape.isMarked(PlainStruct.class));
        assertFalse(Cs2jTypeShape.isRecord(PlainStruct.class));
        assertFalse(Cs2jTypeShape.isReadOnly(PlainStruct.class));
    }

    @Test
    void combinedShapesAreQueryable() {
        assertTrue(Cs2jTypeShape.isStruct(ReadonlyRecordStruct.class));
        assertTrue(Cs2jTypeShape.isRecord(ReadonlyRecordStruct.class));
        assertTrue(Cs2jTypeShape.isReadOnly(ReadonlyRecordStruct.class));
    }

    @Test
    void byRefLikeMarkerJoinsTheSameFamily() {
        assertTrue(Cs2jTypeShape.isByRefLike(LoweredRefStruct.class));
        assertTrue(Cs2jTypeShape.isMarked(LoweredRefStruct.class));
    }

    @Test
    void ordinaryClassAndNullAreUnmarked() {
        assertFalse(Cs2jTypeShape.isMarked(OrdinaryClass.class));
        assertFalse(Cs2jTypeShape.isStruct(OrdinaryClass.class));
        assertFalse(Cs2jTypeShape.isMarked(null));
    }
}
