using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace twoSaaSCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImmutableSQLTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE dbo.DocumentAuditLog
(
    AuditLogId       BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ActionUtc        DATETIME2(7) NOT NULL CONSTRAINT DF_DocumentAuditLog_ActionUtc DEFAULT SYSUTCDATETIME(),
    TenantId         UNIQUEIDENTIFIER NOT NULL,
    RoomId           UNIQUEIDENTIFIER NULL,
    FileId           UNIQUEIDENTIFIER NULL,
    UserId           NVARCHAR(450) NULL,
    Action           NVARCHAR(32) NOT NULL,
    FileName         NVARCHAR(512) NULL,
    FileSize         BIGINT NULL,
    FileSha256       CHAR(64) NULL,
    IpAddress        VARCHAR(64) NULL,
    UserAgent        NVARCHAR(256) NULL,
    CorrelationId    UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_DocumentAuditLog_Correlation DEFAULT NEWID(),
    ExtraJson        NVARCHAR(MAX) NULL,
    SrcExt           NVARCHAR(16) NULL
)
WITH ( LEDGER = ON ( APPEND_ONLY = ON ) );
");
            migrationBuilder.Sql("CREATE INDEX IX_DocumentAuditLog_Tenant_File ON dbo.DocumentAuditLog(TenantId, FileId);");
            migrationBuilder.Sql("CREATE INDEX IX_DocumentAuditLog_ActionUtc ON dbo.DocumentAuditLog(ActionUtc);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS dbo.DocumentAuditLog;");

        }
    }
}
