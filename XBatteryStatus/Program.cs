using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace XBatteryStatus;

internal static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        var proc = Process.GetCurrentProcess();
        Process[] processes = Process.GetProcessesByName(proc.ProcessName);

        if (processes.Length > 1)
        {
            foreach (var process in processes)
            {
                if (process.Id != proc.Id)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // Ignore exceptions.
                    }
                }
            }
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MyApplicationContext());
    }
}
