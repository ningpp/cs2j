package dotnet.wpf.swing.media;

import dotnet.wpf.swing.FlowDirection;
import dotnet.wpf.swing.FontStretches;
import dotnet.wpf.swing.FontStyle;
import dotnet.wpf.swing.FontWeights;
import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class FormattedTextTest {

    @Test
    void measuresNonEmptyText() {
        FormattedText ft = new FormattedText("Hello", CultureInfoLike.instance(),
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"), new FontStyle(), FontWeights.Regular, FontStretches.Normal),
                12, Brushes.Black);
        assertTrue(ft.getWidth() > 0);
        assertTrue(ft.getHeight() > 0);
    }

    @Test
    void emptyText_widthZero() {
        FormattedText ft = new FormattedText("", CultureInfoLike.instance(),
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"), new FontStyle(), FontWeights.Regular, FontStretches.Normal),
                12, Brushes.Black);
        assertEquals(0.0, ft.getWidth(), 1e-9);
    }

    @Test
    void largerFont_givesLargerSize() {
        FormattedText small = new FormattedText("abc", CultureInfoLike.instance(), FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"), new FontStyle(), FontWeights.Regular, FontStretches.Normal),
                10, Brushes.Black);
        FormattedText big = new FormattedText("abc", CultureInfoLike.instance(), FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"), new FontStyle(), FontWeights.Regular, FontStretches.Normal),
                40, Brushes.Black);
        assertTrue(big.getHeight() > small.getHeight());
        assertTrue(big.getWidth() >= small.getWidth());
    }

    @Test
    void maxTextWidth_clampsWidth() {
        FormattedText ft = new FormattedText("Hello World", CultureInfoLike.instance(), FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"), new FontStyle(), FontWeights.Regular, FontStretches.Normal),
                12, Brushes.Black);
        ft.setMaxTextWidth(5);
        assertTrue(ft.getWidth() <= 5);
    }

    /** Minimal culture-like object to avoid pulling in CultureInfo for the geometry test. */
    public static final class CultureInfoLike {
        static Object instance() {
            return new Object();
        }
    }
}
