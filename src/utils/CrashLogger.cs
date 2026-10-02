using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// Writes unhandled exceptions to a log file next to the executable.
    ///
    /// Without this, a failure on any thread other than the UI thread terminates the process
    /// silently, which makes intermittent faults effectively undebuggable. Note that an access
    /// violation in native audio code fails fast and cannot reach here; those only show up in
    /// the Windows Application event log.
    /// </summary>
    public static class CrashLogger
    {
        public static readonly string FILENAME = "crash.log";

        private static readonly object gate = new();

        public static void Install()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Write("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            if (Application.Current != null)
                Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Write("DispatcherUnhandledException", e.Exception);

            // A failure while painting or handling input should not lose the user's session:
            // the translation loops live on their own threads and keep working.
            e.Handled = true;
            SnackbarHost.Show("[ERROR] Unexpected error", e.Exception.Message,
                SnackbarType.Error, timeout: 6, closeButton: true);
        }

        /// <summary>
        /// Records a milestone rather than a failure. Used sparingly, for sequences whose
        /// failure mode is leaving system state behind, where "nothing happened" is otherwise
        /// indistinguishable from "it worked".
        /// </summary>
        public static void Info(string message)
        {
            try
            {
                lock (gate)
                {
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, FILENAME),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}\n");
                }
            }
            catch (Exception)
            {
            }
        }

        public static void Write(string source, Exception? exception)
        {
            try
            {
                var entry = new StringBuilder()
                    .AppendLine("──────────────────────────────────────────────")
                    .AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {source}")
                    .AppendLine(exception?.ToString() ?? "(no exception object)")
                    .ToString();

                lock (gate)
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, FILENAME), entry);
            }
            catch (Exception)
            {
                // Logging must never be the thing that brings the app down.
            }
        }
    }
}
