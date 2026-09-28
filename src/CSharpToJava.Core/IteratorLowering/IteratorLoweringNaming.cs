namespace CSharpToJava.Core.IteratorLowering;

/// <summary>
/// Deterministic naming utilities for iterator state machine types, fields, and labels.
/// Mirrors the collision-avoidance pattern from LocalFunctionLiftNaming but does not share code.
/// </summary>
internal static class IteratorLoweringNaming
{
    public const string StateField = "_cs2jState";
    public const string CurrentField = "_cs2jCurrent";
    public const string InitialThreadIdField = "_cs2jInitialThreadId";
    public const string ThisField = "_cs2jThis";
    private const string CaptureFieldPrefix = "_cs2jCap_";
    private const string LocalFieldPrefix = "_cs2jLoc_";
    private const string ResumeLabelPrefix = "_cs2jResume_";
    private const string StateMachineClassPrefix = "Cs2jIterator_";

    public static string GetStateMachineClassName(string memberName, int index, HashSet<string> usedNames)
    {
        var baseName = $"{StateMachineClassPrefix}{memberName}_{index}";
        return ResolveUniqueName(baseName, usedNames);
    }

    public static string GetCaptureFieldName(string originalName) => CaptureFieldPrefix + originalName;

    public static string GetLocalFieldName(string originalName) => LocalFieldPrefix + originalName;

    public static string GetResumeLabel(int state) => $"{ResumeLabelPrefix}{state}";

    public static string ResolveUniqueName(string baseName, HashSet<string> usedNames)
    {
        var name = baseName;
        var suffix = 2;
        while (usedNames.Contains(name))
        {
            name = baseName + suffix;
            suffix++;
        }
        usedNames.Add(name);
        return name;
    }
}
