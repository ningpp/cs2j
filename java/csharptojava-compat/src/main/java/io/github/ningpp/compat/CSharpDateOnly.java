package io.github.ningpp.compat;

import java.time.LocalDate;
import java.time.Year;
import java.time.YearMonth;
import java.time.temporal.ChronoUnit;

/**
 * C# System.DateOnly compatibility class.
 */
public final class CSharpDateOnly implements Comparable<CSharpDateOnly> {

    public static final CSharpDateOnly MIN_VALUE = new CSharpDateOnly(1, 1, 1);
    public static final CSharpDateOnly MAX_VALUE = new CSharpDateOnly(9999, 12, 31);

    private final LocalDate localDate;

    public CSharpDateOnly(int year, int month, int day) {
        this.localDate = LocalDate.of(year, month, day);
    }

    private CSharpDateOnly(LocalDate localDate) {
        this.localDate = localDate;
    }

    // --- Properties ---

    public int getYear() { return localDate.getYear(); }
    public int getMonth() { return localDate.getMonthValue(); }
    public int getDay() { return localDate.getDayOfMonth(); }
    public DayOfWeek getDayOfWeek() { return DayOfWeek.fromJava(localDate.getDayOfWeek()); }
    public int getDayOfYear() { return localDate.getDayOfYear(); }
    public int getDayNumber() { return (int) LocalDate.of(1, 1, 1).until(localDate, ChronoUnit.DAYS); }

    // --- Add methods ---

    public CSharpDateOnly addDays(int days) { return new CSharpDateOnly(localDate.plusDays(days)); }
    public CSharpDateOnly addMonths(int months) { return new CSharpDateOnly(localDate.plusMonths(months)); }
    public CSharpDateOnly addYears(int years) { return new CSharpDateOnly(localDate.plusYears(years)); }
    public CSharpDateOnly addDays(long days) { return new CSharpDateOnly(localDate.plusDays(days)); }

    // --- Static Methods ---

    public static CSharpDateOnly fromDateTime(CSharpDateTime dateTime) {
        return new CSharpDateOnly(dateTime.getYear(), dateTime.getMonth(), dateTime.getDay());
    }

    public static CSharpDateOnly fromDayNumber(int dayNumber) {
        LocalDate epoch = LocalDate.of(1, 1, 1);
        return new CSharpDateOnly(epoch.plusDays(dayNumber));
    }

    public static CSharpDateOnly parse(String s) {
        return new CSharpDateOnly(LocalDate.parse(s.trim()));
    }

    public static boolean tryParse(String s, ObjectHolder<CSharpDateOnly> result) {
        try {
            result.value = parse(s);
            return true;
        } catch (Exception e) {
            result.value = MIN_VALUE;
            return false;
        }
    }

    // --- toString ---
    // C# format: "M/d/yyyy"
    @Override
    public String toString() {
        return localDate.getMonthValue() + "/" + localDate.getDayOfMonth() + "/" + localDate.getYear();
    }

    @Override
    public int compareTo(CSharpDateOnly other) { return localDate.compareTo(other.localDate); }

    @Override
    public boolean equals(Object obj) {
        if (this == obj) return true;
        if (!(obj instanceof CSharpDateOnly)) return false;
        return localDate.equals(((CSharpDateOnly) obj).localDate);
    }

    @Override
    public int hashCode() { return localDate.hashCode(); }
}
