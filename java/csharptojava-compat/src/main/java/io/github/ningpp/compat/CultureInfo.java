package io.github.ningpp.compat;

import java.util.Locale;

/**
 * Stub for System.Globalization.CultureInfo.
 * Wraps a java.util.Locale.
 */
public class CultureInfo implements IFormatProvider, Cloneable {
    private final Locale locale;
    private final boolean readOnly;

    public CultureInfo(String name) {
        this.locale = Locale.forLanguageTag(name.replace('_', '-'));
        this.readOnly = false;
    }

    private CultureInfo(Locale locale, boolean readOnly) {
        this.locale = locale;
        this.readOnly = readOnly;
    }

    // --- Static properties ---

    public static CultureInfo getCurrentCulture() {
        return new CultureInfo(Locale.getDefault(), false);
    }

    public static void setCurrentCulture(CultureInfo cultureInfo) {
        // no-op: JVM locale is JVM-wide setting, not thread-local
    }

    public static CultureInfo getCurrentUICulture() {
        return new CultureInfo(Locale.getDefault(), false);
    }

    public static void setCurrentUICulture(CultureInfo cultureInfo) {
        // no-op: JVM locale is JVM-wide setting, not thread-local
    }

    public static CultureInfo getDefaultThreadCurrentCulture() {
        return null;
    }

    public static void setDefaultThreadCurrentCulture(CultureInfo value) {
        // no-op
    }

    public static CultureInfo getDefaultThreadCurrentUICulture() {
        return null;
    }

    public static void setDefaultThreadCurrentUICulture(CultureInfo value) {
        // no-op
    }

    public static CultureInfo getInstalledUICulture() {
        return new CultureInfo(Locale.getDefault(), false);
    }

    public static CultureInfo getInvariantCulture() {
        return new CultureInfo(Locale.ENGLISH, true);
    }

    // --- Instance properties ---

    public String getName() {
        return locale.toLanguageTag();
    }

    public String getDisplayName() {
        return locale.getDisplayName();
    }

    public String getEnglishName() {
        return locale.getDisplayName(Locale.ENGLISH);
    }

    public String getNativeName() {
        return locale.getDisplayName(locale);
    }

    public String getIetfLanguageTag() {
        return locale.toLanguageTag();
    }

    public String getTwoLetterISOLanguageName() {
        return locale.getLanguage();
    }

    public String getThreeLetterISOLanguageName() {
        return locale.getISO3Language();
    }

    public String getThreeLetterWindowsLanguageName() {
        return locale.getISO3Language(); // best approximation
    }

    public int getLCID() {
        return 0; // not directly available in Java
    }

    public int getKeyboardLayoutId() {
        return 0;
    }

    public boolean getIsNeutralCulture() {
        String country = locale.getCountry();
        return country == null || country.isEmpty();
    }

    public boolean getIsReadOnly() {
        return readOnly;
    }

    public boolean getUseUserOverride() {
        return false;
    }

    public CultureInfo getParent() {
        Locale parent = locale;
        String tag = locale.toLanguageTag();
        int lastDash = tag.lastIndexOf('-');
        if (lastDash > 0) {
            parent = Locale.forLanguageTag(tag.substring(0, lastDash));
        }
        return new CultureInfo(parent, readOnly);
    }

    public TextInfo getTextInfo() {
        return new TextInfo(locale);
    }

    public NumberFormatInfo getNumberFormat() {
        return new NumberFormatInfo();
    }

    public void setNumberFormat(NumberFormatInfo value) {
        // no-op for stub
    }

    public DateTimeFormatInfo getDateTimeFormat() {
        return new DateTimeFormatInfo();
    }

    public void setDateTimeFormat(DateTimeFormatInfo value) {
        // no-op for stub
    }

    public java.util.Calendar getCalendar() {
        return java.util.Calendar.getInstance(locale);
    }

    public java.util.Calendar[] getOptionalCalendars() {
        return new java.util.Calendar[] { getCalendar() };
    }

    public CompareInfo getCompareInfo() {
        return new CompareInfo(locale);
    }

    public CultureTypes getCultureTypes() {
        return CultureTypes.NEUTRAL_CULTURE;
    }

    // --- Conversion helpers ---

    public Locale toLocale() { return locale; }
    public Locale getLocale() { return locale; }

    public Object getFormat(Class<?> formatType) { return this; }

    public static CultureInfo fromLocale(Locale locale) {
        return new CultureInfo(locale, false);
    }

    // --- Static methods ---

    public static CultureInfo getCultureInfo(int culture) {
        // LCID to locale mapping is complex; return invariant for 0x7F (invariant)
        if (culture == 0x7F) return getInvariantCulture();
        return new CultureInfo(Locale.forLanguageTag("en-US"), true);
    }

    public static CultureInfo getCultureInfo(String name) {
        if (name == null) throw new NullPointerException("name");
        return new CultureInfo(Locale.forLanguageTag(name.replace('_', '-')), true);
    }

    public static CultureInfo getCultureInfo(String name, String altName) {
        return getCultureInfo(name);
    }

    public static CultureInfo getCultureInfo(String name, boolean predefinedOnly) {
        return getCultureInfo(name);
    }

    public static CultureInfo getCultureInfoByIetfLanguageTag(String name) {
        if (name == null) throw new NullPointerException("name");
        return new CultureInfo(Locale.forLanguageTag(name), true);
    }

    public static CultureInfo[] getCultureTypes(CultureTypes types) {
        // Return a basic set of cultures
        return new CultureInfo[] {
            getInvariantCulture(),
            new CultureInfo("en-US"),
            new CultureInfo("en-GB"),
            new CultureInfo("zh-CN"),
            new CultureInfo("ja-JP"),
        };
    }

    public static CultureInfo createSpecificCulture(String name) {
        return getCultureInfo(name);
    }

    public static CultureInfo ReadOnly(CultureInfo ci) {
        if (ci.readOnly) return ci;
        return new CultureInfo(ci.locale, true);
    }

    public CultureInfo getConsoleFallbackUICulture() {
        return getInvariantCulture();
    }

    public void clearCachedData() {
        // no-op
    }

    public Object clone() {
        return new CultureInfo(locale, false);
    }

    // --- Object overrides ---

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CultureInfo)) return false;
        return locale.equals(((CultureInfo) obj).locale);
    }

    @Override
    public int hashCode() {
        return locale.hashCode();
    }

    @Override
    public String toString() {
        return locale.toString();
    }
}
