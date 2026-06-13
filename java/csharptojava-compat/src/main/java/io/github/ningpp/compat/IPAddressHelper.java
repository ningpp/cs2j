package io.github.ningpp.compat;

import java.net.Inet6Address;
import java.net.InetAddress;

/**
 * Compatibility helper for System.Net.IPAddress conversions.
 * Provides methods that match C# IPAddress behavior for IPv6 loopback
 * and address string representation.
 */
public final class IPAddressHelper {

    private IPAddressHelper() {}

    /**
     * Returns the IPv6 loopback address, equivalent to C# IPAddress.IPv6Loopback.
     * Unlike InetAddress.getLoopbackAddress() which returns the IPv4 loopback,
     * this method returns an Inet6Address representing ::1.
     */
    public static Inet6Address ipv6Loopback() {
        return (Inet6Address) Inet6Address.ofLiteral("::1");
    }

    /**
     * Returns the string representation of an InetAddress in C# IPAddress.ToString() format.
     * For IPv6 addresses, C# uses RFC 5952 compressed notation (e.g. "::1") while Java's
     * getHostAddress() returns full notation (e.g. "0:0:0:0:0:0:0:1").
     * This method converts to C# compatible compressed format.
     */
    public static String toString(InetAddress addr) {
        if (addr instanceof Inet6Address ipv6) {
            return compressIPv6(ipv6.getHostAddress());
        }
        return addr.getHostAddress();
    }

    /**
     * Compresses a full IPv6 address string to RFC 5952 compressed notation.
     * E.g. "0:0:0:0:0:0:0:1" -> "::1", "0:0:0:0:0:0:0:0" -> "::"
     */
    static String compressIPv6(String fullAddr) {
        // Handle scope IDs (e.g. "0:0:0:0:0:0:0:1%eth0")
        String scopeId = "";
        int scopeIdx = fullAddr.indexOf('%');
        if (scopeIdx >= 0) {
            scopeId = fullAddr.substring(scopeIdx);
            fullAddr = fullAddr.substring(0, scopeIdx);
        }

        String[] parts = fullAddr.split(":");
        if (parts.length < 8) {
            return fullAddr + scopeId; // Not a full address, return as-is
        }

        // Find the longest run of consecutive zero groups
        int bestStart = -1, bestLen = 0;
        int curStart = -1, curLen = 0;
        for (int i = 0; i < 8; i++) {
            if (parts[i].equals("0")) {
                if (curStart < 0) curStart = i;
                curLen++;
                if (curLen > bestLen) {
                    bestStart = curStart;
                    bestLen = curLen;
                }
            } else {
                curStart = -1;
                curLen = 0;
            }
        }

        if (bestLen < 2) {
            return fullAddr + scopeId; // No compression needed
        }

        // Build compressed form
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < bestStart; i++) {
            if (i > 0) sb.append(':');
            sb.append(parts[i]);
        }
        sb.append("::");
        for (int i = bestStart + bestLen; i < 8; i++) {
            if (i > bestStart + bestLen) sb.append(':');
            sb.append(parts[i]);
        }

        return sb.toString() + scopeId;
    }
}
