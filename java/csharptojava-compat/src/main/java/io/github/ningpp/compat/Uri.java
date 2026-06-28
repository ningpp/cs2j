package io.github.ningpp.compat;

import java.net.URI;

public final class Uri {
    private final URI value;

    public Uri(String uriString) {
        this.value = URI.create(uriString);
    }

    public static boolean isWellFormedUriString(String uriString, UriKind uriKind) {
        if (uriString == null) {
            return false;
        }

        try {
            URI parsed = new URI(uriString);
            return switch (uriKind) {
                case Absolute -> parsed.isAbsolute();
                case Relative -> !parsed.isAbsolute();
                case RelativeOrAbsolute -> true;
            };
        } catch (Exception ex) {
            return false;
        }
    }

    @Override
    public String toString() {
        return value.toString();
    }
}
