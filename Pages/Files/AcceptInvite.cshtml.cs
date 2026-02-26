using System;
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
    public class AcceptInviteModel : PageModel
    {
        private readonly IRoomInvitationService _invitations;
        private readonly UserManager<ApplicationUser> _userManager;

        public AcceptInviteModel(IRoomInvitationService invitations, UserManager<ApplicationUser> userManager)
        {
            _invitations = invitations;
            _userManager = userManager;
        }

        [BindProperty(SupportsGet = true)]
        public string? Token { get; set; }

        public string? ErrorMessage { get; set; }
        public string? SuccessMessage { get; set; }
        public Guid? RoomId { get; set; }
        public string? RoomRole { get; set; }
        public string? InviterEmail { get; set; }
        public string? InviteMessage { get; set; }
        public bool ShowAcceptButton { get; set; }
        public bool Accepted { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            if (string.IsNullOrWhiteSpace(Token))
            {
                ErrorMessage = "No invitation token provided.";
                return Page();
            }

            var invitation = await _invitations.GetInvitationByTokenAsync(Token);
            if (invitation == null)
            {
                ErrorMessage = "Invitation not found.";
                return Page();
            }

            if (invitation.Status == InvitationStatus.Accepted)
            {
                ErrorMessage = "This invitation has already been used.";
                RoomId = invitation.RoomId;
                return Page();
            }

            if (invitation.Status == InvitationStatus.Revoked)
            {
                ErrorMessage = "This invitation has been revoked.";
                return Page();
            }

            if (invitation.ExpiresUtc < DateTimeOffset.UtcNow)
            {
                ErrorMessage = "This invitation has expired.";
                return Page();
            }

            RoomId = invitation.RoomId;
            RoomRole = invitation.Role.ToString();
            InviteMessage = invitation.Message;
            ShowAcceptButton = true;

            // Look up inviter display name
            if (!string.IsNullOrEmpty(invitation.InvitedByUserId))
            {
                var inviter = await _userManager.FindByIdAsync(invitation.InvitedByUserId);
                InviterEmail = inviter?.Email;
            }

            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrWhiteSpace(Token))
            {
                ErrorMessage = "No invitation token provided.";
                return Page();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
            var userEmail = User.FindFirstValue(ClaimTypes.Email) ?? "";

            var (success, roomId, error) = await _invitations.AcceptInvitationAsync(Token, userId, userEmail);

            if (!success)
            {
                ErrorMessage = error ?? "Failed to accept invitation.";
                RoomId = roomId;
                return Page();
            }

            Accepted = true;
            SuccessMessage = "Invitation accepted! You now have access to the data room.";
            RoomId = roomId;
            return Page();
        }
    }
}
