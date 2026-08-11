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

    public static FontWeight getThin() { return Thin; }
    public static FontWeight getExtraLight() { return ExtraLight; }
    public static FontWeight getUltraLight() { return UltraLight; }
    public static FontWeight getLight() { return Light; }
    public static FontWeight getNormal() { return Normal; }
    public static FontWeight getRegular() { return Regular; }
    public static FontWeight getMedium() { return Medium; }
    public static FontWeight getDemiBold() { return DemiBold; }
    public static FontWeight getSemiBold() { return SemiBold; }
    public static FontWeight getBold() { return Bold; }
    public static FontWeight getUltraBold() { return UltraBold; }
    public static FontWeight getExtraBold() { return ExtraBold; }
    public static FontWeight getHeavy() { return Heavy; }
    public static FontWeight getBlack() { return Black; }
    public static FontWeight getExtraBlack() { return ExtraBlack; }
}
