using System.Net.Mail;
using Catalogue.Application;
using Catalogue.Domain;
using Catalogue.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Catalogue.Web.Endpoints;

/// <summary>
/// Sign-in, company registration, token refresh and password management. Passwords are PBKDF2-hashed
/// (ASP.NET Core Identity's hasher); access tokens live 30 minutes; refresh tokens rotate and live in
/// an httpOnly, SameSite=Strict cookie scoped to /api/auth.
/// </summary>
public static class AuthEndpoints
{
    public sealed record LoginRequest(string Username, string Password);
    public sealed record LoginResponse(string Token, DateTime ExpiresAtUtc, UserProfile User,
        string Username, IReadOnlyList<string> Roles, long? CustomerId, string? CustomerName);
    public sealed record RegisterRequest(string CompanyName, string? VatNumber, string? Country, bool WantsToBuy, bool WantsToSell,
        string DisplayName, string Email, string Password);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    // Verifying against a dummy hash for unknown users keeps response timing uniform (no user enumeration).
    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(null!, "not-a-real-password-1A");

    public static void Map(IEndpointRouteBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Auth");

        auth.MapPost("/login", async (LoginRequest req, HttpContext http, ICatalogueDbContext db, IPasswordHasher<AppUser> hasher,
            TokenService tokens, JwtSettings jwt, TimeProvider clock, CancellationToken ct) =>
        {
            var key = (req.Username ?? "").Trim().ToLowerInvariant();
            var user = await db.Users.Include(u => u.Organization)
                .FirstOrDefaultAsync(u => u.Email == key || u.Username == key, ct);
            var now = clock.GetUtcNow().UtcDateTime;

            if (user is null)
            {
                hasher.VerifyHashedPassword(null!, DummyHash, req.Password ?? "");
                return Results.Problem(statusCode: 401, title: "Invalid email/username or password.");
            }
            if (user.IsLockedOut(now))
                return Results.Problem(statusCode: 423, title: "Account temporarily locked",
                    detail: $"Too many failed attempts. Try again after {user.LockoutEndUtc:HH:mm} UTC.");

            var verdict = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "");
            if (verdict == PasswordVerificationResult.Failed)
            {
                user.LoginFailed(now);
                await db.SaveChangesAsync(ct);
                return Results.Problem(statusCode: 401, title: "Invalid email/username or password.");
            }
            if (verdict == PasswordVerificationResult.SuccessRehashNeeded) user.SetPasswordHash(hasher.HashPassword(user, req.Password!));
            if (user.Status != UserStatus.Active)
                return Results.Problem(statusCode: 403, title: "This account is disabled.");
            if (user.Organization is { } org && org.Status != OrganizationStatus.Active)
                return Results.Problem(statusCode: 403, title: org.Status == OrganizationStatus.PendingVerification
                        ? "Your company registration is awaiting verification." : $"Your company account is {org.Status.ToString().ToLowerInvariant()}.",
                    detail: org.ReviewNote, extensions: new Dictionary<string, object?> { ["code"] = org.Status.ToString() });

            user.LoginSucceeded(now);
            db.AuditEvents.Add(new AuditEvent(user.SignInName, "UserSignedIn", "User", user.Id.ToString(), http.Connection.RemoteIpAddress?.ToString(), null));
            await db.SaveChangesAsync(ct);
            return await IssueSessionAsync(http, user, null, db, tokens, jwt, ct);
        }).AllowAnonymous().RequireRateLimiting(Policies.Login);

        auth.MapPost("/register", async (RegisterRequest req, HttpContext http, ICatalogueDbContext db,
            IPasswordHasher<AppUser> hasher, CancellationToken ct) =>
        {
            var errors = new Dictionary<string, string[]>();
            var email = (req.Email ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(req.CompanyName) || req.CompanyName.Trim().Length < 2) errors["companyName"] = ["Company name is required."];
            if (!req.WantsToBuy && !req.WantsToSell) errors["capabilities"] = ["Choose buying, selling or both."];
            if (string.IsNullOrWhiteSpace(req.DisplayName)) errors["displayName"] = ["Your name is required."];
            if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email || email.Length > 256) errors["email"] = ["Enter a valid email address."];
            var pw = PasswordPolicy.Check(req.Password, email);
            if (pw.Count > 0) errors["password"] = pw.ToArray();
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            if (await db.Users.AnyAsync(u => u.Email == email, ct))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["An account with this email already exists."] });

            var caps = (req.WantsToBuy ? OrganizationCapability.Buyer : 0) | (req.WantsToSell ? OrganizationCapability.Seller : 0);
            var org = new Organization(req.CompanyName, caps, req.Country ?? "GB", req.VatNumber);
            db.Organizations.Add(org);
            await db.SaveChangesAsync(ct);

            var roles = new List<string> { AppRoles.OrgAdmin };
            if (req.WantsToBuy) roles.Add(AppRoles.Buyer);
            if (req.WantsToSell) roles.Add(AppRoles.Seller);
            var user = new AppUser(email, req.DisplayName, roles, org.Id);
            user.SetPasswordHash(hasher.HashPassword(user, req.Password));
            db.Users.Add(user);
            db.AuditEvents.Add(new AuditEvent(email, "CompanyRegistered", "Organization", org.Id.ToString(),
                $"{org.Name} ({caps}) from {http.Connection.RemoteIpAddress}", null));
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/organizations/{org.Id}", new
            {
                organizationId = org.Id,
                status = org.Status.ToString(),
                message = "Thanks - your company is registered. A platform administrator will verify it before you can sign in."
            });
        }).AllowAnonymous().RequireRateLimiting(Policies.Register);

        auth.MapPost("/refresh", async (HttpContext http, TokenService tokens, ICatalogueDbContext db, JwtSettings jwt, CancellationToken ct) =>
        {
            if (!http.Request.Cookies.TryGetValue(TokenService.RefreshCookie, out var raw) || string.IsNullOrEmpty(raw))
                return Results.Problem(statusCode: 401, title: "No session.");
            var (outcome, user, next) = await tokens.RotateAsync(raw, http.Connection.RemoteIpAddress?.ToString(), ct);
            if (outcome != TokenService.RefreshOutcome.Ok || user is null || next is null)
            {
                TokenService.ClearCookie(http);
                if (outcome == TokenService.RefreshOutcome.Reused && user is not null)
                {
                    db.AuditEvents.Add(new AuditEvent(user.SignInName, "RefreshTokenReuseDetected", "User", user.Id.ToString(),
                        $"All sessions in the family revoked. IP {http.Connection.RemoteIpAddress}", null));
                    await db.SaveChangesAsync(ct);
                }
                return Results.Problem(statusCode: 401, title: "Session expired. Please sign in again.");
            }
            return await IssueSessionAsync(http, user, next, db, tokens, jwt, ct);
        }).AllowAnonymous();

        auth.MapPost("/logout", async (HttpContext http, TokenService tokens, CancellationToken ct) =>
        {
            if (http.Request.Cookies.TryGetValue(TokenService.RefreshCookie, out var raw) && !string.IsNullOrEmpty(raw))
                await tokens.RevokeAsync(raw, "logout", ct);
            TokenService.ClearCookie(http);
            return Results.NoContent();
        }).AllowAnonymous();

        auth.MapGet("/me", async (CurrentUser me, ICatalogueDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == me.UserId, ct);
            return user is null ? Results.Unauthorized() : Results.Ok(await ProfileBuilder.BuildAsync(db, user, ct));
        }).RequireAuthorization();

        auth.MapPost("/change-password", async (ChangePasswordRequest req, HttpContext http, CurrentUser me, ICatalogueDbContext db,
            IPasswordHasher<AppUser> hasher, TokenService tokens, JwtSettings jwt, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == me.UserId, ct);
            if (user is null) return Results.Unauthorized();
            if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword ?? "") == PasswordVerificationResult.Failed)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currentPassword"] = ["Current password is incorrect."] });
            var problems = PasswordPolicy.Check(req.NewPassword, user.Email);
            if (problems.Count > 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["newPassword"] = problems.ToArray() });

            user.SetPasswordHash(hasher.HashPassword(user, req.NewPassword)); // new security stamp: other sessions end
            var sessions = await db.RefreshTokens.Where(t => t.UserId == user.Id && t.RevokedAtUtc == null).ToListAsync(ct);
            foreach (var t in sessions) t.Revoke("password-changed");
            db.AuditEvents.Add(new AuditEvent(user.SignInName, "PasswordChanged", "User", user.Id.ToString(), null, null));
            await db.SaveChangesAsync(ct);
            return await IssueSessionAsync(http, user, null, db, tokens, jwt, ct);
        }).RequireAuthorization();
    }

    private static async Task<IResult> IssueSessionAsync(HttpContext http, AppUser user, string? refresh, ICatalogueDbContext db,
        TokenService tokens, JwtSettings jwt, CancellationToken ct)
    {
        var org = user.OrganizationId is { } oid ? await db.Organizations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == oid, ct) : null;
        var (token, expires) = tokens.CreateAccessToken(user, org);
        refresh ??= await tokens.IssueRefreshTokenAsync(user, null, http.Connection.RemoteIpAddress?.ToString(), ct);
        TokenService.SetCookie(http, refresh, jwt.RefreshTokenLifetime);
        var profile = await ProfileBuilder.BuildAsync(db, user, ct);
        return Results.Ok(new LoginResponse(token, expires, profile, profile.Username, profile.Roles, profile.CustomerId, profile.CustomerName));
    }
}
