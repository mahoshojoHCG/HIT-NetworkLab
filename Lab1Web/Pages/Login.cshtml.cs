using System.Net;
using Lab1Web.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Lab1Web.Pages
{
    public class LoginModel : PageModel
    {
        [BindProperty] public string UserName { get; set; }

        [BindProperty] public string Token { get; set; }

        public IActionResult OnGet()
        {
            var forward = Request.Headers["X-Forwarded-For"];
            if (string.IsNullOrEmpty(forward))
                forward = Request.HttpContext.Connection.RemoteIpAddress.ToString();
            var ip = IPAddress.Parse(forward);
            if (LoginController.Auth.ContainsKey(ip))
                return Redirect("/Logined");
            return Page();
        }

        public IActionResult OnPost()
        {
            if (LoginController.PasswordDictionary.ContainsKey(UserName) &&
                LoginController.PasswordDictionary[UserName] == Token)
            {
                //Login success
                var forward = Request.Headers["X-Forwarded-For"];
                if (string.IsNullOrEmpty(forward))
                    forward = Request.HttpContext.Connection.RemoteIpAddress.ToString();
                var ip = IPAddress.Parse(forward);
                LoginController.Auth[ip] = UserName;
                return Redirect("/Logined");
            }

            return Page();
        }
    }
}