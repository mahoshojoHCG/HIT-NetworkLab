using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Lab23;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Lab23Server
{
    static class Program
    {
        static async Task Main(string[] args)
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
            services.AddSelectiveRepeat();
            services.AddSingleton(typeof(FileTransferServer));
            await using var provider = services.BuildServiceProvider();
            var server = provider.GetService<FileTransferServer>();
            await server.RunAsync();
        }

        static IServiceCollection AddStopAndWait(this IServiceCollection service)
            => service.AddSingleton(typeof(StopAndWaitServer))
                      .AddTransient<IUdpProtoServer>(
                          provider => provider.GetService<StopAndWaitServer>());
        static IServiceCollection AddSelectiveRepeat(this IServiceCollection service)
            => service.AddSingleton(typeof(SelectiveRepeatServer))
                .AddTransient<IUdpProtoServer>(
                    provider => provider.GetService<SelectiveRepeatServer>());


    }
}

