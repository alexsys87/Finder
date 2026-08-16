using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace Finder
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // Без этих обработчиков любое необработанное исключение
            // (например, в async void обработчике) молча завершает процесс.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Debug.WriteLine($"Unhandled UI exception: {e.Exception}");
            MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}",
                            "Finder", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Debug.WriteLine($"Unhandled exception: {e.ExceptionObject}");
        }
    }
}
