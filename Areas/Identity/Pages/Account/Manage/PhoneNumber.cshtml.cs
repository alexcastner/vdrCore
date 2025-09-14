using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using twoSaaSCore.Models;
using twoSaaSCore.Services;

namespace twoSaaSCore.Areas.Identity.Pages.Account.Manage
{
    public class PhoneNumberModel : PageModel
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ISmsSender _sms;

        public PhoneNumberModel(UserManager<ApplicationUser> userManager, ISmsSender sms)
        {
            _userManager = userManager;
            _sms = sms;
        }

        [BindProperty] public InputModel Input { get; set; } = new();
        public bool PhoneConfirmed { get; private set; }
        public string? CurrentPhone { get; private set; }

        public class InputModel
        {
            [Phone]
            [Display(Name = "Phone number")]
            public string? PhoneNumber { get; set; }

            [Display(Name = "Verification code")]
            public string? Code { get; set; }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();
            CurrentPhone = user.PhoneNumber;
            PhoneConfirmed = await _userManager.IsPhoneNumberConfirmedAsync(user);
            return Page();
        }

        public async Task<IActionResult> OnPostSendAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            if (string.IsNullOrWhiteSpace(Input.PhoneNumber))
            {
                ModelState.AddModelError(string.Empty, "Phone number required.");
                return await OnGetAsync();
            }

            await _userManager.SetPhoneNumberAsync(user, Input.PhoneNumber.Trim());
            var code = await _userManager.GenerateChangePhoneNumberTokenAsync(user, Input.PhoneNumber.Trim());

            await _sms.SendAsync(Input.PhoneNumber.Trim(), $"Your verification code is: {code}");
            TempData["PhoneVerifySent"] = true;
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostVerifyAsync()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return NotFound();

            if (string.IsNullOrWhiteSpace(Input.PhoneNumber) || string.IsNullOrWhiteSpace(Input.Code))
            {
                ModelState.AddModelError(string.Empty, "Phone + Code required.");
                return await OnGetAsync();
            }

            var success = await _userManager.VerifyChangePhoneNumberTokenAsync(user, Input.Code.Trim(), Input.PhoneNumber.Trim());
            if (!success)
            {
                ModelState.AddModelError(string.Empty, "Invalid code.");
                return await OnGetAsync();
            }

            // Mark confirmed (Identity sets PhoneNumber; confirm explicitly)
            user.PhoneNumberConfirmed = true;
            await _userManager.UpdateAsync(user);

            TempData["PhoneConfirmed"] = true;
            return RedirectToPage();
        }
    }
}