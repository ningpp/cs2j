package dotnet.wpf.swing.input;

/**
 * System.Windows.Input.Key compatibility enum (subset of virtual keys used by WPF apps).
 */
public enum Key {
    None(0x00),
    End(0x23),
    Home(0x24),
    Space(0x20),
    Left(0x25),
    Up(0x26),
    Right(0x27),
    Down(0x28),
    Insert(0x2D),
    Delete(0x2E),
    Q(0x51),
    U(0x55),
    L(0x4C),
    S(0x53),
    H(0x48),
    NumPad0(0x60),
    NumPad1(0x61),
    NumPad2(0x62),
    NumPad3(0x63),
    NumPad4(0x64),
    NumPad5(0x65),
    NumPad6(0x66),
    NumPad7(0x67),
    NumPad8(0x68),
    NumPad9(0x69),
    PrintScreen(0x2C),
    Enter(0x0D),
    Return(0x0D),
    Escape(0x1B),
    Tab(0x09),
    A(0x41),
    B(0x42),
    C(0x43),
    D(0x44),
    E(0x45),
    F(0x46),
    G(0x47),
    I(0x49),
    J(0x4A),
    K(0x4B),
    M(0x4D),
    N(0x4E),
    O(0x4F),
    P(0x50),
    R(0x52),
    T(0x54),
    V(0x56),
    W(0x57),
    X(0x58),
    Y(0x59),
    Z(0x5A),
    F1(0x70),
    F2(0x71),
    F3(0x72),
    F4(0x73),
    F5(0x74),
    F6(0x75),
    F7(0x76),
    F8(0x77),
    F9(0x78),
    F10(0x79),
    F11(0x7A),
    F12(0x7B),
    OemMinus(0xBD),
    Back(0x08);

    private final int virtualKey;

    Key(int virtualKey) {
        this.virtualKey = virtualKey;
    }

    public int getVirtualKey() {
        return virtualKey;
    }

    public int getValue() {
        return virtualKey;
    }

    public static Key fromValue(int value) {
        for (Key e : values()) {
            if (e.virtualKey == value) {
                return e;
            }
        }
        return values()[0];
    }

    public static Key fromValueUnchecked(int value) {
        for (Key e : values()) {
            if (e.virtualKey == value) {
                return e;
            }
        }
        return values()[0];
    }

    /**
     * Maps an AWT key code ({@link java.awt.event.KeyEvent#getKeyCode()})
     * to the closest WPF Key. Special keys that use different VK codes are
     * mapped explicitly; letters, digits, F-keys, arrows and numpad keys
     * already share VK values with WPF.
     */
    public static Key fromAwtKeyCode(int keyCode) {
        switch (keyCode) {
            case java.awt.event.KeyEvent.VK_ENTER:
                return Enter;
            case java.awt.event.KeyEvent.VK_BACK_SPACE:
                return Back;
            case java.awt.event.KeyEvent.VK_DELETE:
                return Delete;
            case java.awt.event.KeyEvent.VK_INSERT:
                return Insert;
            case java.awt.event.KeyEvent.VK_MINUS:
                return OemMinus;
            case java.awt.event.KeyEvent.VK_SPACE:
                return Space;
            case java.awt.event.KeyEvent.VK_ESCAPE:
                return Escape;
            case java.awt.event.KeyEvent.VK_TAB:
                return Tab;
            case java.awt.event.KeyEvent.VK_HOME:
                return Home;
            case java.awt.event.KeyEvent.VK_END:
                return End;
            default:
                return fromValue(keyCode);
        }
    }
}

