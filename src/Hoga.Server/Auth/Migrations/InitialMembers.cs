using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Hoga.Server;

// Immutable baseline generated from the SQLite provider's model DDL.
[DbContext(typeof(MarketDb))]
[Migration("20261125000100_InitialMembers")]
internal sealed class InitialMembers : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.Sql("""
            CREATE TABLE "AspNetRoles" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetRoles" PRIMARY KEY,
                "Name" TEXT NULL,
                "NormalizedName" TEXT NULL,
                "ConcurrencyStamp" TEXT NULL
            );
            """);
        migration.Sql("""
            CREATE TABLE "AspNetUsers" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AspNetUsers" PRIMARY KEY,
                "Nickname" TEXT NOT NULL,
                "NormalizedNickname" TEXT NOT NULL,
                "UserName" TEXT NULL,
                "NormalizedUserName" TEXT NULL,
                "Email" TEXT NULL,
                "NormalizedEmail" TEXT NULL,
                "EmailConfirmed" INTEGER NOT NULL,
                "PasswordHash" TEXT NULL,
                "SecurityStamp" TEXT NULL,
                "ConcurrencyStamp" TEXT NULL,
                "PhoneNumber" TEXT NULL,
                "PhoneNumberConfirmed" INTEGER NOT NULL,
                "TwoFactorEnabled" INTEGER NOT NULL,
                "LockoutEnd" TEXT NULL,
                "LockoutEnabled" INTEGER NOT NULL,
                "AccessFailedCount" INTEGER NOT NULL
            );
            """);
        migration.Sql("""
            CREATE TABLE "LoginAudits" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_LoginAudits" PRIMARY KEY AUTOINCREMENT,
                "AtUtc" TEXT NOT NULL,
                "Succeeded" INTEGER NOT NULL,
                "IpPrefix" TEXT NOT NULL
            );
            """);
        migration.Sql("""
            CREATE TABLE "Rounds" (
                "Generation" TEXT NOT NULL CONSTRAINT "PK_Rounds" PRIMARY KEY,
                "Round" TEXT NOT NULL,
                "EndedAtUtc" TEXT NOT NULL
            );
            """);
        migration.Sql("""
            CREATE TABLE "AspNetRoleClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetRoleClaims" PRIMARY KEY AUTOINCREMENT,
                "RoleId" TEXT NOT NULL,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL,
                CONSTRAINT "FK_AspNetRoleClaims_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE
            );
            """);
        migration.Sql("""
            CREATE TABLE "AspNetUserClaims" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AspNetUserClaims" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "ClaimType" TEXT NULL,
                "ClaimValue" TEXT NULL,
                CONSTRAINT "FK_AspNetUserClaims_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            """);
        migration.Sql("""
            CREATE TABLE "AspNetUserLogins" (
                "LoginProvider" TEXT NOT NULL,
                "ProviderKey" TEXT NOT NULL,
                "ProviderDisplayName" TEXT NULL,
                "UserId" TEXT NOT NULL,
                CONSTRAINT "PK_AspNetUserLogins" PRIMARY KEY ("LoginProvider", "ProviderKey"),
                CONSTRAINT "FK_AspNetUserLogins_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            """);
        migration.Sql("""
            CREATE TABLE "AspNetUserRoles" (
                "UserId" TEXT NOT NULL,
                "RoleId" TEXT NOT NULL,
                CONSTRAINT "PK_AspNetUserRoles" PRIMARY KEY ("UserId", "RoleId"),
                CONSTRAINT "FK_AspNetUserRoles_AspNetRoles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "AspNetRoles" ("Id") ON DELETE CASCADE,
                CONSTRAINT "FK_AspNetUserRoles_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            """);
        migration.Sql("""
            CREATE TABLE "AspNetUserTokens" (
                "UserId" TEXT NOT NULL,
                "LoginProvider" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Value" TEXT NULL,
                CONSTRAINT "PK_AspNetUserTokens" PRIMARY KEY ("UserId", "LoginProvider", "Name"),
                CONSTRAINT "FK_AspNetUserTokens_AspNetUsers_UserId" FOREIGN KEY ("UserId") REFERENCES "AspNetUsers" ("Id") ON DELETE CASCADE
            );
            """);
        migration.Sql("""
            CREATE TABLE "RoundEntry" (
                "Generation" TEXT NOT NULL,
                "Rank" INTEGER NOT NULL,
                "MemberId" TEXT NOT NULL,
                "Nickname" TEXT NOT NULL,
                "ReturnBp" INTEGER NOT NULL,
                "Valuation" INTEGER NOT NULL,
                CONSTRAINT "PK_RoundEntry" PRIMARY KEY ("Generation", "Rank"),
                CONSTRAINT "FK_RoundEntry_Rounds_Generation" FOREIGN KEY ("Generation") REFERENCES "Rounds" ("Generation") ON DELETE CASCADE
            );
            """);
        migration.Sql("""
            CREATE INDEX "IX_AspNetRoleClaims_RoleId" ON "AspNetRoleClaims" ("RoleId");
            """);
        migration.Sql("""
            CREATE UNIQUE INDEX "RoleNameIndex" ON "AspNetRoles" ("NormalizedName");
            """);
        migration.Sql("""
            CREATE INDEX "IX_AspNetUserClaims_UserId" ON "AspNetUserClaims" ("UserId");
            """);
        migration.Sql("""
            CREATE INDEX "IX_AspNetUserLogins_UserId" ON "AspNetUserLogins" ("UserId");
            """);
        migration.Sql("""
            CREATE INDEX "IX_AspNetUserRoles_RoleId" ON "AspNetUserRoles" ("RoleId");
            """);
        migration.Sql("""
            CREATE INDEX "EmailIndex" ON "AspNetUsers" ("NormalizedEmail");
            """);
        migration.Sql("""
            CREATE UNIQUE INDEX "IX_AspNetUsers_NormalizedNickname" ON "AspNetUsers" ("NormalizedNickname");
            """);
        migration.Sql("""
            CREATE UNIQUE INDEX "UserNameIndex" ON "AspNetUsers" ("NormalizedUserName");
            """);
        migration.Sql("""
            CREATE INDEX "IX_Rounds_Round" ON "Rounds" ("Round");
            """);
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.DropTable("AspNetRoleClaims");
        migration.DropTable("AspNetUserClaims");
        migration.DropTable("AspNetUserLogins");
        migration.DropTable("AspNetUserRoles");
        migration.DropTable("AspNetUserTokens");
        migration.DropTable("AspNetRoles");
        migration.DropTable("AspNetUsers");
        migration.DropTable("RoundEntry");
        migration.DropTable("Rounds");
        migration.DropTable("LoginAudits");
    }
}
