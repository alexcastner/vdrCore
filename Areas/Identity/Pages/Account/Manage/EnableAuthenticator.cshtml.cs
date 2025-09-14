using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using QRCoder;
using twoSaaSCore.Models;

namespace twoSaaSCore.Areas.Identity.Pages.Account.Manage
{
    [Authorize]
    public class EnableAuthenticatorModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;

        public EnableAuthenticatorModel(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        [BindProperty] public InputModel Input { get; set; } = new();

        public string? SharedKey { get; private set; }
        public string? AuthenticatorUri { get; private set; }
        public string? QrCodePngBase64 { get; private set; }

        public class InputModel
        {
            [Required]
            [Display(Name = "Verification code")]
            public string Code { get; set; } = string.Empty;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();
            await LoadKeyAndQrAsync(user);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            await LoadKeyAndQrAsync(user);

            if (!ModelState.IsValid) return Page();

            var verificationCode = Input.Code.Replace(" ", "").Replace("-", "");
            var isValid = await _userManager.VerifyTwoFactorTokenAsync(
                user,
                _userManager.Options.Tokens.AuthenticatorTokenProvider,
                verificationCode);

            if (!isValid)
            {
                ModelState.AddModelError(string.Empty, "Invalid code. Recheck your authenticator app.");
                return Page();
            }

            await _userManager.SetTwoFactorEnabledAsync(user, true);

            if (await _userManager.CountRecoveryCodesAsync(user) == 0)
            {
                var rc = await _userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
                TempData["RecoveryCodes"] = rc.ToArray();
                return RedirectToPage("./ShowRecoveryCodes");
            }

            return RedirectToPage("./TwoFactorAuthentication");
        }

        private async Task LoadKeyAndQrAsync(ApplicationUser user)
        {
            var key = await _userManager.GetAuthenticatorKeyAsync(user);
            if (string.IsNullOrEmpty(key))
            {
                await _userManager.ResetAuthenticatorKeyAsync(user);
                key = await _userManager.GetAuthenticatorKeyAsync(user);
            }

            SharedKey = FormatSharedKey(key);
            var email = await _userManager.GetEmailAsync(user) ?? user.UserName ?? "user";

            AuthenticatorUri = BuildOtpAuthUri("twoSaaSCore", email, key);
            QrCodePngBase64 = GenerateQrBase64(AuthenticatorUri);
        }

        private static string BuildOtpAuthUri(string issuer, string account, string secret)
            => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

        private static string FormatSharedKey(string raw)
        {
            raw = raw.ToUpperInvariant().Replace(" ", "");
            var sb = new StringBuilder();
            for (int i = 0; i < raw.Length; i += 4)
            {
                var take = Math.Min(4, raw.Length - i);
                sb.Append(raw.AsSpan(i, take));
                if (i + take < raw.Length) sb.Append(' ');
            }
            return sb.ToString();
        }

        private static string GenerateQrBase64(string content)
        {
            using var gen = new QRCodeGenerator();
            using var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
            using var qr = new PngByteQRCode(data);
            var bytes = qr.GetGraphic(20);
            return Convert.ToBase64String(bytes);
        }
    }
}