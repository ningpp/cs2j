package io.github.ningpp.compat;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.assertEquals;

class BitConverterTest {
    @Test
    void toInt16_readsNativeOrderSignedShort() {
        byte[] data = new byte[] { 0x34, 0x12 };
        short expected = ByteBuffer.wrap(data).order(ByteOrder.nativeOrder()).getShort();
        assertEquals(expected, BitConverter.toInt16(data, 0));
    }

    @Test
    void toUInt16_returnsUnsignedValueAsInt() {
        byte[] data = new byte[] { (byte) 0xFF, (byte) 0xFF };
        int expected = ByteBuffer.wrap(data).order(ByteOrder.nativeOrder()).getShort() & 0xFFFF;
        assertEquals(expected, BitConverter.toUInt16(data, 0));
    }

    @Test
    void toUInt32_preservesUnsignedBitPattern() {
        byte[] data = new byte[] { 0x00, 0x00, 0x00, (byte) 0x80 };
        int expected = ByteBuffer.wrap(data).order(ByteOrder.nativeOrder()).getInt();
        assertEquals(expected, BitConverter.toUInt32(data, 0));
    }

    @Test
    void toUInt64_preservesUnsignedBitPattern() {
        byte[] data = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, (byte) 0x80 };
        long expected = ByteBuffer.wrap(data).order(ByteOrder.nativeOrder()).getLong();
        assertEquals(expected, BitConverter.toUInt64(data, 0));
    }

    @Test
    void doubleToInt64Bits_preservesNegativeZeroSign() {
        assertEquals(0x0000000000000000L, BitConverter.doubleToInt64Bits(0.0));
        assertEquals(0x8000000000000000L, BitConverter.doubleToInt64Bits(-0.0));
    }

    @Test
    void singleToInt32Bits_preservesNegativeZeroSign() {
        assertEquals(0x00000000, BitConverter.singleToInt32Bits(0.0f));
        assertEquals(0x80000000, BitConverter.singleToInt32Bits(-0.0f));
    }

    @Test
    void int32BitsToSingle_preservesNegativeZeroSign() {
        assertEquals(0.0f, BitConverter.int32BitsToSingle(0x00000000));
        assertEquals(-0.0f, BitConverter.int32BitsToSingle(0x80000000));
    }

    @Test
    void int64BitsToDouble_preservesNegativeZeroSign() {
        assertEquals(0.0, BitConverter.int64BitsToDouble(0x0000000000000000L));
        assertEquals(-0.0, BitConverter.int64BitsToDouble(0x8000000000000000L));
    }
}
