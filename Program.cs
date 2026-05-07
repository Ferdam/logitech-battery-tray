using System;
using System.Windows.Forms;

namespace G703BatteryMonitor;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new BatteryMonitorContext());
    }
}
