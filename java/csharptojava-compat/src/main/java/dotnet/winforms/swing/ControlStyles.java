package dotnet.winforms.swing;

/**
 * System.Windows.Forms.ControlStyles compatibility flags (int).
 */
public final class ControlStyles {

    private ControlStyles() {
    }

    public static final int UserPaint = 1;
    public static final int AllPaintingInWmPaint = 2;
    public static final int OptimizedDoubleBuffer = 4;
    public static final int ResizeRedraw = 8;
    public static final int Opaque = 16;
    public static final int DoubleBuffer = 32;
    public static final int Selectable = 512;
    public static final int StandardClick = 4096;
    public static final int StandardDoubleClick = 8192;
    public static final int SupportsTransparentBackColor = 32768;
    public static final int CacheText = 65536;
    public static final int FixedHeight = 131072;
    public static final int FixedWidth = 262144;
    public static final int ContainerControl = 524288;
}