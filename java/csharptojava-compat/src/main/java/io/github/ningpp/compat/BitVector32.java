package io.github.ningpp.compat;

/**
 * Compat for System.Collections.Specialized.BitVector32 — minimal wrapper.
 */
public class BitVector32 {
    private int data;

    public BitVector32() {
        this.data = 0;
    }

    public BitVector32(int data) {
        this.data = data;
    }

    public BitVector32(BitVector32 value) {
        this.data = value.data;
    }

    public int getData() {
        return data;
    }

    public void setData(int value) {
        this.data = value;
    }

    @Override
    public boolean equals(Object obj) {
        return obj instanceof BitVector32 other && other.data == data;
    }

    @Override
    public int hashCode() {
        return data;
    }

    @Override
    public String toString() {
        return "BitVector32{" + data + "}";
    }
}
