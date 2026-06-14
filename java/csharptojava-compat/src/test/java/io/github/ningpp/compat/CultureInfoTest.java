package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import java.util.Locale;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for CultureInfo.
 */
class CultureInfoTest {

    @Test
    void getInvariantCulture_notNull() {
        CultureInfo culture = CultureInfo.getInvariantCulture();
        assertNotNull(culture);
    }

    @Test
    void getInvariantCulture_locale() {
        CultureInfo culture = CultureInfo.getInvariantCulture();
        Locale locale = culture.toLocale();
        assertNotNull(locale);
        assertEquals("en", locale.getLanguage());
    }

    @Test
    void constructor_withEnUS() {
        CultureInfo culture = new CultureInfo("en-US");
        Locale locale = culture.toLocale();
        assertNotNull(locale);
        assertEquals("en", locale.getLanguage());
        assertEquals("US", locale.getCountry());
    }

    @Test
    void constructor_withDeDE() {
        CultureInfo culture = new CultureInfo("de-DE");
        Locale locale = culture.toLocale();
        assertNotNull(locale);
        assertEquals("de", locale.getLanguage());
        assertEquals("DE", locale.getCountry());
    }

    @Test
    void constructor_withUnderscore() {
        // C# uses underscore separators like "en_US", Java uses dash
        CultureInfo culture = new CultureInfo("en_US");
        Locale locale = culture.toLocale();
        assertNotNull(locale);
        assertEquals("en", locale.getLanguage());
    }

    @Test
    void toLocale_returnsNonNull() {
        CultureInfo culture = new CultureInfo("fr-FR");
        Locale locale = culture.toLocale();
        assertNotNull(locale);
    }

    @Test
    void toString_returnsLocaleString() {
        CultureInfo culture = new CultureInfo("en-US");
        String str = culture.toString();
        assertNotNull(str);
        // Should contain at least "en"
        assertTrue(str.toLowerCase().contains("en"));
    }

    @Test
    void getCurrentCulture_notNull() {
        CultureInfo culture = CultureInfo.getCurrentCulture();
        assertNotNull(culture);
        assertNotNull(culture.toLocale());
    }

    @Test
    void setCurrentCulture_doesNotThrow() {
        // setCurrentCulture is a no-op, just verify it doesn't throw
        assertDoesNotThrow(() -> CultureInfo.setCurrentCulture(new CultureInfo("en-US")));
    }

    @Test
    void setCurrentUICulture_doesNotThrow() {
        // setCurrentUICulture is a no-op, just verify it doesn't throw
        assertDoesNotThrow(() -> CultureInfo.setCurrentUICulture(new CultureInfo("en-US")));
    }

    @Test
    void getCurrentUICulture_notNull() {
        CultureInfo culture = CultureInfo.getCurrentUICulture();
        assertNotNull(culture);
        assertNotNull(culture.toLocale());
    }

    @Test
    void invariantCulture_toString() {
        CultureInfo culture = CultureInfo.getInvariantCulture();
        assertNotNull(culture.toString());
    }

    @Test
    void numberFormatInfo_isFormatProvider() {
        IFormatProvider provider = NumberFormatInfo.getInvariantInfo();
        assertSame(provider, provider.getFormat(NumberFormatInfo.class));
    }

    @Test
    void dateTimeFormatInfo_isFormatProvider() {
        IFormatProvider provider = DateTimeFormatInfo.getInvariantInfo();
        assertSame(provider, provider.getFormat(DateTimeFormatInfo.class));
    }
}
