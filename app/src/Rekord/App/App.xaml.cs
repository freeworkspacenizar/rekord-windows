using System;
using System.Threading;
using System.Windows;

namespace Rekord.App;

public partial class RekordApplication : Application
{
    private Mutex? _singleInstanceMutex;
    private AppCoordinator? _coordinator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "Local\\Rekord.Windows.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Rekord sudah berjalan di System Tray.\nTekan Ctrl+Shift+6 atau klik ikon Rekord di pojok kanan bawah.",
                "Rekord",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
            Shutdown();
            return;
        }

        _coordinator = new AppCoordinator();
        _coordinator.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _coordinator?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
