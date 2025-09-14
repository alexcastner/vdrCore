using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using twoSaaSCore.Constants;
using twoSaaSCore.Data;
using twoSaaSCore.Models;

namespace twoSaaSCore.Areas.Identity.Pages.Account
{
    public class RegisterModel2 : PageModel
    {
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _db;

        public RegisterModel2(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ApplicationDbContext db)
        {
            _userManager = userManager;
            _signInManager = signInManager;
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

            [DataType(DataType.Password)]
            [Display(Name = "Confirm password")]
            [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
            public string ConfirmPassword { get; set; } = string.Empty;

            //[Display(Name = "Tenant Name")]
            //public string? TenantName { get; set; }

            //[Required]
            //[RegularExpression("^[a-z0-9-]+$", ErrorMessage = "Subdomain may only contain lowercase letters, numbers, and hyphens.")]
            //public string Subdomain { get; set; } = string.Empty;
        }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
        {
            returnUrl ??= Url.Content("~/");
            if (ModelState.IsValid)
            {
                //var existingTenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Subdomain == Input.Subdomain);
                //Tenant tenant;
                //if (existingTenant == null)
                //{
                //    tenant = new Tenant
                //    {
                //        TenantId = Guid.NewGuid(),
                //        TenantName = string.IsNullOrWhiteSpace(Input.TenantName) ? Input.Subdomain : Input.TenantName!,
                //        Subdomain = Input.Subdomain
                //    };
                //    _db.Tenants.Add(tenant);
                //    await _db.SaveChangesAsync();
                //}
                //else
                //{
                //    tenant = existingTenant;
                //}
                //var user = new ApplicationUser
                //{
                //    UserName = Input.Email,
                //    Email = Input.Email,
                //    TenantId = tenant.TenantId,
                //    Subdomain = tenant.Subdomain
                //};
                //var result = await _userManager.CreateAsync(user, Input.Password);
                //if (result.Succeeded)
                //{
                //    var claims = new[] { new Claim(TenantConstants.TenantIdClaimType, user.TenantId.ToString()) };
                //    await _userManager.AddClaimsAsync(user, claims);
                //    await _signInManager.SignInAsync(user, isPersistent: false);
                //    var redirect = BuildTenantRootUrl(tenant.Subdomain);
                //    return LocalRedirect(redirect);
                //}
                //foreach (var error in result.Errors)
                //{
                //    ModelState.AddModelError(string.Empty, error.Description);
                //}
            }
            // If we got this far, something failed, redisplay form
            return Page();
        }

        private string BuildTenantRootUrl(string subdomain)
        {
            var host = HttpContext.Request.Host;
            var rootHost = host.Host;
            var port = host.Port;

            // strip leading subdomain if present
            var hostParts = rootHost.Split('.');
            if (hostParts.Length > 2)
            {
                rootHost = string.Join('.', hostParts.Skip(1));
            }

            var scheme = HttpContext.Request.Scheme;
            var baseHost = string.IsNullOrWhiteSpace(subdomain) ? rootHost : $"{subdomain}.{rootHost}";
            return port.HasValue ? $"{scheme}://{baseHost}:{port}/" : $"{scheme}://{baseHost}/";
        }
    }
}
