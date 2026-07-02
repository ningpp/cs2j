package io.github.ningpp.compat;

import java.util.ArrayList;
import java.util.BitSet;
import java.util.Iterator;
import java.util.List;
import java.util.Objects;

public class CSharpBitArray implements Iterable<Object>, Cloneable {
    private BitSet bitSet;
    private int length;
    private final Object syncRoot = new Object();

    public CSharpBitArray(int length) {
        if (length < 0) throw new IndexOutOfBoundsException("length must be non-negative");
        this.bitSet = new BitSet(length);
        this.length = length;
    }
    public CSharpBitArray(int length, boolean defaultValue) {
        this(length);
        if (defaultValue) bitSet.set(0, length);
    }
    public CSharpBitArray(boolean[] values) {
        this.length = values.length;
        this.bitSet = new BitSet(length);
        for (int i = 0; i < length; i++) bitSet.set(i, values[i]);
    }
    public CSharpBitArray(byte[] bytes) {
        this.length = bytes.length * 8;
        this.bitSet = BitSet.valueOf(bytes);
    }
    public CSharpBitArray(int[] values) {
        // Each int is 32 bits
        this.length = values.length * 32;
        this.bitSet = new BitSet(length);
        for (int i = 0; i < values.length; i++) {
            for (int bit = 0; bit < 32; bit++) {
                if ((values[i] & (1 << bit)) != 0) bitSet.set(i * 32 + bit);
            }
        }
    }

    public boolean get(int index) {
        Objects.checkIndex(index, length);
        return bitSet.get(index);
    }
    public void set(int index, boolean value) {
        Objects.checkIndex(index, length);
        bitSet.set(index, value);
    }
    public void setAll(boolean value) {
        if (value) bitSet.set(0, length); else bitSet.clear(0, length);
    }
    public int getLength() { return length; }
    public void setLength(int value) {
        if (value < 0) throw new IndexOutOfBoundsException("length must be non-negative");
        this.length = value;
    }
    public int getCount() { return length; }
    public boolean getIsReadOnly() { return false; }
    public boolean getIsSynchronized() { return false; }
    public Object getSyncRoot() { return syncRoot; }

    public CSharpBitArray and(CSharpBitArray other) {
        bitSet.and(other.bitSet);
        return this;
    }
    public CSharpBitArray or(CSharpBitArray other) {
        bitSet.or(other.bitSet);
        return this;
    }
    public CSharpBitArray xor(CSharpBitArray other) {
        bitSet.xor(other.bitSet);
        return this;
    }
    public CSharpBitArray not() {
        bitSet.flip(0, length);
        return this;
    }
    public CSharpBitArray leftShift(int count) {
        if (count <= 0) return this;
        long[] words = bitSet.toLongArray();
        BitSet newSet = new BitSet(length);
        for (int i = 0; i < words.length; i++) {
            long shifted = words[i] << count;
            for (int bit = 0; bit < 64; bit++) {
                int idx = i * 64 + bit;
                if (idx < length && (shifted & (1L << bit)) != 0) {
                    newSet.set(idx);
                }
            }
        }
        this.bitSet = newSet;
        return this;
    }
    public CSharpBitArray rightShift(int count) {
        if (count <= 0) return this;
        long[] words = bitSet.toLongArray();
        BitSet newSet = new BitSet(length);
        for (int i = 0; i < words.length; i++) {
            long shifted = words[i] >>> count;
            for (int bit = 0; bit < 64; bit++) {
                int idx = i * 64 + bit;
                if (idx < length && (shifted & (1L << bit)) != 0) {
                    newSet.set(idx);
                }
            }
        }
        this.bitSet = newSet;
        return this;
    }
    public void copyTo(Object[] array, int index) {
        for (int i = 0; i < length; i++) array[index + i] = bitSet.get(i);
    }
    @Override public Iterator<Object> iterator() {
        List<Object> wrapped = new ArrayList<>();
        for (int i = 0; i < length; i++) wrapped.add(bitSet.get(i));
        return wrapped.iterator();
    }
    @Override public CSharpBitArray clone() {
        try {
            CSharpBitArray c = (CSharpBitArray) super.clone();
            c.bitSet = (BitSet) bitSet.clone();
            return c;
        } catch (CloneNotSupportedException e) { throw new InternalError(); }
    }
}
