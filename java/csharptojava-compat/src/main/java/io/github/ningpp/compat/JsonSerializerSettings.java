package io.github.ningpp.compat;

/**
 * Compat for Newtonsoft.Json.JsonSerializerSettings — settings stub; the
 * converted tests mostly pass it around.
 */
public class JsonSerializerSettings {
    public StringEscapeHandling stringEscapeHandling = StringEscapeHandling.Default;
    public boolean formatIndented;
    public String dateFormatString;
    public Object contractResolver;
    public Object nullValueHandling;
    public Object referenceLoopHandling;
    public PreserveReferencesHandling preserveReferencesHandling = PreserveReferencesHandling.None;

    public void setStringEscapeHandling(StringEscapeHandling handling) {
        this.stringEscapeHandling = handling;
    }

    public StringEscapeHandling getStringEscapeHandling() {
        return stringEscapeHandling;
    }

    public void setPreserveReferencesHandling(PreserveReferencesHandling handling) {
        this.preserveReferencesHandling = handling;
    }

    public PreserveReferencesHandling getPreserveReferencesHandling() {
        return preserveReferencesHandling;
    }
}
