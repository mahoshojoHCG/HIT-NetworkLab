using System.Collections.Generic;
using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace Lab1Web.Controllers
{
    [Route("api/[controller]")]
    public class LoginController : ControllerBase
    {
        public static Dictionary<string, string> PasswordDictionary { get; } = new Dictionary<string, string>();
        public static Dictionary<IPAddress, string> Auth { get; } = new Dictionary<IPAddress, string>();

        [HttpGet("PushPassword")]
        public IActionResult PushPassword([FromQuery] string userName, [FromQuery] string token)
        {
            if (Equals(Request.HttpContext.Connection.RemoteIpAddress, IPAddress.Loopback) ||
                Equals(Request.HttpContext.Connection.RemoteIpAddress, IPAddress.IPv6Loopback))
            {
                PasswordDictionary[userName] = token;
                return Ok();
            }

            return BadRequest();
        }

        [HttpGet("CheckAuth")]
        public IActionResult CheckAuth([FromQuery] string ip)
        {
            if (Equals(Request.HttpContext.Connection.RemoteIpAddress, IPAddress.Loopback) ||
                Equals(Request.HttpContext.Connection.RemoteIpAddress, IPAddress.IPv6Loopback))
            {
                var add = IPAddress.Parse(ip);
                if (Auth.TryGetValue(add, out var name))
                    return Ok(name);
                return NotFound();
            }

            return BadRequest();
        }
    }
}