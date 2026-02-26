using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Pages.Files
{
    [Authorize]
    public class QAModel : PageModel
    {
        private readonly ITenantProvider _tenantProvider;
        private readonly IRoomPermissionService _permissions;
        private readonly IRoomQaService _qa;
        private readonly UserManager<ApplicationUser> _userManager;

        public QAModel(ITenantProvider tenantProvider, IRoomPermissionService permissions, IRoomQaService qa, UserManager<ApplicationUser> userManager)
        {
            _tenantProvider = tenantProvider;
            _permissions = permissions;
            _qa = qa;
            _userManager = userManager;
        }

        [BindProperty(SupportsGet = true)]
        public Guid RoomId { get; set; }

        [BindProperty(SupportsGet = true)]
        public int? QuestionId { get; set; }

        public List<RoomQuestion> Questions { get; private set; } = new();
        public RoomQuestion? SelectedQuestion { get; private set; }
        public List<RoomAnswer> Answers { get; private set; } = new();
        public RoomPermission CurrentPermissions { get; private set; }

        private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

        public async Task<IActionResult> OnGetAsync()
        {
            if (RoomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = GetUserId();
            CurrentPermissions = await _permissions.GetEffectivePermissionsAsync(tenantId, RoomId, userId);
            if (!CurrentPermissions.HasFlag(RoomPermission.AccessRoom))
                return Forbid();

            Questions = await _qa.ListQuestionsAsync(tenantId, RoomId);

            if (QuestionId.HasValue)
            {
                SelectedQuestion = await _qa.GetQuestionAsync(tenantId, QuestionId.Value);
                if (SelectedQuestion != null)
                    Answers = await _qa.ListAnswersAsync(tenantId, QuestionId.Value);
            }

            return Page();
        }

        public async Task<IActionResult> OnPostAskAsync(Guid roomId, string subject, string body)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body))
                return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = GetUserId();
            if (!await _permissions.HasPermissionAsync(tenantId, roomId, userId, RoomPermission.AccessRoom))
                return Forbid();

            var user = await _userManager.FindByIdAsync(userId);
            await _qa.AskQuestionAsync(tenantId, roomId, subject.Trim(), body.Trim(), userId, user?.Email);
            return RedirectToPage(new { roomId });
        }

        public async Task<IActionResult> OnPostAnswerAsync(Guid roomId, int questionId, string body)
        {
            if (roomId == Guid.Empty || string.IsNullOrWhiteSpace(body))
                return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = GetUserId();
            if (!await _permissions.HasPermissionAsync(tenantId, roomId, userId, RoomPermission.AccessRoom))
                return Forbid();

            var user = await _userManager.FindByIdAsync(userId);
            await _qa.AnswerQuestionAsync(tenantId, questionId, body.Trim(), userId, user?.Email);
            return RedirectToPage(new { roomId, questionId });
        }

        public async Task<IActionResult> OnPostCloseAsync(Guid roomId, int questionId)
        {
            if (roomId == Guid.Empty) return BadRequest();
            var tenantId = _tenantProvider.GetTenantId();
            if (tenantId == Guid.Empty) return Forbid();

            var userId = GetUserId();
            if (!await _permissions.HasPermissionAsync(tenantId, roomId, userId, RoomPermission.ManagePermissions))
                return Forbid();

            await _qa.CloseQuestionAsync(tenantId, questionId);
            return RedirectToPage(new { roomId, questionId });
        }
    }
}
