using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Hoga.Server;
internal sealed record RankingSnapshot(string Generation, string Round, DateTime UpdatedAtUtc, RankCandidate[] Entries);
internal sealed partial class MarketLoop
{
    private AccountLedger _ledger = null!;
    private string _round = null!;
    private RankingSnapshot _ranking = null!;
    private long _lastRanking;
    private readonly Dictionary<string, string> _online = new(StringComparer.Ordinal);
    private readonly HashSet<string> _withdrawn = new(StringComparer.Ordinal);
    private string Generation => _header.MarketGeneration?.ToString("N") ?? "legacy";
    private string RoundPath => _path + "." + Generation + ".round";
    private sealed record RoundMetadata(string Round, long InitialCash, long InitialShares);

    private void LoadRound(DateTimeOffset now)
    {
        RoundMetadata metadata;
        if (File.Exists(RoundPath))
            metadata = JsonSerializer.Deserialize<RoundMetadata>(File.ReadAllText(RoundPath))
                ?? throw new InvalidDataException("Missing round metadata.");
        else
        {
            var options = configuration.Value;
            metadata = new(RoundAt(now), options.InitialCash, options.InitialShares);
            var staged = RoundPath + ".tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize(metadata));
            File.Move(staged, RoundPath);
        }
        _round = metadata.Round;
        _ledger = new(metadata.InitialCash, metadata.InitialShares, _header.BasePrice);
    }
    private string RoundAt(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul"));
        var reset = MarketOptions.TryParseResetTime(configuration.Value.DailyResetLocalTime, out var clock) ? clock : new TimeOnly(5, 0);
        return local.Date.AddDays(local.TimeOfDay < reset.ToTimeSpan() ? -1 : 0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
    private AccountSnapshot AccountView(string id) => _ledger.Snapshot(id, _round, _engine.DynamicReferencePrice);
    internal Task<AccountSnapshot> Account(string id, CancellationToken ct) => Read(() => AccountView(id), ct);
    internal Task<AccountOrder[]> MemberOrders(string id, CancellationToken ct) => Read(() => _ledger.Orders(id, _projection.Vi is not null), ct);
    internal Task<AccountTrade[]> MemberTrades(string id, CancellationToken ct) => Read(() => _ledger.Trades(id), ct);
    internal Task<RankingSnapshot> Ranking(CancellationToken ct) => Read(() => _ranking, ct);
    internal Task<bool> Unsubscribe(string id) => Read(() => _online.Remove(id), CancellationToken.None);

    private async Task PublishAccount(string id)
    {
        var connections = _online.Where(p => p.Value == id).Select(p => p.Key).ToArray();
        if (connections.Length == 0) return;
        using var send = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        send.CancelAfter(TimeSpan.FromSeconds(1));
        try { await hub.Clients.Clients(connections).SendAsync("accountUpdate", AccountView(id), send.Token); }
        catch (Exception ex) { logger.LogWarning(ex, "Account update delivery failed"); }
    }
    private void RefreshRanking(DateTimeOffset now)
    {
        _lastRanking = time.GetTimestamp();
        _ranking = new(Generation, _round, now.UtcDateTime, _ledger.Standings(_engine.DynamicReferencePrice));
    }
    private async Task<bool> SaveRound(DateTimeOffset now)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketDb>();
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(_stopping);
            // A staged result for the live generation is hidden by read APIs until rotation succeeds.
            await db.Rounds.Where(r => r.Generation == Generation).ExecuteDeleteAsync(_stopping);
            var standings = _ledger.Standings(_engine.DynamicReferencePrice);
            var names = await db.Users.Select(u => new { u.Id, u.Nickname }).ToDictionaryAsync(u => u.Id, u => u.Nickname, _stopping);
            var result = new RoundResult { Generation = Generation, Round = _round, EndedAtUtc = now.UtcDateTime,
                Entries = standings.Select((a, i) => new RoundEntry(Generation, i + 1, a.Id,
                    names.GetValueOrDefault(a.Id, Members.DeletedNickname), a.ReturnBp, a.Valuation)).ToList() };
            db.Rounds.Add(result);
            await db.SaveChangesAsync(_stopping);
            await transaction.CommitAsync(_stopping);
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        { logger.LogWarning("Round result could not be saved"); return false; }
    }
    internal Task<bool> Withdraw(string id, CancellationToken ct) => Invoke(async () =>
    {
        if (_withdrawn.Contains(id)) return true;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MarketDb>();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Member>>();
        var user = await users.FindByIdAsync(id);
        if (user is null) return true;
        foreach (var orderId in _ledger.OpenOrders(id).ToArray())
            await Process(new Hoga.Engine.CancelOrder(orderId, checked(_seq + 1), LogicalNow()));
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(_stopping);
            await db.Set<RoundEntry>().Where(e => e.MemberId == id)
                .ExecuteUpdateAsync(e => e.SetProperty(x => x.Nickname, Members.DeletedNickname), _stopping);
            if (!(await users.DeleteAsync(user)).Succeeded) return false;
            await transaction.CommitAsync(_stopping);
        }
        catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        { logger.LogWarning("Account removal could not be saved"); return false; }
        _withdrawn.Add(id);
        foreach (var key in _online.Where(p => p.Value == id).Select(p => p.Key).ToArray()) _online.Remove(key);
        return true;
    }, ct);
}
