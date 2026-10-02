using System.Windows;

using LiveCaptionsTranslator.audio;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        App()
        {
            CrashLogger.Install();
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Translator.Setting?.Save();

            // A previous run that crashed while isolating a window would leave Live Captions
            // pinned to a device nothing plays into, and it would silently caption nothing.
            // Off the UI thread: this is an STA thread that is not pumping messages yet, so a
            // blocking cross-apartment COM call here would hang the application on startup.
            Task.Run(WindowAudioIsolator.CleanupStaleOverride);

            Task.Run(() => Translator.SyncLoop());
            Task.Run(() => Translator.TranslateLoop());
            Task.Run(() => Translator.DisplayLoop());
        }

        /// <summary>
        /// Where shutdown cleanup actually happens. ProcessExit handlers only get a couple of
        /// seconds before the runtime stops waiting, which is not enough to stop the capture
        /// thread and undo the endpoint override; OnExit has no such budget.
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            Cleanup();
            base.OnExit(e);
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            // Backstop for shutdown paths that bypass OnExit. If it does not finish in time,
            // CleanupStaleOverride picks up the leftovers on the next start.
            Cleanup();
        }

        private static bool cleanedUp;

        private static void Cleanup()
        {
            if (cleanedUp)
                return;
            cleanedUp = true;

            // Order matters. The loops relaunch LiveCaptions the moment it disappears, so they
            // have to be told to stop before anything below touches it.
            Translator.IsShuttingDown = true;
            Thread.Sleep(200);

            // The COM work runs on a pool thread so it cannot deadlock against this STA thread,
            // which is no longer pumping messages by the time shutdown reaches here.
            if (!Task.Run(WindowAudioIsolator.ReleaseForShutdown).Wait(TimeSpan.FromSeconds(8)))
                CrashLogger.Info("shutdown: releasing the endpoint override timed out");

            try
            {
                if (Translator.Window != null)
                    LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
            }
            catch (Exception ex)
            {
                CrashLogger.Write("Shutdown/RestoreLiveCaptions", ex);
            }

            try
            {
                // By process name rather than through Translator.Window, which is null whenever
                // the automation element was invalidated by a restart.
                LiveCaptionsHandler.KillAllLiveCaptions();
            }
            catch (Exception ex)
            {
                CrashLogger.Write("Shutdown/KillLiveCaptions", ex);
            }
        }
    }
}
