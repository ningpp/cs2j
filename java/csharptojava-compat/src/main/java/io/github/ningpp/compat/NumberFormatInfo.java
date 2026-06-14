package io.github.ningpp.compat;

/**
 * Stub for System.Globalization.NumberFormatInfo.
 */
public class NumberFormatInfo implements IFormatProvider {
    private int currencyDecimalDigits = 2;
    private String currencyDecimalSeparator = ".";
    private String currencyGroupSeparator = ",";
    private int[] currencyGroupSizes = new int[] { 3 };
    private int currencyNegativePattern = 0;
    private int currencyPositivePattern = 0;
    private String currencySymbol = "$";
    private String naNSymbol = "NaN";
    private String negativeInfinitySymbol = "-Infinity";
    private String negativeSign = "-";
    private int numberDecimalDigits = 2;
    private String numberDecimalSeparator = ".";
    private String numberGroupSeparator = ",";
    private int[] numberGroupSizes = new int[] { 3 };
    private int numberNegativePattern = 1;
    private int percentDecimalDigits = 2;
    private String percentDecimalSeparator = ".";
    private String percentGroupSeparator = ",";
    private int[] percentGroupSizes = new int[] { 3 };
    private int percentNegativePattern = 1;
    private int percentPositivePattern = 1;
    private String percentSymbol = "%";
    private String perMilleSymbol = "‰";
    private String positiveInfinitySymbol = "Infinity";
    private String positiveSign = "+";
    private boolean readOnly = false;

    // --- Getters and setters ---

    public int getCurrencyDecimalDigits() { return currencyDecimalDigits; }
    public void setCurrencyDecimalDigits(int v) { checkReadOnly(); currencyDecimalDigits = v; }

    public String getCurrencyDecimalSeparator() { return currencyDecimalSeparator; }
    public void setCurrencyDecimalSeparator(String v) { checkReadOnly(); currencyDecimalSeparator = v; }

    public String getCurrencyGroupSeparator() { return currencyGroupSeparator; }
    public void setCurrencyGroupSeparator(String v) { checkReadOnly(); currencyGroupSeparator = v; }

    public int[] getCurrencyGroupSizes() { return currencyGroupSizes; }
    public void setCurrencyGroupSizes(int[] v) { checkReadOnly(); currencyGroupSizes = v; }

    public int getCurrencyNegativePattern() { return currencyNegativePattern; }
    public void setCurrencyNegativePattern(int v) { checkReadOnly(); currencyNegativePattern = v; }

    public int getCurrencyPositivePattern() { return currencyPositivePattern; }
    public void setCurrencyPositivePattern(int v) { checkReadOnly(); currencyPositivePattern = v; }

    public String getCurrencySymbol() { return currencySymbol; }
    public void setCurrencySymbol(String v) { checkReadOnly(); currencySymbol = v; }

    public String getNaNSymbol() { return naNSymbol; }
    public void setNaNSymbol(String v) { checkReadOnly(); naNSymbol = v; }

    public String getNegativeInfinitySymbol() { return negativeInfinitySymbol; }
    public void setNegativeInfinitySymbol(String v) { checkReadOnly(); negativeInfinitySymbol = v; }

    public String getNegativeSign() { return negativeSign; }
    public void setNegativeSign(String v) { checkReadOnly(); negativeSign = v; }

    public int getNumberDecimalDigits() { return numberDecimalDigits; }
    public void setNumberDecimalDigits(int v) { checkReadOnly(); numberDecimalDigits = v; }

    public String getNumberDecimalSeparator() { return numberDecimalSeparator; }
    public void setNumberDecimalSeparator(String v) { checkReadOnly(); numberDecimalSeparator = v; }

    public String getNumberGroupSeparator() { return numberGroupSeparator; }
    public void setNumberGroupSeparator(String v) { checkReadOnly(); numberGroupSeparator = v; }

    public int[] getNumberGroupSizes() { return numberGroupSizes; }
    public void setNumberGroupSizes(int[] v) { checkReadOnly(); numberGroupSizes = v; }

    public int getNumberNegativePattern() { return numberNegativePattern; }
    public void setNumberNegativePattern(int v) { checkReadOnly(); numberNegativePattern = v; }

    public int getPercentDecimalDigits() { return percentDecimalDigits; }
    public void setPercentDecimalDigits(int v) { checkReadOnly(); percentDecimalDigits = v; }

    public String getPercentDecimalSeparator() { return percentDecimalSeparator; }
    public void setPercentDecimalSeparator(String v) { checkReadOnly(); percentDecimalSeparator = v; }

    public String getPercentGroupSeparator() { return percentGroupSeparator; }
    public void setPercentGroupSeparator(String v) { checkReadOnly(); percentGroupSeparator = v; }

    public int[] getPercentGroupSizes() { return percentGroupSizes; }
    public void setPercentGroupSizes(int[] v) { checkReadOnly(); percentGroupSizes = v; }

    public int getPercentNegativePattern() { return percentNegativePattern; }
    public void setPercentNegativePattern(int v) { checkReadOnly(); percentNegativePattern = v; }

    public int getPercentPositivePattern() { return percentPositivePattern; }
    public void setPercentPositivePattern(int v) { checkReadOnly(); percentPositivePattern = v; }

    public String getPercentSymbol() { return percentSymbol; }
    public void setPercentSymbol(String v) { checkReadOnly(); percentSymbol = v; }

    public String getPerMilleSymbol() { return perMilleSymbol; }
    public void setPerMilleSymbol(String v) { checkReadOnly(); perMilleSymbol = v; }

    public String getPositiveInfinitySymbol() { return positiveInfinitySymbol; }
    public void setPositiveInfinitySymbol(String v) { checkReadOnly(); positiveInfinitySymbol = v; }

    public String getPositiveSign() { return positiveSign; }
    public void setPositiveSign(String v) { checkReadOnly(); positiveSign = v; }

    public boolean getIsReadOnly() { return readOnly; }

    // --- Static properties ---

    public static NumberFormatInfo getCurrentInfo() {
        return new NumberFormatInfo();
    }

    public static NumberFormatInfo getInvariantInfo() {
        return new NumberFormatInfo();
    }

    // --- Static methods ---

    public static NumberFormatInfo getInstance(Object formatProvider) {
        if (formatProvider instanceof NumberFormatInfo) return (NumberFormatInfo) formatProvider;
        return getCurrentInfo();
    }

    public static NumberFormatInfo readOnly(NumberFormatInfo nfi) {
        nfi.readOnly = true;
        return nfi;
    }

    // --- Instance methods ---

    public Object getFormat(Class<?> formatType) {
        if (formatType == NumberFormatInfo.class) return this;
        return null;
    }

    public Object clone() {
        NumberFormatInfo copy = new NumberFormatInfo();
        copy.currencyDecimalDigits = this.currencyDecimalDigits;
        copy.currencyDecimalSeparator = this.currencyDecimalSeparator;
        copy.currencyGroupSeparator = this.currencyGroupSeparator;
        copy.currencyGroupSizes = this.currencyGroupSizes;
        copy.currencyNegativePattern = this.currencyNegativePattern;
        copy.currencyPositivePattern = this.currencyPositivePattern;
        copy.currencySymbol = this.currencySymbol;
        copy.naNSymbol = this.naNSymbol;
        copy.negativeInfinitySymbol = this.negativeInfinitySymbol;
        copy.negativeSign = this.negativeSign;
        copy.numberDecimalDigits = this.numberDecimalDigits;
        copy.numberDecimalSeparator = this.numberDecimalSeparator;
        copy.numberGroupSeparator = this.numberGroupSeparator;
        copy.numberGroupSizes = this.numberGroupSizes;
        copy.numberNegativePattern = this.numberNegativePattern;
        copy.percentDecimalDigits = this.percentDecimalDigits;
        copy.percentDecimalSeparator = this.percentDecimalSeparator;
        copy.percentGroupSeparator = this.percentGroupSeparator;
        copy.percentGroupSizes = this.percentGroupSizes;
        copy.percentNegativePattern = this.percentNegativePattern;
        copy.percentPositivePattern = this.percentPositivePattern;
        copy.percentSymbol = this.percentSymbol;
        copy.perMilleSymbol = this.perMilleSymbol;
        copy.positiveInfinitySymbol = this.positiveInfinitySymbol;
        copy.positiveSign = this.positiveSign;
        return copy;
    }

    private void checkReadOnly() {
        if (readOnly) throw new UnsupportedOperationException("NumberFormatInfo is read-only.");
    }
}
