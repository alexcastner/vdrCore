using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Areas.Identity.Pages.Account
{
    [AllowAnonymous]
    public class LoginWithSms2faModel : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ISmsSender _smsSender;
        private readonly ILogger<LoginWithSms2faModel> _logger;

        public LoginWithSms2faModel(SignInManager<ApplicationUser> signInManager,
                                    UserManager<ApplicationUser> userManager,
                                    ISmsSender smsSender,
                                    ILogger<LoginWithSms2faModel> logger)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _smsSender = smsSender;
            _logger = logger;
        }

        [BindProperty] public InputModel Input { get; set; } = new();
        [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
        [BindProperty(SupportsGet = true)] public bool RememberMe { get; set; }

        public bool CodeSent { get; private set; }

        public class InputModel
        {
            [Required]
            [Display(Name = "Code")]
            public string Code { get; set; } = string.Empty;
        }

        public async Task<IActionResult> OnGetAsync(bool rememberMe, string? returnUrl = null)
        {
            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null) return RedirectToPage("./Login");
            RememberMe = rememberMe;
            ReturnUrl = returnUrl;

            if (!user.PhoneNumberConfirmed || string.IsNullOrEmpty(user.PhoneNumber))
            {
                TempData["SmsUnavailable"] = true;
                return RedirectToPage("./LoginWith2fa", new { rememberMe, returnUrl });
            }

            // Send code automatically on first load
            await SendCodeAsync(user);
            CodeSent = true;
            return Page();
        }

        public async Task<IActionResult> OnPostSendAsync()
        {
            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null) return RedirectToPage("./Login");

            await SendCodeAsync(user);
            CodeSent = true;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid) return Page();

            var user = await _signInManager.GetTwoFactorAuthenticationUserAsync();
            if (user == null) return RedirectToPage("./Login");

            var code = Input.Code.Replace(" ", "").Replace("-", "");
            var result = await _signInManager.TwoFactorSignInAsync(TokenOptions.DefaultPhoneProvider, code, RememberMe, rememberClient: false);

            if (result.Succeeded)
            {
                _logger.LogInformation("User {UserId} logged in with SMS 2FA.", await _userManager.GetUserIdAsync(user));
                var target = ReturnUrl;
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = Url.Page("/Files/Index") ?? "/Files/Index";
                }
                return LocalRedirect(target);
            }
            if (result.IsLockedOut)
            {
                return RedirectToPage("./Lockout");
            }
            ModelState.AddModelError(string.Empty, "Invalid code.");
            return Page();
        }

        private async Task SendCodeAsync(ApplicationUser user)
        {
            var token = await _userManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultPhoneProvider);
            if (!string.IsNullOrEmpty(user.PhoneNumber))
            {
                await _smsSender.SendAsync(user.PhoneNumber, $"Security code: {token}");
            }
        }
    }
}