using System.Collections.Generic;

namespace Lab1
{
    public class UserInfo
    {
        public string UserName { get; set; }
        public string Token { get; set; }
        public List<string> BlockList { get; set; } = new List<string>();
        public List<string> AllowList { get; set; } = new List<string>();
        public bool Login { get; set; } = false;
    }
}