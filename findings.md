# Findings: MSAGL Conversion Research

## Project Structure
- Source: C:\automatic-graph-layout-master\GraphLayout\MSAGL
- Project file: AutomaticGraphLayout.csproj
- File count: 492 .cs files
- Subdirs: Core, DebugHelpers, GraphmapsWithMesh, Layout, Miscellaneous, Routing
- Destination: C:\agl\v20260314 (with Maven structure: src/main/java)

## Converter Tool
- CLI at: d:\code\CSharpToJavaConverter\src\CSharpToJava.CLI\Program.cs
- Java25 is supported as a java-version value
- Maven POM generation is enabled via --generate-pom flag (default = true)
- Generated structure: destination/src/main/java/... (when --generate-pom)
- pom.xml is generated at destination root

## Java Environment
- TBD (check with `java -version`)

## Compilation Error Patterns
(To be filled as errors are found)

## Fixes Applied to Converter
(To be filled as fixes are made)
