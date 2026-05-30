package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.util.ArrayList;

import static org.junit.jupiter.api.Assertions.*;

class ReflectionHelperTest {
    @Test
    void asIterable_boxesPrimitiveDoubleArray() {
        Iterable<Double> values = ReflectionHelper.asIterable(new double[] { 1.5, 2.5 });

        ArrayList<Double> actual = new ArrayList<>();
        values.forEach(actual::add);

        assertEquals(2, actual.size());
        assertEquals(1.5, actual.get(0));
        assertEquals(2.5, actual.get(1));
    }

    @Test
    void asIterable_returnsExistingIterable() {
        ArrayList<String> source = new ArrayList<>();
        source.add("a");

        Iterable<String> values = ReflectionHelper.asIterable(source);

        assertSame(source, values);
    }
}
