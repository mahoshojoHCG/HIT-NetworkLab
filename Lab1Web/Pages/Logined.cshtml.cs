using System.Net;
using Lab1Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lab1Web.Pages
{
    public class LoginedModel : PageModel
    {
        public IActionResult OnGet()
        {
            var forward = Request.Headers["X-Forwarded-For"];
            if (string.IsNullOrEmpty(forward))
                forward = Request.HttpContext.Connection.RemoteIpAddress.ToString();
            var ip = IPAddress.Parse(forward);
            if (!LoginController.Auth.ContainsKey(ip))
                return Redirect("/Login");
            return Page();
        }
    }
}