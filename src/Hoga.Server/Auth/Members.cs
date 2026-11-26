using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Hoga.Server;
internal static partial class Members
{
    internal const string Admin = "Admin";
    internal const string DeletedNickname = "탈퇴한 사용자";
    internal static void AddMembers(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(services =>
        {
            var root = services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
            var config = services.GetRequiredService<IConfiguration>();
            return new StoragePaths(Path.GetFullPath(config["Market:DatabasePath"] ?? "data/hoga.db", root),
                Path.GetFullPath(config["Market:KeysPath"] ?? "data/keys", root));
        });
        builder.Services.AddOptions<MarketOptions>().Validate(o => o.InitialCash >= 0 && o.InitialShares >= 0 &&
            (Int128)o.InitialCash + (Int128)o.InitialShares * o.BasePrice > 0 &&
            (Int128)o.InitialCash + (Int128)o.InitialShares * o.BasePrice <= long.MaxValue,
            "시작 자산의 평가액이 올바르지 않습니다.");
        builder.Services.AddDbContext<MarketDb>((services, options) => options.UseSqlite(new SqliteConnectionStringBuilder
        { DataSource = services.GetRequiredService<StoragePaths>().Database }.ToString()));
        builder.Services.AddDataProtection().SetApplicationName("Hoga.Server");
        builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<StoragePaths, ILoggerFactory>((options, paths, logs) => options.XmlRepository =
                new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(new(paths.Keys), logs));
        builder.Services.AddIdentityCore<Member>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.User.AllowedUserNameCharacters = null!;
            options.Password.RequiredLength = 10; // 임의 지정 (계약 v0.5)
            options.Password.RequireLowercase = options.Password.RequireUppercase = options.Password.RequireDigit =
                options.Password.RequireNonAlphanumeric = false;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        }).AddRoles<IdentityRole>().AddEntityFrameworkStores<MarketDb>();
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        builder.Services.AddAuthorization();
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Events.OnValidatePrincipal = async context =>
            {
                var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<Member>>();
                var principal = context.Principal;
                if (principal is null || await users.GetUserAsync(principal) is not { } member ||
                    member.SecurityStamp != principal.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType)) context.RejectPrincipal();
            };
        });
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        builder.Services.AddRateLimiter(options => options.AddPolicy("signup", context =>
        {
            var ip = context.Connection.RemoteIpAddress;
            if (ip?.IsIPv4MappedToIPv6 == true) ip = ip.MapToIPv4();
            return RateLimitPartition.GetFixedWindowLimiter(ip?.ToString() ?? "unknown", _ => new()
            { Window = TimeSpan.FromHours(1), PermitLimit = 10 }); // 임의 지정
        }));
    }
    internal static async Task<bool> InitializeMembers(this WebApplication app, string[] args)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var paths = services.GetRequiredService<StoragePaths>();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.Database)!);
        Directory.CreateDirectory(paths.Keys);
        await services.GetRequiredService<MarketDb>().Database.MigrateAsync();
        if (args.FirstOrDefault() != "create-admin") return false;
        if (args is not [_, "--email", var email, "--nickname", var nickname])
        { Console.Error.WriteLine("사용법: create-admin --email <e> --nickname <n>"); Environment.ExitCode = 2; return true; }
        var password = ReadPassword();
        var users = services.GetRequiredService<UserManager<Member>>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(Admin) && !(await roles.CreateAsync(new(Admin))).Succeeded)
        { Environment.ExitCode = 1; return true; }
        var member = New(email, nickname.Trim());
        if (Validate(email, password, member.Nickname) is not null || !(await users.CreateAsync(member, password)).Succeeded)
        { Console.Error.WriteLine("관리자 계정을 만들 수 없습니다."); Environment.ExitCode = 1; return true; }
        if (!(await users.AddToRoleAsync(member, Admin)).Succeeded)
        { await users.DeleteAsync(member); Environment.ExitCode = 1; return true; }
        Console.WriteLine("관리자 계정을 만들었습니다.");
        return true;
    }
    private static string ReadPassword()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return buffer.ToString();
            if (key.Key == ConsoleKey.Backspace && buffer.Length > 0) buffer.Length--;
            else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
    }
    internal static IResult Error(int status, string error) => Results.Json(new { error }, statusCode: status);
    internal static string? Id(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    internal static async ValueTask<IResult?> Csrf(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue("X-CSRF-TOKEN", out var header) || header.Count != 1 || string.IsNullOrWhiteSpace(header[0])) return Error(400, "BadCsrfToken");
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); return null; }
        catch (AntiforgeryValidationException) { return Error(400, "BadCsrfToken"); }
    }
    internal static async ValueTask<object?> AdminFilter(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var c = invocation.HttpContext;
        var key = c.RequestServices.GetRequiredService<IConfiguration>()["Market:AdminKey"];
        if (!string.IsNullOrWhiteSpace(key) && c.Request.Headers.TryGetValue("X-Admin-Key", out var headers) && headers.Count == 1 &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)),
                SHA256.HashData(Encoding.UTF8.GetBytes(headers[0]!)))) return await next(invocation);
        if (!c.User.IsInRole(Admin)) return Error(Id(c) is null ? 401 : 403, "Unauthorized");
        return await Csrf(c) is { } failure ? failure : await next(invocation);
    }
    internal sealed record StoragePaths(string Database, string Keys);
}
