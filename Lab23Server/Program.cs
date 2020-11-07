using System;
using System.IO;
using System.Threading.Tasks;
using Lab23;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lab23Server
{
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            var builder =
                new ConfigurationBuilder()
                    .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "config.json"),
                        false, true);

            var root = builder.Build();
            var services = new ServiceCollection();
            services.AddSingleton(root);
            services.AddSingleton<IConfiguration>(root);
            services.AddLogging(b => b.AddConsole());
            if (root["UseSelectiveRepeat"] != "false")
                services.AddSelectiveRepeat();
            else
                services.AddStopAndWait();
            services.AddSelectiveRepeat();
            services.AddSingleton(typeof(FileTransferServer));
            await using var provider = services.BuildServiceProvider();
            var server = provider.GetService<FileTransferServer>();
            await server.RunAsync();
        }

        private static IServiceCollection AddStopAndWait(this IServiceCollection service)
        {
            return service.AddSingleton(typeof(StopAndWaitServer))
                .AddTransient<IUdpProtoServer>(
                    provider => provider.GetService<StopAndWaitServer>());
        }

        private static IServiceCollection AddSelectiveRepeat(this IServiceCollection service)
        {
            return service.AddSingleton(typeof(SelectiveRepeatServer))
                .AddTransient<IUdpProtoServer>(
                    provider => provider.GetService<SelectiveRepeatServer>());
        }
    }
}