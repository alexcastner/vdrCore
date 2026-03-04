using System;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Data
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        private readonly ITenantProvider _tenantProvider;

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ITenantProvider tenantProvider)
            : base(options)
        {
            _tenantProvider = tenantProvider;
        }

        public DbSet<Tenant> Tenants { get; set; } = null!;
        public DbSet<TenantFile> TenantFiles { get; set; } = null!;
        public DbSet<RoomMember> RoomMembers { get; set; } = null!;
        public DbSet<RoomInvitation> RoomInvitations { get; set; } = null!;
        public DbSet<NdaAcceptance> NdaAcceptances { get; set; } = null!;
        public DbSet<RoomQuestion> RoomQuestions { get; set; } = null!;
        public DbSet<RoomAnswer> RoomAnswers { get; set; } = null!;
        public DbSet<RoomFileRef> RoomFileRefs { get; set; } = null!;
        public DbSet<RoomAgent> RoomAgents { get; set; } = null!;
        public DbSet<RoomWebLink> RoomWebLinks { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Unique index on Tenant.Subdomain
            builder.Entity<Tenant>()
                .HasIndex(t => t.Subdomain)
                .IsUnique();

            // TenantFile config
            builder.Entity<TenantFile>()
                .HasIndex(f => new { f.TenantId, f.BlobName })
                .IsUnique();

            // RoomMember config
            builder.Entity<RoomMember>(e =>
            {
                e.HasIndex(m => new { m.TenantId, m.RoomId, m.UserId, m.FolderPath })
                 .IsUnique()
                 .HasFilter(null);

                e.HasIndex(m => new { m.TenantId, m.UserId });
                e.HasIndex(m => new { m.TenantId, m.RoomId });

                e.Property(m => m.Role)
                 .HasConversion<int>();

                e.Property(m => m.PermissionOverrides)
                 .HasConversion<int?>();

                e.Property(m => m.UserId)
                 .HasMaxLength(450);

                e.Property(m => m.FolderPath)
                 .HasMaxLength(500);

                e.Property(m => m.GrantedByUserId)
                 .HasMaxLength(450);
            });

            // RoomInvitation config
            builder.Entity<RoomInvitation>(e =>
            {
                e.HasIndex(i => i.Token)
                 .IsUnique();

                e.HasIndex(i => new { i.TenantId, i.RoomId, i.Status });

                e.Property(i => i.Email)
                 .HasMaxLength(256);

                e.Property(i => i.Token)
                 .HasMaxLength(64);

                e.Property(i => i.Message)
                 .HasMaxLength(500);

                e.Property(i => i.InvitedByUserId)
                 .HasMaxLength(450);

                e.Property(i => i.AcceptedByUserId)
                 .HasMaxLength(450);

                e.Property(i => i.Role)
                 .HasConversion<int>();

                e.Property(i => i.Status)
                 .HasConversion<int>();
            });

            // NdaAcceptance config
            builder.Entity<NdaAcceptance>(e =>
            {
                e.HasIndex(n => new { n.TenantId, n.RoomId, n.UserId })
                 .IsUnique();

                e.Property(n => n.UserId)
                 .HasMaxLength(450);

                e.Property(n => n.IpAddress)
                 .HasMaxLength(64);
            });

            // RoomQuestion config
            builder.Entity<RoomQuestion>(e =>
            {
                e.HasIndex(q => new { q.TenantId, q.RoomId });

                e.Property(q => q.Subject)
                 .HasMaxLength(200);

                e.Property(q => q.Body)
                 .HasMaxLength(4000);

                e.Property(q => q.AskedByUserId)
                 .HasMaxLength(450);

                e.Property(q => q.AskedByEmail)
                 .HasMaxLength(256);

                e.Property(q => q.AssignedToUserId)
                 .HasMaxLength(450);

                e.Property(q => q.Status)
                 .HasConversion<int>();
            });

            // RoomAnswer config
            builder.Entity<RoomAnswer>(e =>
            {
                e.HasIndex(a => a.QuestionId);

                e.Property(a => a.Body)
                 .HasMaxLength(4000);

                e.Property(a => a.AnsweredByUserId)
                 .HasMaxLength(450);

                e.Property(a => a.AnsweredByEmail)
                 .HasMaxLength(256);

                e.HasOne(a => a.Question)
                 .WithMany()
                 .HasForeignKey(a => a.QuestionId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // RoomFileRef config
            builder.Entity<RoomFileRef>(e =>
            {
                e.HasIndex(r => new { r.TenantId, r.RoomId, r.FileId })
                 .IsUnique();

                e.HasIndex(r => new { r.TenantId, r.RoomId, r.FolderPath });

                e.HasIndex(r => new { r.TenantId, r.BlobName });

                e.Property(r => r.BlobName)
                 .HasMaxLength(1024);

                e.Property(r => r.OriginalFileName)
                 .HasMaxLength(256);

                e.Property(r => r.ContentType)
                 .HasMaxLength(128);

                e.Property(r => r.FolderPath)
                 .HasMaxLength(500);

                e.Property(r => r.AddedByUserId)
                 .HasMaxLength(450);
            });

            // RoomAgent config
            builder.Entity<RoomAgent>(e =>
            {
                e.HasIndex(a => new { a.TenantId, a.RoomId })
                 .IsUnique();

                e.Property(a => a.AgentId)
                 .HasMaxLength(128);

                e.Property(a => a.VectorStoreId)
                 .HasMaxLength(128);
            });

            // RoomFileRef: VectorStoreFileId
            builder.Entity<RoomFileRef>()
                .Property(r => r.VectorStoreFileId)
                .HasMaxLength(128);

            // RoomWebLink config
            builder.Entity<RoomWebLink>(e =>
            {
                e.HasIndex(w => new { w.TenantId, w.RoomId, w.LinkId })
                 .IsUnique();

                e.HasIndex(w => new { w.TenantId, w.RoomId });

                e.Property(w => w.Url)
                 .HasMaxLength(2048);

                e.Property(w => w.Title)
                 .HasMaxLength(256);

                e.Property(w => w.VectorStoreFileId)
                 .HasMaxLength(128);

                e.Property(w => w.AddedByUserId)
                 .HasMaxLength(450);
            });

            // Apply global tenant filter to all entities implementing ITenantEntity
            foreach (var entityType in builder.Model.GetEntityTypes().ToList())
            {
                var clrType = entityType.ClrType;
                if (typeof(ITenantEntity).IsAssignableFrom(clrType))
                {
                    var parameter = Expression.Parameter(clrType, "e");
                    var left = Expression.Property(parameter, nameof(ITenantEntity.TenantId));

                    // e => ((ITenantEntity)e).TenantId == CurrentTenantId
                    var currentTenantIdMethod = typeof(ApplicationDbContext).GetMethod(nameof(GetCurrentTenantId), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    var tenantIdExpression = Expression.Call(Expression.Constant(this), currentTenantIdMethod);
                    var body = Expression.Equal(left, tenantIdExpression);
                    var lambda = Expression.Lambda(body, parameter);

                    builder.Entity(clrType).HasQueryFilter(lambda);
                }
            }
        }

        // This method is invoked inside the expression tree
        private Guid GetCurrentTenantId()
        {
            var id = _tenantProvider.GetTenantId();
            return id; // Guid.Empty returns no data when filter applied
        }
    }
}
