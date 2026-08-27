package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

/**
 * MemoryMarshal.read must reconstruct Java value class instances (JEP 401) from
 * their encoded bytes. Value objects forbid field mutation via deep reflection,
 * so the only sanctioned creation path is a constructor; these tests mirror the
 * two generated shapes in the wild: a transformed constructor (enum packed with
 * "&lt;&lt; 28", like dotnet.text.json DbRow) and a canonical field-order
 * constructor (like StackRow).
 */
public class MemoryMarshalValueClassTest {

    enum JsonLikeTokenType {
        None, StartObject, EndObject, StartArray, EndArray, PropertyName,
        Comment, Number, String, True, False, Null;

        int getValue() {
            return ordinal();
        }
    }

    /** Mirrors dotnet.text.json JsonDocument.DbRow: fields are ints, the constructor transforms an enum. */
    public static value class SimulatedDbRow {
        public static final int Size = 12;
        public static final int UnknownSize = -1;
        final int location;
        final int sizeOrLengthUnion;
        final int numberOfRowsAndTypeUnion;

        SimulatedDbRow() {
            this.location = 0;
            this.sizeOrLengthUnion = 0;
            this.numberOfRowsAndTypeUnion = 0;
        }

        SimulatedDbRow(JsonLikeTokenType tokenType, int location, int sizeOrLength) {
            this.location = location;
            this.sizeOrLengthUnion = sizeOrLength;
            this.numberOfRowsAndTypeUnion = tokenType.getValue() << 28;
        }
    }

    /** Mirrors dotnet.text.json JsonDocument.StackRow: canonical field-order constructor. */
    public static value class SimulatedStackRow {
        public static final int Size = 8;
        final int sizeOrLength;
        final int numberOfRows;

        SimulatedStackRow() {
            this(0, -1);
        }

        SimulatedStackRow(int sizeOrLength, int numberOfRows) {
            this.sizeOrLength = sizeOrLength;
            this.numberOfRows = numberOfRows;
        }
    }

    /** No constructor's parameter count matches the field count, so reconstruction must fail fast. */
    public static value class Unreconstructable {
        final int a;
        final int b;

        Unreconstructable(int only) {
            this.a = only;
            this.b = only * 2;
        }
    }

    /** Regular class keeps the reflective field-write path. */
    public static class PlainPoint {
        public int x;
        public int y;

        public PlainPoint() {
        }
    }

    @Test
    void transformedConstructorRoundTrips() {
        SimulatedDbRow row = new SimulatedDbRow(JsonLikeTokenType.StartArray, 0, SimulatedDbRow.UnknownSize);
        Integer[] bytes = new Integer[SimulatedDbRow.Size];
        MemoryMarshal.write(new Span<>(bytes), new ObjectHolder<>(row));

        SimulatedDbRow back = MemoryMarshal.read(new ReadOnlySpan<>(bytes), SimulatedDbRow.class);

        assertEquals(0, back.location);
        assertEquals(SimulatedDbRow.UnknownSize, back.sizeOrLengthUnion);
        assertEquals(JsonLikeTokenType.StartArray.ordinal() << 28, back.numberOfRowsAndTypeUnion);
    }

    @Test
    void transformedConstructorRoundTripsWithNonTrivialValues() {
        SimulatedDbRow row = new SimulatedDbRow(JsonLikeTokenType.Number, 40, 3);
        Integer[] bytes = new Integer[SimulatedDbRow.Size];
        MemoryMarshal.write(new Span<>(bytes), new ObjectHolder<>(row));

        SimulatedDbRow back = MemoryMarshal.read(new ReadOnlySpan<>(bytes), SimulatedDbRow.class);

        assertEquals(40, back.location);
        assertEquals(3, back.sizeOrLengthUnion);
        assertEquals(JsonLikeTokenType.Number.ordinal() << 28, back.numberOfRowsAndTypeUnion);
    }

    @Test
    void canonicalConstructorRoundTrips() {
        SimulatedStackRow row = new SimulatedStackRow(5, 3);
        Integer[] bytes = new Integer[SimulatedStackRow.Size];
        MemoryMarshal.write(new Span<>(bytes), new ObjectHolder<>(row));

        SimulatedStackRow back = MemoryMarshal.read(new ReadOnlySpan<>(bytes), SimulatedStackRow.class);

        assertEquals(5, back.sizeOrLength);
        assertEquals(3, back.numberOfRows);
    }

    @Test
    void unreconstructableValueClassFailsFast() {
        SimulatedStackRow seed = new SimulatedStackRow(5, 3);
        Integer[] bytes = new Integer[SimulatedStackRow.Size];
        MemoryMarshal.write(new Span<>(bytes), new ObjectHolder<>(seed));

        assertThrows(UnsupportedOperationException.class,
            () -> MemoryMarshal.read(new ReadOnlySpan<>(bytes), Unreconstructable.class));
    }

    @Test
    void regularClassPathUnchanged() {
        PlainPoint point = new PlainPoint();
        point.x = 7;
        point.y = -2;
        Integer[] bytes = new Integer[8];
        MemoryMarshal.write(new Span<>(bytes), new ObjectHolder<>(point));

        PlainPoint back = MemoryMarshal.read(new ReadOnlySpan<>(bytes), PlainPoint.class);

        assertEquals(7, back.x);
        assertEquals(-2, back.y);
    }
}
