using System.Windows;
using FreeGamesClaimer.Services;

namespace FreeGamesClaimer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureDirectories();
        base.OnStartup(e);
    }
}
