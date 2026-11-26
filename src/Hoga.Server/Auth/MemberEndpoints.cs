using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hoga.Server;
internal static partial class Members
{
    private static Member New(string email, string nickname) => new()
    {
        Id = "u-" + Guid.NewGuid().ToString("N"), Email = email, UserName = email,
        Nickname = nickname, NormalizedNickname = nickname.Normalize().ToUpperInvariant()
    };
    private static string? Validate(string email, string password, string nickname) =>
        email.Length is 0 or > 254 || !new EmailAddressAttribute().IsValid(email) ? "InvalidEmail" :
        password.Length < 10 ? "WeakPassword" : nickname.Length is < 2 or > 16 || nickname.Any(char.IsControl) ||
            nickname == DeletedNickname ? "InvalidNickname" : null;
    private static async Task<T?> Body<T>(HttpContext context) where T : class
    {
        if (!context.Request.HasJsonContentType()) return null;
        try { return await context.Request.ReadFromJsonAsync<T>(context.RequestAborted); }
        catch (Exception e) when (e is JsonException or BadHttpRequestException) { return null; }
    }
    private static async Task SignIn(HttpContext context, Member member, IUserClaimsPrincipalFactory<Member> claims) =>
        await context.SignInAsync(IdentityConstants.ApplicationScheme, await claims.CreateAsync(member), new AuthenticationProperties { IsPersistent = true });
    private static object View(Member user, bool isAdmin) => new { user.Id, user.Nickname, isAdmin };

    internal static void MapMembers(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapGet("/csrf", (HttpContext c, IAntiforgery a) => Results.Ok(new { token = a.GetAndStoreTokens(c).RequestToken }));
        auth.MapPost("/register", Register).RequireRateLimiting("signup");
        auth.MapPost("/login", Login).RequireRateLimiting("login");
        auth.MapPost("/logout", async (HttpContext c, UserManager<Member> users, MarketLoop market) =>
        {
            if (await Csrf(c) is { } failure) return failure;
            if (await users.GetUserAsync(c.User) is { } member)
            {
                if (!(await users.UpdateSecurityStampAsync(member)).Succeeded) return Error(503, "ServiceUnavailable");
                await market.RevokeAccountSubscriptions(member.Id);
            }
            await c.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.NoContent();
        });
        auth.MapGet("/me", async (HttpContext c, UserManager<Member> users) => await users.GetUserAsync(c.User) is { } user
            ? Results.Ok(new { user.Id, user.Nickname, user.Email, isAdmin = c.User.IsInRole(Admin) }) : Error(401, "Unauthorized"));
        auth.MapDelete("/me", Withdraw).RequireRateLimiting("withdraw");
    }

    private static async Task<IResult> Register(HttpContext c, UserManager<Member> users, MarketDb db,
        IUserClaimsPrincipalFactory<Member> claims)
    {
        if (await Csrf(c) is { } failure) return failure;
        if (await Body<Credentials>(c) is not { } request) return Error(400, "BadRequest");
        var email = request.Email?.Trim() ?? "";
        var password = request.Password ?? "";
        var nickname = request.Nickname?.Trim() ?? "";
        if (Validate(email, password, nickname) is { } invalid) return Error(400, invalid);
        var member = New(email, nickname);
        if (await users.FindByEmailAsync(email) is not null) return Error(409, "EmailTaken");
        if (await db.Users.AnyAsync(u => u.NormalizedNickname == member.NormalizedNickname)) return Error(409, "NicknameTaken");
        try
        {
            var created = await users.CreateAsync(member, password);
            if (!created.Succeeded) return created.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName")
                ? Error(409, "EmailTaken") : Error(400, "BadRequest");
        }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        { return Error(409, await users.FindByEmailAsync(email) is not null ? "EmailTaken" : "NicknameTaken"); }
        await SignIn(c, member, claims);
        return Results.Json(View(member, false), statusCode: 201);
    }

    private static async Task<IResult> Login(HttpContext c, UserManager<Member> users, MarketDb db, TimeProvider time,
        IUserClaimsPrincipalFactory<Member> claims, IPasswordHasher<Member> hasher, CredentialAttempts attempts)
    {
        if (await Csrf(c) is { } failure) return failure;
        if (await Body<Credentials>(c) is not { } request) return Error(400, "BadRequest");
        var email = request.Email?.Trim() ?? "";
        var password = request.Password ?? "";
        if (email.Length > 254 || password.Length > 1024) return Error(401, "InvalidCredentials");
        var key = users.NormalizeEmail(email) ?? "";
        using var quota = attempts.Acquire(key);
        if (!quota.IsAcquired) return Error(429, "TooManyRequests");
        using var slot = attempts.AcquireHash();
        if (!slot.IsAcquired) return Error(429, "TooManyRequests");
        var stripe = attempts.Stripe(key);
        await stripe.WaitAsync(c.RequestAborted);
        try
        {
            var user = await users.FindByEmailAsync(email);
            var locked = user is not null && await users.IsLockedOutAsync(user);
            var success = false;
            if (user is null || locked)
                _ = hasher.VerifyHashedPassword(new Member(), attempts.DummyHash, password);
            else
            {
                success = await users.CheckPasswordAsync(user, password);
                if (success) await users.ResetAccessFailedCountAsync(user);
                else await users.AccessFailedAsync(user);
            }
            var bytes = ClientIp(c)?.GetAddressBytes();
            var prefix = bytes is null ? "unknown" : Convert.ToHexStringLower(bytes.AsSpan(0, bytes.Length == 4 ? 3 : 6));
            db.LoginAudits.Add(new(0, time.GetUtcNow().UtcDateTime, success, prefix, user?.Id));
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: 787 })
            { return Error(401, "InvalidCredentials"); }
            if (!success) return Error(401, "InvalidCredentials");
            await SignIn(c, user!, claims);
            return Results.Ok(View(user!, await users.IsInRoleAsync(user!, Admin)));
        }
        finally { stripe.Release(); }
    }

    private static async Task<IResult> Withdraw(HttpContext c, UserManager<Member> users, MarketLoop market, CredentialAttempts attempts)
    {
        if (await users.GetUserAsync(c.User) is not { } user) return Error(401, "Unauthorized");
        if (await Csrf(c) is { } failure) return failure;
        using var quota = attempts.Acquire(user.Id, withdraw: true);
        using var slot = attempts.AcquireHash();
        if (!quota.IsAcquired || !slot.IsAcquired) return Error(429, "TooManyRequests");
        if (await Body<Credentials>(c) is not { Password: { Length: <= 1024 } password } || !await users.CheckPasswordAsync(user, password))
            return Error(401, "InvalidCredentials");
        if (!await market.Withdraw(user.Id, c.RequestAborted)) return Error(503, "ServiceUnavailable");
        await c.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.NoContent();
    }
    private sealed record Credentials(string? Email, string? Password, string? Nickname);
}
