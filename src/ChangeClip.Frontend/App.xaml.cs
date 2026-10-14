using System;
using System.Windows;
using System.Windows.Threading;

namespace ChangeClip.Frontend
{
    public partial class App : Application
    {
        private bool _handlingDispatcherException;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            base.OnStartup(e);
            var window = Bootstrapper.CreateWindow();
            if (window == null)
            {
                Shutdown(1);
                return;
            }
            MainWindow = window;
            window.Show();
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            if (_handlingDispatcherException)
            {
                Shutdown(1);
                return;
            }

            _handlingDispatcherException = true;
            var details = String.IsNullOrWhiteSpace(e.Exception.Message)
                ? e.Exception.GetType().Name
                : e.Exception.Message;
            MessageBox.Show("Došlo je do neočekivane greške. ChangeClip će se zatvoriti.\n\n" + details,
                "ChangeClip", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
