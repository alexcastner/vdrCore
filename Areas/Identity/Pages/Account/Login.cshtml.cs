using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using twoSaaSCore.Constants;
using twoSaaSCore.Data;
using twoSaaSCore.Models;

namespace twoSaaSCore.Areas.Identity.Pages.Account
{
    public class LoginModel : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _db;

        public LoginModel(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager, ApplicationDbContext db)
        {
            _signInManager = signInManager;
            _userManager = userManager;
            _db = db;
        }

        [BindProperty]
        public InputModel Input { get; set; } = new();

        public class InputModel
        {
            [Required]
            [EmailAddress]
            public string Email { get; set; } = string.Empty;

            [Required]
            [DataType(DataType.Password)]
            public string Password { get; set; } = string.Empty;

            [Display(Name = "Remember me?")]
            public bool RememberMe { get; set; }

            [Display(Name = "Subdomain")]
            [RegularExpression("^[a-z0-9-]*$", ErrorMessage = "Subdomain may only contain lowercase letters, numbers, and hyphens.")]
            public string? Subdomain { get; set; }
        }

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
        {
            returnUrl ??= Url.Page("/Files/Index") ?? "/Files/Index";
            bool isAjax = Request.Headers["X-Requested-With"] == "XMLHttpRequest";

            if (!ModelState.IsValid)
            {
                if (isAjax)
                    return new JsonResult(new { succeeded = false, error = "Please provide a valid email and password." });
                return Page();
            }

            var user = await _userManager.Users.FirstOrDefaultAsync(u => u.Email == Input.Email);
            if (user == null)
            {
                if (isAjax)
                    return new JsonResult(new { succeeded = false, error = "Invalid login attempt." });
                ModelState.AddModelError(string.Empty, "Invalid login attempt.");
                return Page();
            }

            // First factor
            var result = await _signInManager.PasswordSignInAsync(user.UserName!, Input.Password, Input.RememberMe, lockoutOnFailure: true);

            if (result.RequiresTwoFactor)
            {
                if (isAjax)
                    return new JsonResult(new { succeeded = false, requiresTwoFactor = true, redirectUrl = Url.Page("./LoginWith2fa", new { ReturnUrl = returnUrl, RememberMe = Input.RememberMe }) });
                return RedirectToPage("./LoginWith2fa", new { ReturnUrl = returnUrl, RememberMe = Input.RememberMe });
            }

            if (result.Succeeded)
            {
                // Ensure tenant claim exists (will also be done in 2FA success path separately if needed)
                var claims = await _userManager.GetClaimsAsync(user);
                if (!claims.Any(c => c.Type == TenantConstants.TenantIdClaimType))
                {
                    await _userManager.AddClaimAsync(user, new Claim(TenantConstants.TenantIdClaimType, user.TenantId.ToString()));
                    // Re-sign-in so the new claim is included in the current cookie
                    await _signInManager.RefreshSignInAsync(user);
                }

                if (isAjax)
                    return new JsonResult(new { succeeded = true, redirectUrl = returnUrl });
                return LocalRedirect(returnUrl);
            }

            if (result.IsLockedOut)
            {
                if (isAjax)
                    return new JsonResult(new { succeeded = false, error = "Account locked. Try again later." });
                ModelState.AddModelError(string.Empty, "Account locked. Try again later.");
                return Page();
            }

            if (isAjax)
                return new JsonResult(new { succeeded = false, error = "Invalid login attempt." });
            ModelState.AddModelError(string.Empty, "Invalid login attempt.");
            return Page();
        }

        private string BuildTenantRootUrl(string subdomain)
        {
            var host = HttpContext.Request.Host;
            var rootHost = host.Host;
            var port = host.Port;
            var hostParts = rootHost.Split('.');
            if (hostParts.Length > 2)
                rootHost = string.Join('.', hostParts.Skip(1));

            var scheme = HttpContext.Request.Scheme;
            var baseHost = string.IsNullOrWhiteSpace(subdomain) ? rootHost : $"{subdomain}.{rootHost}";
            return port.HasValue ? $"{scheme}://{baseHost}:{port}/" : $"{scheme}://{baseHost}/";
        }
    }
}
