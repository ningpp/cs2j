package dotnet.system;

/**
 * System.Uri compatibility class (minimal surface used by generated XAML code).
 */
public class Uri {

    private final String originalString;

    public Uri(String uriString) {
        this(uriString, UriKind.RelativeOrAbsolute);
    }

    public Uri(String uriString, UriKind uriKind) {
        this.originalString = uriString;
    }

    public String getOriginalString() {
        return originalString;
    }

    @Override
    public String toString() {
        return originalString;
    }
}
