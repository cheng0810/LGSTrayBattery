using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using LGSTrayPrimitives.IPC;
using Microsoft.Extensions.Configuration;
using LGSTrayPrimitives;
using Tommy.Extensions.Configuration;

namespace LGSTrayHID
{
    internal static class GlobalSettings
    {
        public static NativeDeviceManagerSettings settings = new();
    }

    internal class Program
    {
        static async Task Main(string[] args)
        {
            DiagnosticLog.Initialize("LGSTrayHID");
            DiagnosticLog.WriteLine($"Native HID service starting from {AppContext.BaseDirectory}");
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                {
                    DiagnosticLog.WriteException("Unhandled native HID exception", exception);
                }
            };

            var builder = Host.CreateEmptyApplicationBuilder(null);
            builder.Configuration.AddTomlFile("appsettings.toml");

            GlobalSettings.settings = builder.Configuration.GetSection("Native")
                .Get<NativeDeviceManagerSettings>() ?? GlobalSettings.settings;
            DiagnosticLog.WriteLine($"Native settings loaded; retryTime={GlobalSettings.settings.RetryTime}s pollPeriod={GlobalSettings.settings.PollPeriod}s");

            builder.Services.AddLGSMessagePipe();
            builder.Services.AddHostedService<HidppManagerService>();

            var host = builder.Build();

            _ = Task.Run(async () =>
            {
                bool ret = int.TryParse(args.ElementAtOrDefault(0), out int parentPid);
                if (!ret) {
#if DEBUG
                    return; 
#else
                    // Started without a parent, assume invalid.
                    Environment.Exit(0);
#endif
                }

                DiagnosticLog.WriteLine($"Monitoring UI parent process {parentPid}");
                await Process.GetProcessById(parentPid).WaitForExitAsync();
                DiagnosticLog.WriteLine("UI parent exited; stopping native HID service");

                CancellationTokenSource cts = new(5000);
                await host.StopAsync(cts.Token);

                Environment.Exit(0);
            });

            try
            {
                await host.RunAsync();
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteException("Unhandled native HID exception", ex);
                throw;
            }
        }
    }
}
