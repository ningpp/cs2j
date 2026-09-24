package io.github.ningpp.compat;

/**
 * System.Runtime.InteropServices.GCHandleType compatibility enum.
 *
 * <p>Underlying values mirror the .NET definition: Weak=0,
 * WeakTrackResurrection=1, Normal=2, Pinned=3.</p>
 */
public final class GCHandleType {

    public static final GCHandleType Weak = new GCHandleType(0);
    public static final GCHandleType WeakTrackResurrection = new GCHandleType(1);
    public static final GCHandleType Normal = new GCHandleType(2);
    public static final GCHandleType Pinned = new GCHandleType(3);

    private final int value;

    private GCHandleType(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static GCHandleType fromValue(int v) {
        switch (v) {
            case 0: return Weak;
            case 1: return WeakTrackResurrection;
            case 2: return Normal;
            case 3: return Pinned;
            default: throw new IllegalArgumentException("Unknown GCHandleType value: " + v);
        }
    }
}
