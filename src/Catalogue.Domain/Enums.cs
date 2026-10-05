namespace Catalogue.Domain;

/// <summary>Stored as int: append new values only.</summary>
public enum ProductStatus { Draft, Active, Inactive, PendingApproval }

public enum ImportStatus
{
    Received,
    Processing,
    Completed,
    CompletedWithErrors,
    Failed,
    Rejected,
    Retrying
}

public enum IntegrationType { Csv, Edi, Rest, Manual }

public static class Roles
{
    public const string Admin = "Admin";
    public const string SupplierManager = "SupplierManager";
    public const string Buyer = "Buyer";
    public const string Support = "Support";
    public const string Viewer = "Viewer";
}

public static class ImportFormats
{
    public const string Csv = "CSV";
    public const string Edi = "EDI";
    /// <summary>Large EDI interchange fanned out over Event Hubs in chunks.</summary>
    public const string EdiStream = "EDI-STREAM";
}
