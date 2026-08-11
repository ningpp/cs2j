package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.HitTestResultBehavior compatibility enum.
 */
public enum HitTestResultBehavior {
    Stop(0),
    Continue(1);

    private final int value;

    HitTestResultBehavior(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static HitTestResultBehavior fromValue(int value) {
        for (HitTestResultBehavior e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static HitTestResultBehavior fromValueUnchecked(int value) {
        for (HitTestResultBehavior e : values()) {
            if (e.value == value) {
                return e;
            }
        }
        return values()[0];
    }
}
