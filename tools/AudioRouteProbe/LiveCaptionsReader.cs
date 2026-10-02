using System.Diagnostics;
using System.Windows.Automation;

namespace AudioRouteProbe
{
    /// <summary>
    /// Minimal stand-in for the main project's LiveCaptionsHandler: launches LiveCaptions.exe
    /// and scrapes its caption text through UI Automation. Kept separate so the probe can run
    /// without dragging in the WPF app.
    /// </summary>
    public static class LiveCaptionsReader
    {
        public const string PROCESS_NAME = "LiveCaptions";
        private const string WINDOW_CLASS = "LiveCaptionsDesktopWindow";

        private static AutomationElement? captionsTextBlock;

        public static void KillAll()
        {
            foreach (Process process in Process.GetProcessesByName(PROCESS_NAME))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }
        }

        /// <summary>Starts LiveCaptions and waits for its window to become reachable via UIA.</summary>
        public static (AutomationElement window, int processId) Launch(int timeoutMs = 20000)
        {
            KillAll();
            captionsTextBlock = null;

            Process.Start(PROCESS_NAME);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                foreach (Process process in Process.GetProcessesByName(PROCESS_NAME))
                {
                    AutomationElement? window = FindWindowByProcessId(process.Id);
                    if (window == null)
                        continue;
                    try
                    {
                        if (window.Current.ClassName == WINDOW_CLASS)
                            return (window, process.Id);
                    }
                    catch (ElementNotAvailableException)
                    {
                        // Window died between the lookup and the property read.
                    }
                }
                Thread.Sleep(200);
            }

            throw new TimeoutException($"LiveCaptions window did not appear within {timeoutMs} ms.");
        }

        public static string GetCaptions(AutomationElement window)
        {
            if (captionsTextBlock == null)
            {
                captionsTextBlock = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "CaptionsTextBlock"));
            }
            try
            {
                return captionsTextBlock?.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                captionsTextBlock = null;
                return string.Empty;
            }
        }

        private static AutomationElement? FindWindowByProcessId(int processId)
        {
            return AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId));
        }
    }
}
