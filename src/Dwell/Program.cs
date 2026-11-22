using System.Diagnostics;
using System.Text.Json;
using Dwell.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Dwell;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--power-worker", var mode, var parentId] && int.TryParse(parentId, out var pid))
        {
            RunPowerWorker(pid, mode == "emi" ||
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64);
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }

    private static void RunPowerWorker(int parentId, bool emiOnly)
    {
        if (!Console.IsInputRedirected || !Console.IsOutputRedirected) return;
        try
        {
            using var parent = Process.GetProcessById(parentId);
            if (parent.HasExited) return;
            var watchdog = new Thread(() =>
            {
                try { parent.WaitForExit(); }
                catch (InvalidOperationException) { }
                Environment.Exit(0);
            }) { IsBackground = true, Name = "Power worker lifetime" };
            watchdog.Start();

            using var monitor = new HardwarePowerMonitor(emiOnly);
            while (Console.ReadLine() == "read")
                Console.WriteLine(JsonSerializer.Serialize(monitor.Read()));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Debug.WriteLine(ex);
        }
    }
}
