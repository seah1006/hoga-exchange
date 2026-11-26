using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hoga.Server;

[DbContext(typeof(MarketDb))]
[Migration("20261126000100_LoginAuditRetention")]
internal sealed class LoginAuditRetention : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.Sql("ALTER TABLE LoginAudits ADD COLUMN MemberId TEXT REFERENCES AspNetUsers(Id) ON DELETE CASCADE;");
        migration.Sql("DELETE FROM LoginAudits;"); // Pre-migration rows cannot be attributed to a member.
        migration.Sql("CREATE INDEX IX_LoginAudits_AtUtc ON LoginAudits(AtUtc);");
        migration.Sql("CREATE INDEX IX_LoginAudits_MemberId ON LoginAudits(MemberId);");
    }
    protected override void Down(MigrationBuilder migration)
    {
        migration.Sql("DROP INDEX IX_LoginAudits_MemberId;");
        migration.Sql("DROP INDEX IX_LoginAudits_AtUtc;");
        migration.Sql("ALTER TABLE LoginAudits DROP COLUMN MemberId;");
    }
}
