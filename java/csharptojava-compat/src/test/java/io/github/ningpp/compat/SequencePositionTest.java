package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotEquals;

/**
 * SequencePosition is a value type in C# (struct): default positions compare
 * equal, and two positions over the same segment/index compare equal. The Java
 * port must mirror that or Assert.equal(default, reader.getPosition()) fails.
 */
public class SequencePositionTest {

    @Test
    public void defaultPositionsAreEqual() {
        assertEquals(new SequencePosition(), new SequencePosition());
    }

    @Test
    public void sameSegmentAndIndexAreEqual() {
        Object segment = new Object();
        assertEquals(new SequencePosition(segment, 3), new SequencePosition(segment, 3));
    }

    @Test
    public void differentIndexOrSegmentAreNotEqual() {
        Object segment = new Object();
        assertNotEquals(new SequencePosition(segment, 1), new SequencePosition(segment, 2));
        assertNotEquals(new SequencePosition(new Object(), 1), new SequencePosition(new Object(), 1));
    }
}
