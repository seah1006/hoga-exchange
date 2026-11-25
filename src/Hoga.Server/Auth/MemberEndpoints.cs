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
        auth.MapPost("/login", Login);
        auth.MapPost("/logout", async (HttpContext c) =>
        {
            if (await Csrf(c) is { } failure) return failure;
            await c.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.NoContent();
        });
        auth.MapGet("/me", async (HttpContext c, UserManager<Member> users) => await users.GetUserAsync(c.User) is { } user
            ? Results.Ok(new { user.Id, user.Nickname, user.Email, isAdmin = c.User.IsInRole(Admin) }) : Error(401, "Unauthorized"));
        auth.MapDelete("/me", Withdraw);
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
            if (!(await users.CreateAsync(member, password)).Succeeded) return Error(409, "EmailTaken");
        }
        catch (DbUpdateException e) when (e.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        { return Error(409, await users.FindByEmailAsync(email) is not null ? "EmailTaken" : "NicknameTaken"); }
        await SignIn(c, member, claims);
        return Results.Json(View(member, false), statusCode: 201);
    }

    private static readonly SemaphoreSlim LoginGate = new(1); // Serialize Identity lockout counter updates.
    private static async Task<IResult> Login(HttpContext c, UserManager<Member> users, MarketDb db, TimeProvider time,
        IUserClaimsPrincipalFactory<Member> claims)
    {
        if (await Csrf(c) is { } failure) return failure;
        if (await Body<Credentials>(c) is not { } request) return Error(400, "BadRequest");
        await LoginGate.WaitAsync(c.RequestAborted);
        try
        {
            var user = await users.FindByEmailAsync(request.Email?.Trim() ?? "");
            var locked = user is not null && await users.IsLockedOutAsync(user);
            var success = !locked && user is not null && await users.CheckPasswordAsync(user, request.Password ?? "");
            if (user is not null && !locked)
            {
                if (success) await users.ResetAccessFailedCountAsync(user);
                else { await users.AccessFailedAsync(user); locked = await users.IsLockedOutAsync(user); }
            }
            var ip = c.Connection.RemoteIpAddress;
            if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
            var bytes = ip?.GetAddressBytes();
            var prefix = bytes is null ? "unknown" : Convert.ToHexStringLower(bytes.AsSpan(0, bytes.Length == 4 ? 3 : 6)); // /24, /48 (임의 지정)
            db.LoginAudits.Add(new(0, time.GetUtcNow().UtcDateTime, success, prefix));
            await db.SaveChangesAsync();
            if (!success) return locked ? Error(429, "LockedOut") : Error(401, "InvalidCredentials");
            await SignIn(c, user!, claims);
            return Results.Ok(View(user!, await users.IsInRoleAsync(user!, Admin)));
        }
        finally { LoginGate.Release(); }
    }

    private static async Task<IResult> Withdraw(HttpContext c, UserManager<Member> users, MarketLoop market)
    {
        if (await users.GetUserAsync(c.User) is not { } user) return Error(401, "Unauthorized");
        if (await Csrf(c) is { } failure) return failure;
        if (await Body<Credentials>(c) is not { Password: { } password } || !await users.CheckPasswordAsync(user, password))
            return Error(401, "InvalidCredentials");
        if (!await market.Withdraw(user.Id, c.RequestAborted)) return Error(503, "ServiceUnavailable");
        await c.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.NoContent();
    }
    private sealed record Credentials(string? Email, string? Password, string? Nickname);
}
