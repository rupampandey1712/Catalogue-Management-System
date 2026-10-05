namespace Catalogue.Domain;

/// <summary>What a registered company does on the platform. A merchant is usually both.</summary>
[Flags]
public enum OrganizationCapability { None = 0, Buyer = 1, Seller = 2 }

public enum OrganizationStatus { PendingVerification, Active, Suspended, Rejected }

/// <summary>
/// A B2B company account. Every company can sell (it is linked to a <see cref="Supplier"/> whose offers
/// appear in the catalogue) and/or buy (it is linked to a <see cref="CustomerAccount"/> whose price lists
/// and supplier discounts apply). New registrations wait for platform verification (KYB).
/// </summary>
public sealed class Organization
{
    public long Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string? VatNumber { get; private set; }
    public string Country { get; private set; } = "GB";
    public OrganizationCapability Capabilities { get; private set; }
    public OrganizationStatus Status { get; private set; } = OrganizationStatus.PendingVerification;
    /// <summary>The company that runs the platform (it buys and sells too).</summary>
    public bool IsPlatformOwner { get; private set; }
    public long? SupplierId { get; private set; }
    public Supplier? Supplier { get; private set; }
    public long? CustomerAccountId { get; private set; }
    public CustomerAccount? CustomerAccount { get; private set; }
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? ReviewedAtUtc { get; private set; }
    public string? ReviewedBy { get; private set; }
    public string? ReviewNote { get; private set; }

    private Organization() { }

    public Organization(string name, OrganizationCapability capabilities, string country, string? vatNumber, bool isPlatformOwner = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (capabilities == OrganizationCapability.None) throw new ArgumentException("A company must buy, sell or both.", nameof(capabilities));
        Name = name.Trim();
        Capabilities = capabilities;
        Country = string.IsNullOrWhiteSpace(country) ? "GB" : country.Trim().ToUpperInvariant();
        VatNumber = string.IsNullOrWhiteSpace(vatNumber) ? null : vatNumber.Trim().ToUpperInvariant();
        IsPlatformOwner = isPlatformOwner;
    }

    public bool CanBuy => Capabilities.HasFlag(OrganizationCapability.Buyer);
    public bool CanSell => Capabilities.HasFlag(OrganizationCapability.Seller);

    public void Approve(string reviewer, long? supplierId, long? customerAccountId)
    {
        if (Status is OrganizationStatus.Active) throw new InvalidOperationException("Company is already active.");
        if (CanSell && supplierId is null) throw new InvalidOperationException("A selling company needs a supplier record.");
        if (CanBuy && customerAccountId is null) throw new InvalidOperationException("A buying company needs a customer account.");
        SupplierId = supplierId;
        CustomerAccountId = customerAccountId;
        Status = OrganizationStatus.Active;
        Review(reviewer, null);
    }

    public void Reject(string reviewer, string? note) { Status = OrganizationStatus.Rejected; Review(reviewer, note); }
    public void Suspend(string reviewer, string? note) { Status = OrganizationStatus.Suspended; Review(reviewer, note); }
    public void Reactivate(string reviewer)
    {
        if (Status != OrganizationStatus.Suspended) throw new InvalidOperationException("Only suspended companies can be reactivated.");
        Status = OrganizationStatus.Active;
        Review(reviewer, null);
    }

    /// <summary>Seeding helper: link existing supplier/customer records and activate.</summary>
    public void LinkAndActivate(long? supplierId, long? customerAccountId)
    {
        SupplierId = supplierId;
        CustomerAccountId = customerAccountId;
        Status = OrganizationStatus.Active;
        ReviewedAtUtc = DateTime.UtcNow;
        ReviewedBy = "seed";
    }

    private void Review(string reviewer, string? note)
    {
        ReviewedBy = reviewer;
        ReviewedAtUtc = DateTime.UtcNow;
        ReviewNote = note;
    }
}

public enum UserStatus { Active, Disabled }

/// <summary>A person who signs in. Platform staff have no organization; company users belong to one.</summary>
public sealed class AppUser
{
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public long Id { get; private set; }
    public string Email { get; private set; } = null!;
    /// <summary>Optional short sign-in name (the seeded demo accounts use "admin", "buyer"...).</summary>
    public string? Username { get; private set; }
    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public long? OrganizationId { get; private set; }
    public Organization? Organization { get; private set; }
    /// <summary>Comma-separated roles: platform (Admin, Support, SupplierManager, Viewer) or company (OrgAdmin, Buyer, Seller).</summary>
    public string Roles { get; private set; } = "";
    public UserStatus Status { get; private set; } = UserStatus.Active;
    public int FailedLogins { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }
    public DateTime? LastLoginUtc { get; private set; }
    /// <summary>Changes on password change/disable: every refresh token minted before it is invalid.</summary>
    public string SecurityStamp { get; private set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;

    private AppUser() { }

    public AppUser(string email, string displayName, IEnumerable<string> roles, long? organizationId, string? username = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        Email = email.Trim().ToLowerInvariant();
        DisplayName = displayName.Trim();
        Username = string.IsNullOrWhiteSpace(username) ? null : username.Trim().ToLowerInvariant();
        OrganizationId = organizationId;
        SetRoles(roles);
    }

    public IReadOnlyList<string> RoleList => Roles.Split(',', StringSplitOptions.RemoveEmptyEntries);
    public string SignInName => Username ?? Email;

    public void SetRoles(IEnumerable<string> roles) => Roles = string.Join(',', roles.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct());
    public void SetPasswordHash(string hash)
    {
        PasswordHash = hash;
        SecurityStamp = Guid.NewGuid().ToString("N");
    }

    public bool IsLockedOut(DateTime now) => LockoutEndUtc is { } end && end > now;

    public void LoginFailed(DateTime now)
    {
        FailedLogins++;
        if (FailedLogins >= MaxFailedLogins)
        {
            LockoutEndUtc = now.Add(LockoutDuration);
            FailedLogins = 0;
        }
    }

    public void LoginSucceeded(DateTime now)
    {
        FailedLogins = 0;
        LockoutEndUtc = null;
        LastLoginUtc = now;
    }

    public void Disable() { Status = UserStatus.Disabled; SecurityStamp = Guid.NewGuid().ToString("N"); }
    public void Enable() => Status = UserStatus.Active;
}

/// <summary>
/// Rotating refresh token (only its SHA-256 is stored). Each use replaces it; presenting an already
/// rotated token means it was stolen, so the whole family is revoked.
/// </summary>
public sealed class RefreshToken
{
    public long Id { get; private set; }
    public long UserId { get; private set; }
    public AppUser? User { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public Guid FamilyId { get; private set; }
    public string SecurityStamp { get; private set; } = null!;
    public DateTime CreatedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string? RevokedReason { get; private set; }
    public string? CreatedByIp { get; private set; }

    private RefreshToken() { }

    public RefreshToken(long userId, string tokenHash, Guid familyId, string securityStamp, DateTime expiresAtUtc, string? ip)
    {
        UserId = userId;
        TokenHash = tokenHash;
        FamilyId = familyId;
        SecurityStamp = securityStamp;
        ExpiresAtUtc = expiresAtUtc;
        CreatedByIp = ip;
    }

    public bool IsActive(DateTime now) => RevokedAtUtc is null && ExpiresAtUtc > now;
    public void Revoke(string reason) { RevokedAtUtc ??= DateTime.UtcNow; RevokedReason ??= reason; }
}

public static class PasswordPolicy
{
    /// <summary>Returns the problems with a proposed password (empty = acceptable).</summary>
    public static IReadOnlyList<string> Check(string? password, string? email = null)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < 10) problems.Add("Use at least 10 characters.");
        if (password is not null)
        {
            if (!password.Any(char.IsUpper)) problems.Add("Add an upper-case letter.");
            if (!password.Any(char.IsLower)) problems.Add("Add a lower-case letter.");
            if (!password.Any(char.IsDigit)) problems.Add("Add a digit.");
            if (email is not null && email.Split('@')[0] is { Length: >= 3 } local && password.Contains(local, StringComparison.OrdinalIgnoreCase))
                problems.Add("Don't include your email name in the password.");
        }
        return problems;
    }
}
