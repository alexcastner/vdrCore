using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class RoomChatModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomPermissionService _permissions;
        private readonly IRoomAgentService _agentService;
        private readonly IRoomFileCatalog _catalog;
        private readonly IAuditLogger _auditLogger;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<RoomChatModel> _logger;

        public RoomChatModel(
            ITenantProvider tenantProvider,
            IRoomPermissionService permissions,
            IRoomAgentService agentService,
            IRoomFileCatalog catalog,
            IAuditLogger auditLogger,
            UserManager<ApplicationUser> userManager,
            ILogger<RoomChatModel> logger)
        {
            _tenantProvider = tenantProvider;
            _permissions = permissions;
            _agentService = agentService;
            _catalog = catalog;
            _auditLogger = auditLogger;
            _userManager = userManager;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public Guid RoomId { get; set; }

        public string? RoomName { get; private set; }
        public bool AiConfigured => _agentService.IsConfigured;

        /// <summary>Current custom system instructions for this room (null = defaults only).</summary>
        public string? SystemInstructions { get; private set; }

        /// <summary>Whether the current user can edit system instructions (Owner/Admin only).</summary>
        public bool CanEditInstructions { get; private set; }

        /// <summary>Saved chat threads for the current user in this room.</summary>
        public List<ChatThreadSummary> SavedThreads { get; private set; } = [];

        private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

        public async Task<IActionResult> OnGetAsync()
        {
            if (RoomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = GetUserId();
            var perms = await _permissions.GetEffectivePermissionsAsync(tenantId, RoomId, userId);
            if (!perms.HasFlag(RoomPermission.ViewDocuments))
                return Forbid();

            CanEditInstructions = perms.HasFlag(RoomPermission.ManageRoom);

            var room = await _catalog.GetRoomAsync(tenantId, RoomId);
            RoomName = room?.Name ?? "Room";

            if (_agentService.IsConfigured)
            {
                SystemInstructions = await _agentService.GetSystemInstructionsAsync(tenantId, RoomId);
                SavedThreads = await _agentService.ListThreadsAsync(tenantId, RoomId, userId, savedOnly: true);
            }

            return Page();
        }

        /// <summary>AJAX handler to save a chat thread for later reuse.</summary>
        public async Task<IActionResult> OnPostSaveThreadAsync(
            [FromForm] Guid roomId,
            [FromForm] string threadId,
            [FromForm] string? title)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(threadId))
                return new JsonResult(new { error = "Invalid request." }) { StatusCode = 400 };

            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty)
                return new JsonResult(new { error = "Forbidden." }) { StatusCode = 403 };

            var userId = GetUserId();
            var perms = await _permissions.GetEffectivePermissionsAsync(tenantId, roomId, userId);
            if (!perms.HasFlag(RoomPermission.ViewDocuments))
                return new JsonResult(new { error = "Forbidden." }) { StatusCode = 403 };

            try
            {
                await _agentService.SaveThreadAsync(tenantId, roomId, userId, threadId, title);
                return new JsonResult(new { ok = true });
            }
            catch (InvalidOperationException ex)
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 404 };
            }
        }

        /// <summary>AJAX handler to list saved chat threads for the current user.</summary>
        public async Task<IActionResult> OnGetSavedThreadsAsync(Guid roomId)
        {
            if (roomId == Guid.Empty)
                return new JsonResult(new { });

            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty)
                return Forbid();

            var userId = GetUserId();
            var perms = await _permissions.GetEffectivePermissionsAsync(tenantId, roomId, userId);
            if (!perms.HasFlag(RoomPermission.ViewDocuments))
                return Forbid();

            var threads = await _agentService.ListThreadsAsync(tenantId, roomId, userId, savedOnly: true);
            return new JsonResult(threads);
        }

        /// <summary>AJAX handler to save custom system instructions.</summary>
        public async Task<IActionResult> OnPostSaveInstructionsAsync(
            [FromForm] Guid roomId,
            [FromForm] string? instructions)
        {
            if (roomId == Guid.Empty)
                return new JsonResult(new { error = "Invalid request." }) { StatusCode = 400 };

            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty)
                return new JsonResult(new { error = "Forbidden." }) { StatusCode = 403 };

            var userId = GetUserId();
            var perms = await _permissions.GetEffectivePermissionsAsync(tenantId, roomId, userId);
            if (!perms.HasFlag(RoomPermission.ManageRoom))
                return new JsonResult(new { error = "You do not have permission to edit instructions." }) { StatusCode = 403 };

            if (!_agentService.IsConfigured)
                return new JsonResult(new { error = "AI assistant is not configured." }) { StatusCode = 503 };

            try
            {
                await _agentService.UpdateSystemInstructionsAsync(tenantId, roomId, instructions);
                return new JsonResult(new { ok = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save system instructions for room {RoomId}", roomId);
                return new JsonResult(new { error = "Failed to save instructions." }) { StatusCode = 500 };
            }
        }

        public async Task<IActionResult> OnPostChatAsync(
            [FromForm] Guid roomId,
            [FromForm] string message,
            [FromForm] string? threadId)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(message))
                return new JsonResult(new { error = "Invalid request." }) { StatusCode = 400 };

            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty)
                return new JsonResult(new { error = "Forbidden." }) { StatusCode = 403 };

            var userId = GetUserId();
            var perms = await _permissions.GetEffectivePermissionsAsync(tenantId, roomId, userId);
            if (!perms.HasFlag(RoomPermission.ViewDocuments))
                return new JsonResult(new { error = "Forbidden." }) { StatusCode = 403 };

            if (!_agentService.IsConfigured)
                return new JsonResult(new { error = "AI assistant is not configured." }) { StatusCode = 503 };

            var userObj = await _userManager.FindByIdAsync(userId);
            var userEmail = userObj?.Email;

            try
            {
                var response = await _agentService.ChatAsync(
                    tenantId,
                    roomId,
                    userId,
                    userEmail,
                    message.Trim(),
                    threadId);

                // Audit log the interaction
                await _auditLogger.LogAsync(new AuditEntry(
                    tenantId, roomId, null, "AiChat", userId, userEmail,
                    null, null, null, null,
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    Request.Headers.UserAgent.ToString().Truncate(256),
                    _auditLogger.NewCorrelationId(),
                    JsonSerializer.Serialize(new
                    {
                        query = message.Trim().Length > 500 ? message.Trim()[..500] : message.Trim(),
                        responseLength = response.Message.Length,
                        threadId = response.ThreadId,
                        citationCount = response.Citations.Count
                    })));

                return new JsonResult(new
                {
                    message = response.Message,
                    threadId = response.ThreadId,
                    citations = response.Citations
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI Chat failed for room {RoomId}", roomId);
                return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }
    }
}
