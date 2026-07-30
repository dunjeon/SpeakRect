using System;
using System.Threading;
using System.Windows.Forms;

namespace SpeakRect
{
    internal static class Program
    {
        private static Mutex? _mutex;

        /// <summary>
        /// The main entry point for the application.
        /// Enforces single instance using a named mutex.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Global mutex ensures only one instance of SpeakRect can run at a time
            const string mutexName = "Global\\SpeakRect_SingleInstance_2026";

            _mutex = new Mutex(true, mutexName, out bool isNewInstance);

            if (!isNewInstance)
            {
                // Another instance is already running
                MessageBox.Show(
                    "SpeakRect is already running.\n\n" +
                    "Check the system tray (notification area) for the existing instance.",
                    "SpeakRect - Already Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Prefer dark system chrome (scrollbars/menus) before first window.
                UiTheme.InitAppDarkMode();
                // Load / create SpeakRect.ini before UI so flags are ready
                AppSettings.Current.Load();

                // Bundled Local-LLM host: start with SpeakRect, die with SpeakRect
                // (explicit Stop + Job Object KILL_ON_JOB_CLOSE).
                LocalLlmHost.Start();
                Application.ApplicationExit += (_, _) => LocalLlmHost.Stop();
                AppDomain.CurrentDomain.ProcessExit += (_, _) => LocalLlmHost.Stop();

                Application.Run(new frm_SpeakRect());
            }
            finally
            {
                LocalLlmHost.Stop();
                // Release the mutex when the application exits
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }
    }
}