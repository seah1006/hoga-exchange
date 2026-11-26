using Microsoft.EntityFrameworkCore;
namespace Hoga.Server;
internal static class AccountEndpoints
{
    internal static void MapAccounts(this WebApplication app)
    {
        var accounts = app.MapGroup("/api/account");
        accounts.MapGet("", async (HttpContext c, MarketLoop m) => Members.Id(c) is { } id
            ? Results.Ok(await m.Account(id, c.RequestAborted)) : Members.Error(401, "Unauthorized"));
        accounts.MapGet("/orders", async (HttpContext c, MarketLoop m) => Members.Id(c) is { } id
            ? Results.Ok(await m.MemberOrders(id, c.RequestAborted)) : Members.Error(401, "Unauthorized"));
        accounts.MapGet("/trades", async (HttpContext c, MarketLoop m) => Members.Id(c) is { } id
            ? Results.Ok(await m.MemberTrades(id, c.RequestAborted)) : Members.Error(401, "Unauthorized"));
        var ranking = app.MapGroup("/api/ranking");
        ranking.MapGet("", async (HttpContext c, MarketLoop market, MarketDb db) =>
        {
            var snapshot = await market.Ranking(c.RequestAborted);
            var ids = snapshot.Entries.Take(50).Select(a => a.Id).ToArray();
            var names = await db.Users.Where(u => ids.Contains(u.Id)).Select(u => new { u.Id, u.Nickname })
                .ToDictionaryAsync(u => u.Id, u => u.Nickname, c.RequestAborted);
            return View(snapshot.Round, snapshot.UpdatedAtUtc, snapshot.Entries.Select((a, i) => new RoundEntry(
                snapshot.Generation, i + 1, a.Id, names.GetValueOrDefault(a.Id, Members.DeletedNickname), a.ReturnBp, a.Valuation)), Members.Id(c));
        });
        ranking.MapGet("/rounds", async (MarketLoop market, MarketDb db, CancellationToken ct) =>
        {
            var current = await market.Ranking(ct);
            return Results.Ok(await db.Rounds.Where(r => r.Generation != current.Generation).Select(r => r.Round)
                .Distinct().OrderByDescending(r => r).Take(30).ToArrayAsync(ct));
        });
        ranking.MapGet("/{round}", async (string round, HttpContext c, MarketLoop market, MarketDb db) =>
        {
            var current = await market.Ranking(c.RequestAborted);
            var saved = await db.Rounds.AsNoTracking().Where(r => r.Round == round && r.Generation != current.Generation)
                .OrderByDescending(r => r.EndedAtUtc).ThenByDescending(r => r.Generation)
                .FirstOrDefaultAsync(c.RequestAborted);
            if (saved is null) return Members.Error(404, "NotFound");
            var mine = Members.Id(c);
            var entries = await db.Set<RoundEntry>().AsNoTracking().Where(e => e.Generation == saved.Generation &&
                (e.Rank <= 50 || e.MemberId == mine)).OrderBy(e => e.Rank).ToArrayAsync(c.RequestAborted);
            return View(saved.Round, DateTime.SpecifyKind(saved.EndedAtUtc, DateTimeKind.Utc), entries, mine);
        });
    }
    private static IResult View(string round, DateTime updatedAtUtc, IEnumerable<RoundEntry> records, string? id)
    {
        var entries = records.Take(50).Select(e => new { e.Rank, e.Nickname, e.ReturnBp, e.Valuation }).ToArray();
        var member = records.FirstOrDefault(e => e.MemberId == id);
        return Results.Ok(new { round, updatedAtUtc, entries,
            me = member is null ? null : new { member.Rank, member.ReturnBp, member.Valuation } });
    }
}
