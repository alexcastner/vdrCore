using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace twoSaaSCore.Areas.Identity.Pages.Account.Manage
{
    [Authorize]
    public class RequireMfaModel : PageModel
    {
        public void OnGet() { }
    }
}