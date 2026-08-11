package dotnet.winforms.swing;

/**
 * System.Windows.Forms.DialogResult compatibility enum.
 */
public enum DialogResult {
    None(0),
    OK(1),
    Cancel(2),
    Abort(3),
    Retry(4),
    Ignore(5),
    Yes(6),
    No(7);

    private final int value;

    DialogResult(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public static DialogResult fromValue(int v) {
        for (DialogResult e : values()) {
            if (e.value == v) {
                return e;
            }
        }
        return values()[0];
    }

    public static DialogResult fromValueUnchecked(int v) {
        return fromValue(v);
    }
}

