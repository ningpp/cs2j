package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

/**
 * C# enums with explicit non-sequential values are emitted as final classes with
 * static constants, {@code name()}/{@code ordinal()}/{@code getValue()} and a
 * static {@code values()} (see the converted JsonCommentHandling). Enum.GetValues
 * and Enum.GetNames must work for that shape too, not only for Java enums.
 */
public class EnumHelperPseudoEnumTest {

    @Test
    public void getValuesArray_readsPseudoEnumValuesAndFiltersUnmapped() {
        Object[] values = EnumHelper.getValuesArray(CommentHandling.class);

        assertEquals(3, values.length);
        assertEquals(CommentHandling.Disallow, values[0]);
        assertEquals(CommentHandling.Skip, values[1]);
        assertEquals(CommentHandling.Allow, values[2]);
    }

    @Test
    public void getNames_readsPseudoEnumNames() {
        assertArrayEquals(new String[] {"Disallow", "Skip", "Allow"},
                EnumHelper.getNames(CommentHandling.class));
    }

    @Test
    public void getValues_iteratesPseudoEnumConstants() {
        Object[] values = (Object[]) EnumHelper.getValues(CommentHandling.class).unwrap();

        assertEquals(3, values.length);
        assertEquals(CommentHandling.Allow, values[2]);
    }

    /** Mirrors the converter's pseudo-enum emission (JsonCommentHandling). */
    public static final class CommentHandling {
        private final String __cs2jName;
        private final int __cs2jOrdinal;
        private final byte __cs2jValue;
        public static final CommentHandling Disallow = new CommentHandling("Disallow", 0, (byte) 0);
        public static final CommentHandling Skip = new CommentHandling("Skip", 1, (byte) 1);
        public static final CommentHandling Allow = new CommentHandling("Allow", 2, (byte) 2);
        private static final CommentHandling _UNMAPPED = new CommentHandling("_UNMAPPED", 3, (byte) 3);
        private static final CommentHandling[] __cs2jValues =
                new CommentHandling[] {Disallow, Skip, Allow, _UNMAPPED};

        private CommentHandling(String name, int ordinal, byte value) {
            this.__cs2jName = name;
            this.__cs2jOrdinal = ordinal;
            this.__cs2jValue = value;
        }

        public byte getValue() {
            return this.__cs2jValue;
        }

        public int ordinal() {
            return this.__cs2jOrdinal;
        }

        public String name() {
            return this.__cs2jName;
        }

        @Override
        public String toString() {
            return this.__cs2jName;
        }

        public static CommentHandling[] values() {
            return __cs2jValues.clone();
        }
    }
}
