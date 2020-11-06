using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lab1
{
    internal class Program
    {
        private static async Task Main(string[] args)
        {
            var builder =
                new ConfigurationBuilder()
                    .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "config.json"), false, true);
            var root = builder.Build();
            var services = new ServiceCollection();
            services.AddSingleton(root);
            services.AddSingleton<IConfiguration>(root);
            services.AddLogging(b => b.AddConsole());
            services.AddSingleton(typeof(HttpProxy));
            await using var provider = services.BuildServiceProvider();
            await provider.GetService<HttpProxy>().RunProxyAsync();
        }
    }
}