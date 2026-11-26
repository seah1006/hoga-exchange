using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
namespace Hoga.Server;
internal sealed class Member : IdentityUser
{
    public string Nickname { get; set; } = null!;
    public string NormalizedNickname { get; set; } = null!;
}
internal sealed record LoginAudit(long Id, DateTime AtUtc, bool Succeeded, string IpPrefix, string? MemberId);
internal sealed class RoundResult
{
    public string Generation { get; set; } = null!;
    public string Round { get; set; } = null!;
    public DateTime EndedAtUtc { get; set; }
    public List<RoundEntry> Entries { get; set; } = [];
}
internal sealed record RoundEntry(string Generation, int Rank, string MemberId, string Nickname, long ReturnBp, long Valuation);
internal sealed class MarketDb(DbContextOptions<MarketDb> options) : IdentityDbContext<Member>(options)
{
    public DbSet<LoginAudit> LoginAudits => Set<LoginAudit>();
    public DbSet<RoundResult> Rounds => Set<RoundResult>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);
        model.Entity<Member>().HasIndex(x => x.NormalizedNickname).IsUnique();
        model.Entity<LoginAudit>().HasKey(x => x.Id);
        model.Entity<LoginAudit>().HasIndex(x => x.AtUtc);
        model.Entity<LoginAudit>().HasOne<Member>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<RoundResult>().HasKey(x => x.Generation);
        model.Entity<RoundResult>().HasIndex(x => x.Round);
        model.Entity<RoundResult>().HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.Generation);
        model.Entity<RoundEntry>().HasKey(x => new { x.Generation, x.Rank });
    }
}
