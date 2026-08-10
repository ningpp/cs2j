package dotnet.wpf.swing;

/**
 * System.Windows.FontWeights compatibility class.
 */
public final class FontWeights {

    private FontWeights() {
    }

    public static final FontWeight Thin = new FontWeight(100);
    public static final FontWeight ExtraLight = new FontWeight(200);
    public static final FontWeight UltraLight = new FontWeight(200);
    public static final FontWeight Light = new FontWeight(300);
    public static final FontWeight Normal = new FontWeight(400);
    public static final FontWeight Regular = new FontWeight(400);
    public static final FontWeight Medium = new FontWeight(500);
    public static final FontWeight DemiBold = new FontWeight(600);
    public static final FontWeight SemiBold = new FontWeight(600);
    public static final FontWeight Bold = new FontWeight(700);
    public static final FontWeight UltraBold = new FontWeight(800);
    public static final FontWeight ExtraBold = new FontWeight(800);
    public static final FontWeight Heavy = new FontWeight(900);
    public static final FontWeight Black = new FontWeight(900);
    public static final FontWeight ExtraBlack = new FontWeight(950);

    public static FontWeight fromOpenTypeWeight(int weight) {
        return new FontWeight(weight);
    }
}
