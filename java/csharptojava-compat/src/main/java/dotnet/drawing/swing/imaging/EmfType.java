package dotnet.drawing.swing.imaging;

/**
 * System.Drawing.Imaging.EmfType compatibility enum.
 */
public enum EmfType {
    EmfOnly(3), EmfPlusOnly(4), EmfPlusDual(5);

    private final int value;

    EmfType(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static EmfType fromValue(int v) {
        for (EmfType e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static EmfType fromValueUnchecked(int v) {
        return fromValue(v);
    }
}