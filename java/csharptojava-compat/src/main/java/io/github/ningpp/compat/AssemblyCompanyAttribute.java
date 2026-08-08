package io.github.ningpp.compat;

/**
 * Compatibility class for C# System.Reflection.AssemblyCompanyAttribute.
 * Used only for reflection (e.g. CustomAttributeBuilder construction), so it
 * exposes the C#-style constructor and a getter for the company value.
 */
public class AssemblyCompanyAttribute {

    private final String company;

    public AssemblyCompanyAttribute(String company) {
        this.company = company;
    }

    public String getCompany() {
        return company;
    }
}
