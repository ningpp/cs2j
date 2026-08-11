package dotnet.wpf.swing;

/**
 * System.Windows.FontStretches compatibility class.
 */
public final class FontStretches {

    private FontStretches() {
    }

    public static final FontStretch UltraCondensed = new FontStretch(1);
    public static final FontStretch ExtraCondensed = new FontStretch(2);
    public static final FontStretch Condensed = new FontStretch(3);
    public static final FontStretch SemiCondensed = new FontStretch(4);
    public static final FontStretch Normal = new FontStretch(5);
    public static final FontStretch Medium = new FontStretch(5);
    public static final FontStretch SemiExpanded = new FontStretch(6);
    public static final FontStretch Expanded = new FontStretch(7);
    public static final FontStretch ExtraExpanded = new FontStretch(8);
    public static final FontStretch UltraExpanded = new FontStretch(9);

    public static FontStretch getUltraCondensed() { return UltraCondensed; }
    public static FontStretch getExtraCondensed() { return ExtraCondensed; }
    public static FontStretch getCondensed() { return Condensed; }
    public static FontStretch getSemiCondensed() { return SemiCondensed; }
    public static FontStretch getNormal() { return Normal; }
    public static FontStretch getMedium() { return Medium; }
    public static FontStretch getSemiExpanded() { return SemiExpanded; }
    public static FontStretch getExpanded() { return Expanded; }
    public static FontStretch getExtraExpanded() { return ExtraExpanded; }
    public static FontStretch getUltraExpanded() { return UltraExpanded; }
}
