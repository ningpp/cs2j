package io.github.ningpp.compat;

import java.util.Locale;

/**
 * Stub for System.Globalization.DateTimeFormatInfo.
 */
public class DateTimeFormatInfo {
    private String[] abbreviatedDayNames = new String[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    private String[] abbreviatedMonthGenitiveNames = new String[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec", "" };
    private String[] abbreviatedMonthNames = new String[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec", "" };
    private String aMDesignator = "AM";
    private String dateSeparator = "/";
    private String[] dayNames = new String[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
    private String fullDateTimePattern = "dddd, MMMM dd, yyyy h:mm:ss tt";
    private String longDatePattern = "dddd, MMMM dd, yyyy";
    private String longTimePattern = "h:mm:ss tt";
    private String monthDayPattern = "MMMM dd";
    private String[] monthGenitiveNames = new String[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December", "" };
    private String[] monthNames = new String[] { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December", "" };
    private String pMDesignator = "PM";
    private String shortDatePattern = "M/d/yyyy";
    private String[] shortestDayNames = new String[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };
    private String shortTimePattern = "h:mm tt";
    private String timeSeparator = ":";
    private String yearMonthPattern = "MMMM yyyy";
    private boolean readOnly = false;

    // --- Getters and setters ---

    public String[] getAbbreviatedDayNames() { return abbreviatedDayNames; }
    public void setAbbreviatedDayNames(String[] v) { checkReadOnly(); abbreviatedDayNames = v; }

    public String[] getAbbreviatedMonthGenitiveNames() { return abbreviatedMonthGenitiveNames; }
    public void setAbbreviatedMonthGenitiveNames(String[] v) { checkReadOnly(); abbreviatedMonthGenitiveNames = v; }

    public String[] getAbbreviatedMonthNames() { return abbreviatedMonthNames; }
    public void setAbbreviatedMonthNames(String[] v) { checkReadOnly(); abbreviatedMonthNames = v; }

    public String getAMDesignator() { return aMDesignator; }
    public void setAMDesignator(String v) { checkReadOnly(); aMDesignator = v; }

    public String getDateSeparator() { return dateSeparator; }
    public void setDateSeparator(String v) { checkReadOnly(); dateSeparator = v; }

    public String[] getDayNames() { return dayNames; }
    public void setDayNames(String[] v) { checkReadOnly(); dayNames = v; }

    public String getFullDateTimePattern() { return fullDateTimePattern; }
    public void setFullDateTimePattern(String v) { checkReadOnly(); fullDateTimePattern = v; }

    public boolean getIsReadOnly() { return readOnly; }

    public String getLongDatePattern() { return longDatePattern; }
    public void setLongDatePattern(String v) { checkReadOnly(); longDatePattern = v; }

    public String getLongTimePattern() { return longTimePattern; }
    public void setLongTimePattern(String v) { checkReadOnly(); longTimePattern = v; }

    public String getMonthDayPattern() { return monthDayPattern; }
    public void setMonthDayPattern(String v) { checkReadOnly(); monthDayPattern = v; }

    public String[] getMonthGenitiveNames() { return monthGenitiveNames; }
    public void setMonthGenitiveNames(String[] v) { checkReadOnly(); monthGenitiveNames = v; }

    public String[] getMonthNames() { return monthNames; }
    public void setMonthNames(String[] v) { checkReadOnly(); monthNames = v; }

    public String getNativeCalendarName() { return "Gregorian"; }

    public String getPMDesignator() { return pMDesignator; }
    public void setPMDesignator(String v) { checkReadOnly(); pMDesignator = v; }

    public String getRFC1123Pattern() { return "ddd, dd MMM yyyy HH:mm:ss 'GMT'"; }

    public String getShortDatePattern() { return shortDatePattern; }
    public void setShortDatePattern(String v) { checkReadOnly(); shortDatePattern = v; }

    public String[] getShortestDayNames() { return shortestDayNames; }
    public void setShortestDayNames(String[] v) { checkReadOnly(); shortestDayNames = v; }

    public String getShortTimePattern() { return shortTimePattern; }
    public void setShortTimePattern(String v) { checkReadOnly(); shortTimePattern = v; }

    public String getSortableDateTimePattern() { return "yyyy'-'MM'-'dd'T'HH':'mm':'ss"; }

    public String getTimeSeparator() { return timeSeparator; }
    public void setTimeSeparator(String v) { checkReadOnly(); timeSeparator = v; }

    public String getUniversalSortableDateTimePattern() { return "yyyy'-'MM'-'dd HH':'mm':'ss'Z'"; }

    public String getYearMonthPattern() { return yearMonthPattern; }
    public void setYearMonthPattern(String v) { checkReadOnly(); yearMonthPattern = v; }

    // --- Calendar (simplified) ---

    public java.util.Calendar getCalendar() {
        return java.util.Calendar.getInstance();
    }

    public void setCalendar(java.util.Calendar value) {
        // no-op for stub
    }

    public int getFirstDayOfWeek() { return 0; } // Sunday
    public void setFirstDayOfWeek(int value) { /* no-op */ }

    public int getCalendarWeekRule() { return 0; } // FirstDay
    public void setCalendarWeekRule(int value) { /* no-op */ }

    // --- Static properties ---

    public static DateTimeFormatInfo getCurrentInfo() {
        return new DateTimeFormatInfo();
    }

    public static DateTimeFormatInfo getInvariantInfo() {
        return new DateTimeFormatInfo();
    }

    // --- Static methods ---

    public static DateTimeFormatInfo getInstance(Object provider) {
        if (provider instanceof DateTimeFormatInfo) return (DateTimeFormatInfo) provider;
        return getCurrentInfo();
    }

    public static DateTimeFormatInfo readOnly(DateTimeFormatInfo dtfi) {
        dtfi.readOnly = true;
        return dtfi;
    }

    // --- Instance methods ---

    public String getAbbreviatedDayName(int dayOfWeek) {
        return abbreviatedDayNames[dayOfWeek % 7];
    }

    public String getAbbreviatedEraName(int era) {
        return "AD";
    }

    public String getAbbreviatedMonthName(int month) {
        if (month < 1 || month > 12) return "";
        return abbreviatedMonthNames[month - 1];
    }

    public String[] getAllDateTimePatterns() {
        return new String[] { shortDatePattern, longDatePattern, shortTimePattern, longTimePattern, fullDateTimePattern };
    }

    public String[] getAllDateTimePatterns(char format) {
        switch (format) {
            case 'd': return new String[] { shortDatePattern };
            case 'D': return new String[] { longDatePattern };
            case 'f': return new String[] { longDatePattern + " " + shortTimePattern };
            case 'F': return new String[] { fullDateTimePattern };
            case 'g': return new String[] { shortDatePattern + " " + shortTimePattern };
            case 'G': return new String[] { shortDatePattern + " " + longTimePattern };
            case 'm': return new String[] { monthDayPattern };
            case 'M': return new String[] { monthDayPattern };
            case 'o': return new String[] { "yyyy'-'MM'-'dd'T'HH':'mm':'ss.fffffffK" };
            case 'O': return new String[] { "yyyy'-'MM'-'dd'T'HH':'mm':'ss.fffffffK" };
            case 'r': return new String[] { "ddd, dd MMM yyyy HH':'mm':'ss 'GMT'" };
            case 'R': return new String[] { "ddd, dd MMM yyyy HH':'mm':'ss 'GMT'" };
            case 's': return new String[] { "yyyy'-'MM'-'dd'T'HH':'mm':'ss" };
            case 't': return new String[] { shortTimePattern };
            case 'T': return new String[] { longTimePattern };
            case 'u': return new String[] { "yyyy'-'MM'-'dd HH':'mm':'ss'Z'" };
            case 'U': return new String[] { fullDateTimePattern };
            case 'y': return new String[] { yearMonthPattern };
            case 'Y': return new String[] { yearMonthPattern };
            default: return new String[0];
        }
    }

    public String getDayName(int dayOfWeek) {
        return dayNames[dayOfWeek % 7];
    }

    public int getEra(String eraName) {
        return 1;
    }

    public String getEraName(int era) {
        return "A.D.";
    }

    public String getMonthName(int month) {
        if (month < 1 || month > 12) return "";
        return monthNames[month - 1];
    }

    public String getShortestDayName(int dayOfWeek) {
        return shortestDayNames[dayOfWeek % 7];
    }

    public Object getFormat(Class<?> formatType) {
        if (formatType == DateTimeFormatInfo.class) return this;
        return null;
    }

    public void setAllDateTimePatterns(String[] patterns, char format) {
        checkReadOnly();
        // no-op for stub
    }

    public Object clone() {
        DateTimeFormatInfo copy = new DateTimeFormatInfo();
        copy.abbreviatedDayNames = this.abbreviatedDayNames.clone();
        copy.abbreviatedMonthGenitiveNames = this.abbreviatedMonthGenitiveNames.clone();
        copy.abbreviatedMonthNames = this.abbreviatedMonthNames.clone();
        copy.aMDesignator = this.aMDesignator;
        copy.dateSeparator = this.dateSeparator;
        copy.dayNames = this.dayNames.clone();
        copy.fullDateTimePattern = this.fullDateTimePattern;
        copy.longDatePattern = this.longDatePattern;
        copy.longTimePattern = this.longTimePattern;
        copy.monthDayPattern = this.monthDayPattern;
        copy.monthGenitiveNames = this.monthGenitiveNames.clone();
        copy.monthNames = this.monthNames.clone();
        copy.pMDesignator = this.pMDesignator;
        copy.shortDatePattern = this.shortDatePattern;
        copy.shortestDayNames = this.shortestDayNames.clone();
        copy.shortTimePattern = this.shortTimePattern;
        copy.timeSeparator = this.timeSeparator;
        copy.yearMonthPattern = this.yearMonthPattern;
        return copy;
    }

    private void checkReadOnly() {
        if (readOnly) throw new UnsupportedOperationException("DateTimeFormatInfo is read-only.");
    }
}
