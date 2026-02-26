using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class AuditLogModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomPermissionService _permissions;
        private readonly IAuditLogger _auditLogger;

        public AuditLogModel(ITenantProvider tenantProvider, IRoomPermissionService permissions, IAuditLogger auditLogger)
        {
            _tenantProvider = tenantProvider;
            _permissions = permissions;
            _auditLogger = auditLogger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid RoomId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int Page_ { get; set; } = 1;

        public List<DocumentAuditLog> Entries { get; private set; } = new();
        public int CurrentPage => Page_ < 1 ? 1 : Page_;
        public bool HasMore { get; private set; }

        private const int PageSize = 50;

        public async Task<IActionResult> OnGetAsync()
        {
            if (RoomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            if (!await _permissions.HasPermissionAsync(tenantId, RoomId, userId, RoomPermission.ViewAuditLog))
                return Forbid();

            var skip = (CurrentPage - 1) * PageSize;
            Entries = await _auditLogger.ListAsync(tenantId, RoomId, skip, PageSize + 1);
            HasMore = Entries.Count > PageSize;
            if (HasMore) Entries.RemoveAt(Entries.Count - 1);

            return Page();
        }
    }
}
