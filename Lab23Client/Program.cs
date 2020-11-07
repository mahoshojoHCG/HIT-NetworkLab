using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using CommandLine;
using Lab23;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lab23Client
{
    internal static class Program
    {
        public enum Proto
        {
            StopAndWait,
            SelectiveRepeat
        }

        private static async Task Main(string[] args)
        {
            await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(async options =>
            {
                var services = new ServiceCollection();
                services.AddLogging(b => b.AddConsole());
                switch (options.Proto)
                {
                    case Proto.StopAndWait:
                        services.AddStopAndWait();
                        break;
                    case Proto.SelectiveRepeat:
                        services.AddSelectiveRepeat();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                services.AddSingleton(typeof(FileTransferClient));
                await using var provider = services.BuildServiceProvider();

                var client = provider.GetService<FileTransferClient>();
                var logger = provider.GetService<ILogger<FileTransferClient>>();
                await client.ConnectAsync(IPEndPoint.Parse(options.EndPoint));
                if (options.Get is {Length: > 0})
                {
                    var sw = new Stopwatch();
                    sw.Start();
                    //Get file
                    await client.GetFileAsync(options.Get);
                    sw.Stop();
                    logger.LogInformation($"File transfer took {sw.Elapsed}.");
                }

                if (options.Send is {Length: > 0})
                {
                    var sw = new Stopwatch();
                    sw.Start();
                    //Get file
                    await client.SendFileAsync(new FileInfo(options.Send));
                    sw.Stop();
                    logger.LogInformation($"File transfer took {sw.Elapsed}.");
                }
            });
        }

        private static IServiceCollection AddStopAndWait(this IServiceCollection service)
        {
            return service.AddSingleton(typeof(IUdpProtoClient), typeof(StopAndWaitClient));
        }

        private static IServiceCollection AddSelectiveRepeat(this IServiceCollection service)
        {
            return service.AddSingleton(typeof(SelectiveRepeatClient))
                .AddTransient<IUdpProtoClient>(
                    provider => provider.GetService<SelectiveRepeatClient>());
        }

        public class Options
        {
            [Option('s', "send", HelpText = "Send file to server.")]
            public string Send { get; set; }

            [Option('g', "get", HelpText = "Get file from server.")]
            public string Get { get; set; }

            [Option('p', "proto", Default = Proto.SelectiveRepeat, HelpText = "Proto to use.")]
            public Proto Proto { get; set; }

            [Option("server", Required = true, HelpText = "Remote address of server.")]
            public string EndPoint { get; set; }
        }
    }
}