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
