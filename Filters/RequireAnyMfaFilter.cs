using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Identity;
using twoSaaSCore.Models;

namespace twoSaaSCore.Filters
{
    // Redirects authenticated users without any configured MFA.
    public class RequireAnyMfaFilter : IAsyncPageFilter
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private static readonly string[] _allowedPaths =
        {
            "/",
            "/Index",
            "/Identity/Account/Manage/RequireMfa",
            "/Identity/Account/Manage/EnableAuthenticator",
            "/Identity/Account/Manage/TwoFactorAuthentication",
            "/Identity/Account/Manage/ShowRecoveryCodes",
            "/Identity/Account/Manage/GenerateRecoveryCodes",
            "/Identity/Account/Manage/PhoneNumber",
            "/Identity/Account/Logout",
            "/Identity/Account/Login",
            "/Identity/Account/LoginWith2fa",
            "/Identity/Account/LoginWithSms2fa",
            "/Identity/Account/LoginWithRecoveryCode",
            "/Identity/Account/Register",
            "/Identity/Account/ForgotPassword",
            "/Identity/Account/ResetPassword"
        };

        public RequireAnyMfaFilter(UserManager<ApplicationUser> userManager)
        {
            _userManager = userManager;
        }

        public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

        public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            var http = context.HttpContext;
            var userPrincipal = http.User;

            if (userPrincipal?.Identity?.IsAuthenticated != true)
            {
                await next();
                return;
            }

            var path = http.Request.Path.Value ?? string.Empty;

            // Allow static assets
            if (path.StartsWith("/css", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/js", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/lib", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/images", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            // Allow explicit paths
            if (_allowedPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)))
            {
                await next();
                return;
            }

            var user = await _userManager.GetUserAsync(userPrincipal);
            if (user == null)
            {
                await next();
                return;
            }

            // MFA considered satisfied if TwoFactorEnabled (preferred) OR confirmed phone (backup)
            var hasAuthenticatorOr2fa = user.TwoFactorEnabled;
            var hasConfirmedPhone = user.PhoneNumberConfirmed && !string.IsNullOrWhiteSpace(user.PhoneNumber);

            if (!(hasAuthenticatorOr2fa || hasConfirmedPhone))
            {
                context.Result = new RedirectToPageResult("/Account/Manage/RequireMfa", new { area = "Identity" });
                return;
            }

            await next();
        }
    }
}