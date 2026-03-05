using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc; // ADD for ServiceFilterAttribute / filter helpers
using twoSaaSCore.Data;
using twoSaaSCore.Models;
using twoSaaSCore.Services;
using twoSaaSCore.Middleware;
using twoSaaSCore.Filters;
using Syncfusion.Licensing; // add

var builder = WebApplication.CreateBuilder(args);

// Register Syncfusion license (configure in appsettings or user-secrets: "Syncfusion:LicenseKey")
var sfLicense = builder.Configuration["Syncfusion:LicenseKey"];
if (!string.IsNullOrWhiteSpace(sfLicense))
{
    SyncfusionLicenseProvider.RegisterLicense(sfLicense);
}


// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantProvider, HttpContextTenantProvider>();

builder.Services.Configure<AzureBlobOptions>(builder.Configuration.GetSection("AzureBlobs"));
builder.Services.AddSingleton<IFileStorage, AzureBlobFileStorage>();

builder.Services.Configure<AzureBlobOptions>(builder.Configuration.GetSection("AzureBlob"));
builder.Services.AddScoped<IRoomFileCatalog, BlobRoomFileCatalog>();
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders(); // Adds Authenticator + Email + Phone token providers

// Add this registration
builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, TenantClaimsPrincipalFactory>();


// Register the MFA enforcement filter in DI
builder.Services.AddScoped<RequireAnyMfaFilter>();

builder.Services.AddScoped<IAuditLogger, SqlLedgerAuditLogger>();
builder.Services.AddScoped<IRoomPermissionService, RoomPermissionService>();
builder.Services.AddScoped<IRoomInvitationService, RoomInvitationService>();
builder.Services.AddScoped<IRoomQaService, RoomQaService>();

// Azure AI Foundry agent (per-room RAG chat)
builder.Services.Configure<AzureAiOptions>(builder.Configuration.GetSection("AzureAi"));
builder.Services.AddHttpClient();
builder.Services.AddScoped<IRoomAgentService, RoomAgentService>();
builder.Services.AddSingleton<AiIndexingQueue>();
builder.Services.AddSingleton<IAiIndexingQueue>(sp => sp.GetRequiredService<AiIndexingQueue>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AiIndexingQueue>());

// Register SMS sender (Twilio when enabled, otherwise no-op)
var twilioSection = builder.Configuration.GetSection("Twilio");
if (twilioSection.GetValue<bool>("Enable"))
{
    var sid = twilioSection["AccountSid"]!;
    var token = twilioSection["AuthToken"]!;
    var from = twilioSection["FromNumber"]!;
    builder.Services.AddSingleton<ISmsSender>(new TwilioSmsSender(sid, token, from));
}
else
{
    builder.Services.AddSingleton<ISmsSender, NoopSmsSender>();
}

builder.Services.AddRazorPages(options =>
{
});

var app = builder.Build();

// Ensure database exists and all migrations are applied at startup.
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("StartupMigration");

    try
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        db.Database.Migrate();

        // DocumentAuditLog is a raw-SQL table (not managed by EF migrations).
        // Ensure it exists with the full schema on every startup.
        var auditDdl = @"
IF OBJECT_ID(N'dbo.DocumentAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DocumentAuditLog
    (
        AuditLogId   BIGINT         IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ActionUtc    DATETIME2(7)   NOT NULL DEFAULT SYSUTCDATETIME(),
        TenantId     UNIQUEIDENTIFIER NOT NULL,
        RoomId       UNIQUEIDENTIFIER NULL,
        FileId       UNIQUEIDENTIFIER NULL,
        UserId       NVARCHAR(450)  NULL,
        UserEmail    NVARCHAR(256)  NULL,
        Action       NVARCHAR(100)  NOT NULL,
        FileName     NVARCHAR(500)  NULL,
        FileSize     BIGINT         NULL,
        FileSha256   NVARCHAR(128)  NULL,
        IpAddress    NVARCHAR(45)   NULL,
        UserAgent    NVARCHAR(500)  NULL,
        CorrelationId UNIQUEIDENTIFIER NOT NULL,
        ExtraJson    NVARCHAR(MAX)  NULL,
        SrcExt       NVARCHAR(20)   NULL
    );
    CREATE NONCLUSTERED INDEX IX_DocumentAuditLog_Tenant_Room
        ON dbo.DocumentAuditLog (TenantId, RoomId, ActionUtc DESC);
END

IF COL_LENGTH(N'dbo.DocumentAuditLog', N'UserEmail') IS NULL
BEGIN
    ALTER TABLE dbo.DocumentAuditLog ADD UserEmail NVARCHAR(256) NULL;
END";

        db.Database.ExecuteSqlRaw(auditDdl);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred while applying database migrations at startup.");
        throw;
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<SubdomainTenantMiddleware>();

app.MapStaticAssets();
app.MapRazorPages()
   .WithStaticAssets();

app.Run();
