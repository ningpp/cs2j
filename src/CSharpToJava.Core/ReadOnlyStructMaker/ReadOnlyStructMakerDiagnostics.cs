namespace CSharpToJava.Core.ReadOnlyStructMaker;

public enum ReadOnlyStructSeverity { Info, Warning, Error }

public enum StructPattern
{
    FullImmutable,               // A
    AlreadyReadonly,             // B
    PrivateSetter,               // C
    DataContainer,               // D
    PublicFields,                // E
    MutableMethods,              // F
    MutableMethodsNonMigratable, // G
}

public enum ConversionLevel
{
    Skip,                // L0
    DirectAdd,           // L1
    PropertyConvert,     // L2
    DataContainer,       // L3
    PublicFieldToProperty, // L4
    MethodMigrate,       // L5
    NotConvertible,      // L7
}

public enum MigrationType
{
    VoidToStruct,
    ThisToStruct,
    OtherReturnToOut,
}

public sealed record ReadOnlyStructMakerDiagnostic(
    ReadOnlyStructSeverity Severity,
    string StructName,
    string Reason,
    ConversionLevel? Level,
    StructPattern? Pattern,
    string? FilePath = null,
    int? Line = null);

public sealed class ReadOnlyStructMakerStatistics
{
    public int StructsScanned;
    public int StructsConverted;
    public int StructsSkipped;
    public int StructsFailed;
    public int Level0_Skipped;
    public int Level1_DirectAdd;
    public int Level2_PropertyConvert;
    public int Level3_DataContainer;
    public int Level4_PublicFieldToProperty;
    public int Level5_MethodMigrate;
    public int MethodsMigrated;
    public int CallSitesUpdated;
    public int PublicFieldsConverted;
    public int Failed_RefThisEscape;
    public int Failed_VirtualOrInterface;
    public int Failed_DelegateReferenced;
    public int Failed_ComplexMutableState;
    public int Failed_NameConflict;
    public int Failed_RefFieldMutation;
}

public sealed record ReadOnlyStructMakerResult(
    string? OutputCode,
    bool Changed,
    IReadOnlyDictionary<string, string> ChangedFiles,
    IReadOnlyList<ReadOnlyStructMakerDiagnostic> Diagnostics,
    ReadOnlyStructMakerStatistics Statistics);
