using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;

namespace Catalogue.Web.Endpoints;

/// <summary>Company accounts: platform verification (KYB) and each company's own users.</summary>
public static class OrganizationEndpoints
{
    public sealed record OrganizationDto(long Id, string Name, string? VatNumber, string Country, bool CanBuy, bool CanSell, string Status,
        bool IsPlatformOwner, long? SupplierId, string? SupplierCode, long? CustomerAccountId, string? CustomerAccountNumber,
        int UserCount, DateTime CreatedAtUtc, DateTime? ReviewedAtUtc, string? ReviewedBy, string? ReviewNote);
    public sealed record ApproveRequest(long? PriceListId, string? SupplierCode);
    public sealed record ReviewRequest(string? Note);
    public sealed record OrgUserDto(long Id, string Email, string? Username, string DisplayName, IReadOnlyList<string> Roles,
        string Status, DateTime? LastLoginUtc, DateTime? LockoutEndUtc);
    public sealed record AddUserRequest(string Email, string DisplayName, string Password, IReadOnlyList<string> Roles);

    public static void Map(IEndpointRouteBuilder api)
    {
        var orgs = api.MapGroup("/organizations").WithTags("Organizations");

        orgs.MapGet("", async (ICatalogueDbContext db, string? status, CancellationToken ct) =>
        {
            var q = db.Organizations.AsNoTracking();
            if (Enum.TryParse<OrganizationStatus>(status, true, out var st)) q = q.Where(o => o.Status == st);
            var rows = await q.OrderBy(o => o.Status).ThenByDescending(o => o.CreatedAtUtc)
                .Select(o => new OrganizationDto(o.Id, o.Name, o.VatNumber, o.Country,
                    ((int)o.Capabilities & 1) != 0, ((int)o.Capabilities & 2) != 0, o.Status.ToString(), o.IsPlatformOwner,
                    o.SupplierId, o.Supplier != null ? o.Supplier.Code : null, o.CustomerAccountId,
                    o.CustomerAccount != null ? o.CustomerAccount.AccountNumber : null,
                    db.Users.Count(u => u.OrganizationId == o.Id), o.CreatedAtUtc, o.ReviewedAtUtc, o.ReviewedBy, o.ReviewNote))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).RequireAuthorization("PlatformAdmin");

        // Approval creates the company's marketplace identities: a Supplier (to sell) and/or a CustomerAccount (to buy).
        orgs.MapPost("/{id:long}/approve", async (long id, ApproveRequest req, ICatalogueDbContext db, IActorContext actor,
            IReferenceDataProvider refData, HybridCache cache, CancellationToken ct) =>
        {
            var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (org is null) return Results.NotFound();
            if (org.Status == OrganizationStatus.Active) return Results.Conflict(new { error = "Company is already active." });

            long? supplierId = org.SupplierId, customerId = org.CustomerAccountId;
            if (org.CanSell && supplierId is null)
            {
                var code = await UniqueSupplierCodeAsync(db, req.SupplierCode ?? org.Name, ct);
                var supplier = new Supplier(code, org.Name, IntegrationType.Manual);
                db.Suppliers.Add(supplier);
                await db.SaveChangesAsync(ct);
                supplierId = supplier.Id;
            }
            if (org.CanBuy && customerId is null)
            {
                var priceList = req.PriceListId ?? await db.PriceLists.Where(p => p.Code == "TRADE").Select(p => (long?)p.Id).FirstOrDefaultAsync(ct);
                var account = new CustomerAccount($"ACC-{org.Id:D5}", org.Name, priceList);
                db.CustomerAccounts.Add(account);
                await db.SaveChangesAsync(ct);
                customerId = account.Id;
            }
            org.Approve(actor.ActorId, supplierId, customerId);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "CompanyApproved", "Organization", org.Id.ToString(),
                $"{org.Name}: supplier={supplierId} customer={customerId}", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            await cache.RemoveByTagAsync(CacheTags.Suppliers, ct);
            refData.Invalidate();
            return Results.Ok(new { org.Id, status = org.Status.ToString(), supplierId, customerAccountId = customerId });
        }).RequireAuthorization("PlatformAdmin");

        orgs.MapPost("/{id:long}/reject", (long id, ReviewRequest req, ICatalogueDbContext db, IActorContext actor, CancellationToken ct)
            => Review(id, db, actor, ct, o => o.Reject(actor.ActorId, req.Note), "CompanyRejected", req.Note)).RequireAuthorization("PlatformAdmin");
        orgs.MapPost("/{id:long}/suspend", (long id, ReviewRequest req, ICatalogueDbContext db, IActorContext actor, CancellationToken ct)
            => Review(id, db, actor, ct, o => o.Suspend(actor.ActorId, req.Note), "CompanySuspended", req.Note)).RequireAuthorization("PlatformAdmin");
        orgs.MapPost("/{id:long}/reactivate", (long id, ICatalogueDbContext db, IActorContext actor, CancellationToken ct)
            => Review(id, db, actor, ct, o => o.Reactivate(actor.ActorId), "CompanyReactivated", null)).RequireAuthorization("PlatformAdmin");

        orgs.MapGet("/{id:long}/users", (long id, ICatalogueDbContext db, CancellationToken ct) => Users(id, db, ct))
            .RequireAuthorization("PlatformAdmin");

        // ----- the signed-in user's own company -----
        orgs.MapGet("/me", async (CurrentUser me, ICatalogueDbContext db, CancellationToken ct) =>
        {
            if (me.OrganizationId is not { } oid) return Results.NotFound(new { error = "You are not a member of a company." });
            var org = await db.Organizations.AsNoTracking().Include(o => o.Supplier).Include(o => o.CustomerAccount).ThenInclude(c => c!.PriceList)
                .FirstAsync(o => o.Id == oid, ct);
            return Results.Ok(new
            {
                org.Id, org.Name, org.VatNumber, org.Country, org.CanBuy, org.CanSell, status = org.Status.ToString(), org.IsPlatformOwner,
                supplier = org.Supplier is null ? null : new { org.Supplier.Id, org.Supplier.Code, org.Supplier.Name },
                customerAccount = org.CustomerAccount is null ? null : new
                {
                    org.CustomerAccount.Id, org.CustomerAccount.AccountNumber, priceList = org.CustomerAccount.PriceList?.Code
                },
                users = (await Users(oid, db, ct) as IValueHttpResult)?.Value
            });
        }).RequireAuthorization();

        orgs.MapPost("/me/users", async (AddUserRequest req, CurrentUser me, ICatalogueDbContext db, IPasswordHasher<AppUser> hasher,
            IActorContext actor, CancellationToken ct) =>
        {
            if (me.OrganizationId is not { } oid || !me.IsInRole(AppRoles.OrgAdmin)) return Results.Forbid();
            var org = await db.Organizations.FirstAsync(o => o.Id == oid, ct);
            var allowed = new List<string> { AppRoles.OrgAdmin };
            if (org.CanBuy) allowed.Add(AppRoles.Buyer);
            if (org.CanSell) allowed.Add(AppRoles.Seller);
            var bad = req.Roles.Except(allowed).ToList();
            if (bad.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["roles"] = [$"Not allowed for this company: {string.Join(", ", bad)}"] });
            var email = req.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Email == email, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["An account with this email already exists."] });
            var problems = PasswordPolicy.Check(req.Password, email);
            if (problems.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = problems.ToArray() });
            var user = new AppUser(email, req.DisplayName, req.Roles, oid);
            user.SetPasswordHash(hasher.HashPassword(user, req.Password));
            db.Users.Add(user);
            db.AuditEvents.Add(new AuditEvent(actor.ActorId, "UserAdded", "Organization", oid.ToString(), $"{email} [{string.Join(',', req.Roles)}]", actor.CorrelationId));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/me/users/{user.Id}", new { user.Id });
        }).RequireAuthorization();
    }

    private static async Task<IResult> Users(long orgId, ICatalogueDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Users.AsNoTracking().Where(u => u.OrganizationId == orgId).OrderBy(u => u.Email).ToListAsync(ct))
            .Select(u => new OrgUserDto(u.Id, u.Email, u.Username, u.DisplayName, u.RoleList, u.Status.ToString(), u.LastLoginUtc, u.LockoutEndUtc)));

    private static async Task<IResult> Review(long id, ICatalogueDbContext db, IActorContext actor, CancellationToken ct,
        Action<Organization> change, string action, string? note)
    {
        var org = await db.Organizations.FirstOrDefaultAsync(o => o.Id == id, ct);
        if (org is null) return Results.NotFound();
        if (org.IsPlatformOwner) return Results.Conflict(new { error = "The platform owner company cannot be changed here." });
        change(org);
        if (org.Status is OrganizationStatus.Suspended or OrganizationStatus.Rejected)
        {
            // End every session of the company's users immediately.
            var userIds = await db.Users.Where(u => u.OrganizationId == id).Select(u => u.Id).ToListAsync(ct);
            var tokens = await db.RefreshTokens.Where(t => userIds.Contains(t.UserId) && t.RevokedAtUtc == null).ToListAsync(ct);
            foreach (var t in tokens) t.Revoke(action);
        }
        db.AuditEvents.Add(new AuditEvent(actor.ActorId, action, "Organization", id.ToString(), $"{org.Name}{(note is null ? "" : ": " + note)}", actor.CorrelationId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { org.Id, status = org.Status.ToString() });
    }

    private static async Task<string> UniqueSupplierCodeAsync(ICatalogueDbContext db, string seed, CancellationToken ct)
    {
        var baseCode = new string(seed.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        if (baseCode.Length < 3) baseCode = "SUP" + baseCode;
        baseCode = baseCode[..Math.Min(10, baseCode.Length)];
        var code = baseCode;
        for (var i = 2; await db.Suppliers.AnyAsync(s => s.Code == code, ct); i++) code = $"{baseCode[..Math.Min(8, baseCode.Length)]}{i}";
        return code;
    }
}
