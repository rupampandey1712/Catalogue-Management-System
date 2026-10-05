using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Catalogue.Application;
using Catalogue.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Catalogue.Web.Security;

public static class AppRoles
{
    // Platform staff
    public const string Admin = "Admin";
    public const string Support = "Support";
    public const string SupplierManager = "SupplierManager";
    public const string Viewer = "Viewer";
    // Company members
    public const string OrgAdmin = "OrgAdmin";
    public const string Buyer = "Buyer";
    public const string Seller = "Seller";
}

public static class AppClaims
{
    public const string UserId = "uid";
    public const string OrgId = "org_id";
    public const string OrgName = "org_name";
    public const string OrgCapabilities = "org_caps";
    public const string CustomerId = "customer_id";
    public const string SupplierId = "supplier_id";
}

public sealed record OrgInfo(long Id, string Name, bool CanBuy, bool CanSell, string Status, bool IsPlatformOwner);
public sealed record UserProfile(long Id, string Username, string DisplayName, string Email, IReadOnlyList<string> Roles,
    OrgInfo? Organization, long? CustomerId, string? CustomerName, long? SupplierId, string? SupplierCode);

/// <summary>JWT settings; refuses to run outside Development with the built-in dev key.</summary>
public sealed class JwtSettings
{
    public const string DevKey = "dev-only-signing-key-change-me-0123456789abcdef";
    public required string Issuer { get; init; }
    public required byte[] Key { get; init; }
    public TimeSpan AccessTokenLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan RefreshTokenLifetime { get; init; } = TimeSpan.FromDays(7);

    public static JwtSettings From(IConfiguration config, IHostEnvironment env)
    {
        var key = config["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!env.IsDevelopment() && !config.GetValue<bool>("Jwt:AllowDevKey"))
                throw new InvalidOperationException("Jwt:SigningKey must be configured (32+ characters) outside Development.");
            key = DevKey;
        }
        if (Encoding.UTF8.GetByteCount(key) < 32) throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        return new JwtSettings
        {
            Issuer = config["Jwt:Issuer"] ?? "catalogue-dev",
            Key = Encoding.UTF8.GetBytes(key),
            AccessTokenLifetime = TimeSpan.FromMinutes(config.GetValue("Jwt:AccessTokenMinutes", 30)),
            RefreshTokenLifetime = TimeSpan.FromDays(config.GetValue("Jwt:RefreshTokenDays", 7))
        };
    }
}

/// <summary>Issues access tokens and manages rotating refresh tokens (stored hashed).</summary>
public sealed class TokenService(JwtSettings settings, ICatalogueDbContext db, TimeProvider clock)
{
    public const string RefreshCookie = "cat_rt";

    public (string Token, DateTime ExpiresAtUtc) CreateAccessToken(AppUser user, Organization? org)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.SignInName),
            new(AppClaims.UserId, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        claims.AddRange(user.RoleList.Select(r => new Claim(ClaimTypes.Role, r)));
        if (org is not null)
        {
            claims.Add(new Claim(AppClaims.OrgId, org.Id.ToString()));
            claims.Add(new Claim(AppClaims.OrgName, org.Name));
            claims.Add(new Claim(AppClaims.OrgCapabilities, ((int)org.Capabilities).ToString()));
            if (org.CustomerAccountId is { } c) claims.Add(new Claim(AppClaims.CustomerId, c.ToString()));
            if (org.SupplierId is { } s) claims.Add(new Claim(AppClaims.SupplierId, s.ToString()));
        }
        var expires = now.Add(settings.AccessTokenLifetime);
        var token = new JwtSecurityToken(settings.Issuer, audience: null, claims, notBefore: now, expires: expires,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(settings.Key), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    /// <summary>Creates a refresh token (new family on login, same family on rotation) and returns the raw value.</summary>
    public async Task<string> IssueRefreshTokenAsync(AppUser user, Guid? family, string? ip, CancellationToken ct)
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.RefreshTokens.Add(new RefreshToken(user.Id, Hash(raw), family ?? Guid.NewGuid(), user.SecurityStamp,
            clock.GetUtcNow().UtcDateTime.Add(settings.RefreshTokenLifetime), ip));
        await db.SaveChangesAsync(ct);
        return raw;
    }

    public enum RefreshOutcome { Ok, Invalid, Reused }

    /// <summary>Validates and rotates. A token that was already rotated means theft: the whole family dies.</summary>
    public async Task<(RefreshOutcome Outcome, AppUser? User, string? NewRefresh)> RotateAsync(string raw, string? ip, CancellationToken ct)
    {
        var hash = Hash(raw);
        var token = await db.RefreshTokens.Include(t => t.User).ThenInclude(u => u!.Organization)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (token?.User is null) return (RefreshOutcome.Invalid, null, null);

        if (token.RevokedAtUtc is not null)
        {
            var family = await db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId && t.RevokedAtUtc == null).ToListAsync(ct);
            foreach (var t in family) t.Revoke("reuse-detected");
            await db.SaveChangesAsync(ct);
            return (RefreshOutcome.Reused, token.User, null);
        }
        var user = token.User;
        if (!token.IsActive(now) || token.SecurityStamp != user.SecurityStamp || user.Status != UserStatus.Active
            || user.Organization is { Status: not OrganizationStatus.Active })
            return (RefreshOutcome.Invalid, user, null);

        token.Revoke("rotated");
        var next = await IssueRefreshTokenAsync(user, token.FamilyId, ip, ct);
        return (RefreshOutcome.Ok, user, next);
    }

    public async Task RevokeAsync(string raw, string reason, CancellationToken ct)
    {
        var hash = Hash(raw);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return;
        var family = await db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId && t.RevokedAtUtc == null).ToListAsync(ct);
        foreach (var t in family) t.Revoke(reason);
        await db.SaveChangesAsync(ct);
    }

    public static void SetCookie(HttpContext http, string raw, TimeSpan lifetime) =>
        http.Response.Cookies.Append(RefreshCookie, raw, new CookieOptions
        {
            HttpOnly = true,                       // invisible to JavaScript (XSS can't steal it)
            Secure = http.Request.IsHttps,         // https outside local dev
            SameSite = SameSiteMode.Strict,        // never sent cross-site (CSRF)
            Path = "/api/auth",                    // only sent to the auth endpoints
            Expires = DateTimeOffset.UtcNow.Add(lifetime)
        });

    public static void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/auth", SameSite = SameSiteMode.Strict, HttpOnly = true });

    public static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
}

/// <summary>Builds the profile the SPA uses to decide what to show.</summary>
public static class ProfileBuilder
{
    public static async Task<UserProfile> BuildAsync(ICatalogueDbContext db, AppUser user, CancellationToken ct)
    {
        Organization? org = user.OrganizationId is { } oid
            ? await db.Organizations.AsNoTracking().Include(o => o.CustomerAccount).Include(o => o.Supplier).FirstOrDefaultAsync(o => o.Id == oid, ct)
            : null;
        return new UserProfile(user.Id, user.SignInName, user.DisplayName, user.Email, user.RoleList,
            org is null ? null : new OrgInfo(org.Id, org.Name, org.CanBuy, org.CanSell, org.Status.ToString(), org.IsPlatformOwner),
            org?.CustomerAccountId, org?.CustomerAccount?.Name, org?.SupplierId, org?.Supplier?.Code);
    }
}

/// <summary>Claims of the signed-in user, for company-scoped endpoints.</summary>
public sealed class CurrentUser(IHttpContextAccessor http)
{
    private ClaimsPrincipal? User => http.HttpContext?.User;
    private long? Long(string type) => long.TryParse(User?.FindFirst(type)?.Value, out var v) ? v : null;

    public long? UserId => Long(AppClaims.UserId);
    public long? OrganizationId => Long(AppClaims.OrgId);
    public long? CustomerAccountId => Long(AppClaims.CustomerId);
    public long? SupplierId => Long(AppClaims.SupplierId);
    public string Name => User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
    public bool IsInRole(string role) => User?.IsInRole(role) == true;
    public bool IsPlatformStaff => IsInRole(AppRoles.Admin) || IsInRole(AppRoles.Support) || IsInRole(AppRoles.SupplierManager);
}

/// <summary>
/// Demo identities, now real database users with hashed passwords. The platform owner company buys
/// and sells like any member; three member companies cover buyer, seller and both; one registration
/// waits for verification so the approval flow has something to show.
/// </summary>
public static class IdentitySeed
{
    public static async Task SeedAsync(ICatalogueDbContext db, IPasswordHasher<AppUser> hasher, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct)) return;

        var trade = await db.PriceLists.FirstOrDefaultAsync(p => p.Code == "TRADE", ct);
        async Task<Supplier> SupplierFor(string code, string name, IntegrationType type)
        {
            var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Code == code, ct);
            if (s is not null) return s;
            s = new Supplier(code, name, type);
            db.Suppliers.Add(s);
            await db.SaveChangesAsync(ct);
            return s;
        }
        async Task<CustomerAccount> CustomerFor(string number, string name)
        {
            var c = await db.CustomerAccounts.FirstOrDefaultAsync(x => x.AccountNumber == number, ct);
            if (c is not null) return c;
            c = new CustomerAccount(number, name, trade?.Id);
            db.CustomerAccounts.Add(c);
            await db.SaveChangesAsync(ct);
            return c;
        }
        async Task<Organization> Org(string name, OrganizationCapability caps, Supplier? s, CustomerAccount? c, bool owner = false, bool active = true)
        {
            var o = new Organization(name, caps, "GB", null, owner);
            if (active) o.LinkAndActivate(s?.Id, c?.Id);
            db.Organizations.Add(o);
            await db.SaveChangesAsync(ct);
            return o;
        }
        void User(string username, string email, string name, string password, string[] roles, long? orgId)
        {
            var u = new AppUser(email, name, roles, orgId, username);
            u.SetPasswordHash(hasher.HashPassword(u, password));
            db.Users.Add(u);
        }

        var both = OrganizationCapability.Buyer | OrganizationCapability.Seller;
        var platform = await Org("Catalogue Direct Ltd", both,
            await SupplierFor("OWN", "Catalogue Direct (own range)", IntegrationType.Manual),
            await CustomerFor("ACC-0001", "Catalogue Direct purchasing"), owner: true);
        var briteheat = await Org("BriteHeat Installers Ltd", OrganizationCapability.Buyer, null, await CustomerFor("ACC-1001", "BriteHeat Installers Ltd"));
        var nwd = await Org("Northwest Distribution Ltd", both,
            await SupplierFor("NWD", "Northwest Distribution (EDI)", IntegrationType.Edi), await CustomerFor("ACC-1004", "Northwest Distribution Ltd"));
        var hvx = await Org("HVAC Express Ltd", OrganizationCapability.Seller, await SupplierFor("HVX", "HVAC Express (EDI)", IntegrationType.Edi), null);
        var pending = await Org("Pennine Plumbing Supplies", both, null, null, active: false);

        // Platform staff (the owner company's people) - the demo passwords are intentionally simple.
        User("admin", "admin@catalogue.test", "Platform Admin", "admin123", [AppRoles.Admin, AppRoles.OrgAdmin, AppRoles.Buyer, AppRoles.Seller], platform.Id);
        User("support", "support@catalogue.test", "Support Engineer", "support123", [AppRoles.Support], platform.Id);
        User("supplier", "suppliers@catalogue.test", "Supplier Manager", "supplier123", [AppRoles.SupplierManager], platform.Id);
        // Member companies
        User("buyer", "buyer@briteheat.test", "Bea Buyer (BriteHeat)", "buyer123", [AppRoles.OrgAdmin, AppRoles.Buyer], briteheat.Id);
        User("seller", "sales@nwd.test", "Sam Seller (NWD)", "seller123", [AppRoles.OrgAdmin, AppRoles.Seller, AppRoles.Buyer], nwd.Id);
        User("hvx", "sales@hvx.test", "Hana (HVAC Express)", "hvx12345", [AppRoles.OrgAdmin, AppRoles.Seller], hvx.Id);
        User("pennine", "owner@pennine.test", "Pat (Pennine Plumbing)", "pennine123", [AppRoles.OrgAdmin, AppRoles.Buyer, AppRoles.Seller], pending.Id);
        await db.SaveChangesAsync(ct);

        // Sellers grant some buyer discounts so "cheapest after discounts" has something to compare.
        var now = DateTime.UtcNow.Date;
        var hvxSupplier = await db.Suppliers.FirstAsync(s => s.Code == "HVX", ct);
        var nwdSupplier = await db.Suppliers.FirstAsync(s => s.Code == "NWD", ct);
        var bh = await db.CustomerAccounts.FirstAsync(c => c.AccountNumber == "ACC-1001", ct);
        db.SupplierDiscounts.AddRange(
            new SupplierDiscount(hvxSupplier.Id, bh.Id, 6m, now, null, "BriteHeat volume agreement"),
            new SupplierDiscount(nwdSupplier.Id, null, 2.5m, now, null, "Marketplace launch discount (all buyers)"));
        await db.SaveChangesAsync(ct);
    }
}
