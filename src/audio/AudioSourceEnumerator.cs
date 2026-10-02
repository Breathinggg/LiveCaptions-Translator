using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.audio
{
    /// <summary>
    /// Builds the two lists the source picker shows: visible windows, and processes that are
    /// currently rendering audio.
    /// </summary>
    public static class AudioSourceEnumerator
    {
        private const string APPLICATION_FRAME_HOST = "ApplicationFrameHost";

        // Helper processes that belong to the app that spawned them, as opposed to independent
        // applications that merely happen to have been launched from it.
        private static readonly HashSet<string> HELPER_EXECUTABLES =
            new(StringComparer.OrdinalIgnoreCase) { "msedgewebview2.exe" };

        public static List<AudioSource> EnumerateWindows()
        {
            var sources = new List<AudioSource>();
            int ownProcessId = Environment.ProcessId;

            WindowsAPI.EnumWindows((hWnd, _) =>
            {
                if (!WindowsAPI.IsWindowVisible(hWnd))
                    return true;

                int length = WindowsAPI.GetWindowTextLength(hWnd);
                if (length == 0)
                    return true;

                // Windows 10+ keeps suspended UWP apps around as invisible-but-"visible" windows.
                if (WindowsAPI.DwmGetWindowAttribute(hWnd, WindowsAPI.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                    && cloaked != 0)
                    return true;

                // Tool windows are palettes and popups, never something a user means to capture.
                int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);
                if ((exStyle & WindowsAPI.WS_EX_TOOLWINDOW) != 0)
                    return true;

                WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);

                // UWP apps such as Media Player draw inside a frame owned by ApplicationFrameHost,
                // which renders no audio; capturing it would yield silence. The app itself owns
                // the hosted CoreWindow child. A suspended app has none and is skipped.
                if (string.Equals(GetProcessName(processId), APPLICATION_FRAME_HOST, StringComparison.OrdinalIgnoreCase))
                {
                    processId = GetHostedAppProcessId(hWnd, processId);
                    if (processId == 0)
                        return true;
                }

                if (processId == ownProcessId || processId == 0)
                    return true;

                string processName = GetProcessName(processId);
                if (string.Equals(processName, LiveCaptionsHandler.PROCESS_NAME, StringComparison.OrdinalIgnoreCase))
                    return true;

                var title = new StringBuilder(length + 1);
                WindowsAPI.GetWindowText(hWnd, title, title.Capacity);

                sources.Add(new AudioSource
                {
                    Kind = AudioSourceKind.Window,
                    ProcessId = processId,
                    ProcessName = processName,
                    Title = title.ToString(),
                    WindowHandle = hWnd,
                    Icon = GetWindowIcon(hWnd) ?? GetProcessIcon(processId)
                });
                return true;
            }, IntPtr.Zero);

            return sources
                .OrderBy(s => s.ProcessName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Processes with an audio session on any active render endpoint. This is the fallback
        /// that makes browsers work: Chrome and Edge render from a utility child process which
        /// has no window of its own.
        /// </summary>
        public static List<AudioSource> EnumerateSessions()
        {
            var sources = new List<AudioSource>();
            var seen = new HashSet<int>();
            int ownProcessId = Environment.ProcessId;

            using var enumerator = new MMDeviceEnumerator();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                SessionCollection sessions;
                try
                {
                    sessions = device.AudioSessionManager.Sessions;
                }
                catch (Exception)
                {
                    // Some virtual endpoints refuse session enumeration.
                    continue;
                }

                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl session = sessions[i];
                    int processId;
                    try
                    {
                        processId = (int)session.GetProcessID;
                        if (session.State == AudioSessionState.AudioSessionStateExpired)
                            continue;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (processId == 0 || processId == ownProcessId || !seen.Add(processId))
                        continue;

                    string processName = GetProcessName(processId);
                    if (string.IsNullOrEmpty(processName) ||
                        string.Equals(processName, LiveCaptionsHandler.PROCESS_NAME, StringComparison.OrdinalIgnoreCase))
                        continue;

                    sources.Add(new AudioSource
                    {
                        Kind = AudioSourceKind.Session,
                        ProcessId = processId,
                        ProcessName = processName,
                        Title = DescribeSession(session, processId, processName),
                        Icon = GetProcessIcon(processId),
                        SessionControl = session
                    });
                }
            }

            return sources
                .OrderBy(s => s.ProcessName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Prefers the window title of the owning process, because a session's own display name
        /// is usually empty or an unhelpful resource string.
        /// </summary>
        private static string DescribeSession(AudioSessionControl session, int processId, string processName)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                if (!string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    return process.MainWindowTitle;
            }
            catch (Exception)
            {
                // Process exited or is not queryable.
            }

            try
            {
                string displayName = session.DisplayName;
                if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("@"))
                    return displayName;
            }
            catch (Exception)
            {
                // Not all sessions expose a name.
            }

            return processName;
        }

        private static int GetHostedAppProcessId(nint frameWindow, int frameProcessId)
        {
            int hostedProcessId = 0;
            WindowsAPI.EnumChildWindows(frameWindow, (child, _) =>
            {
                WindowsAPI.GetWindowThreadProcessId(child, out int childProcessId);
                if (childProcessId == 0 || childProcessId == frameProcessId)
                    return true;
                hostedProcessId = childProcessId;
                return false;
            }, IntPtr.Zero);
            return hostedProcessId;
        }

        /// <summary>
        /// Picks the process id to hand to process loopback capture for a chosen source.
        ///
        /// Capture covers the whole process tree, and the tree follows whoever launched a
        /// process. Clicking a Zoom or Teams meeting link in Chrome makes Zoom or Teams a child
        /// of Chrome, so capturing Chrome would capture the meeting as well. When the tree
        /// holds such an independent application, capture is narrowed to the process of the
        /// chosen application that actually renders audio (Chrome's audio service).
        /// Trees without one are captured whole, which keeps WebView2-based apps like Teams
        /// working: their audio comes from msedgewebview2 children.
        /// </summary>
        public static int ResolveCaptureProcessId(int processId)
        {
            Dictionary<int, (int Parent, string Exe)> processes = SnapshotProcesses();
            if (!processes.TryGetValue(processId, out var root))
                return processId;

            var tree = new List<int> { processId };
            for (int i = 0; i < tree.Count; i++)
            {
                foreach (var (pid, info) in processes)
                {
                    // pid != parent guards against the cycles that recycled ids can produce.
                    if (info.Parent == tree[i] && pid != tree[i] && !tree.Contains(pid))
                        tree.Add(pid);
                }
            }

            bool hasForeignApp = tree.Skip(1).Any(pid =>
                !string.Equals(processes[pid].Exe, root.Exe, StringComparison.OrdinalIgnoreCase)
                && !HELPER_EXECUTABLES.Contains(processes[pid].Exe));
            if (!hasForeignApp)
                return processId;

            Dictionary<int, bool> rendering = GetRenderingProcessIds();
            int[] candidates = tree
                .Where(pid => rendering.ContainsKey(pid)
                    && string.Equals(processes[pid].Exe, root.Exe, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(pid => rendering[pid])
                .ToArray();

            // Nothing of its own is playing yet; the whole tree is the best that can be done.
            return candidates.Length > 0 ? candidates[0] : processId;
        }

        private static Dictionary<int, (int Parent, string Exe)> SnapshotProcesses()
        {
            var processes = new Dictionary<int, (int, string)>();
            nint snapshot = WindowsAPI.CreateToolhelp32Snapshot(WindowsAPI.TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                return processes;
            try
            {
                var entry = new PROCESSENTRY32W
                {
                    dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32W>()
                };
                for (bool ok = WindowsAPI.Process32FirstW(snapshot, ref entry); ok;
                     ok = WindowsAPI.Process32NextW(snapshot, ref entry))
                {
                    processes[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
                }
            }
            finally
            {
                WindowsAPI.CloseHandle(snapshot);
            }
            return processes;
        }

        /// <summary>Process ids owning a render session, mapped to whether it is playing right now.</summary>
        private static Dictionary<int, bool> GetRenderingProcessIds()
        {
            var result = new Dictionary<int, bool>();
            using var enumerator = new MMDeviceEnumerator();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    SessionCollection sessions = device.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        AudioSessionControl session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateExpired)
                            continue;
                        int pid = (int)session.GetProcessID;
                        bool active = session.State == AudioSessionState.AudioSessionStateActive;
                        result[pid] = result.GetValueOrDefault(pid) || active;
                    }
                }
                catch (Exception)
                {
                    // Some virtual endpoints refuse session enumeration.
                }
            }
            return result;
        }

        public static string GetProcessName(int processId)
        {
            try
            {
                return Process.GetProcessById(processId).ProcessName;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static string? GetProcessPath(int processId)
        {
            nint handle = WindowsAPI.OpenProcess(WindowsAPI.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return WindowsAPI.QueryFullProcessImageName(handle, 0, buffer, ref size)
                    ? buffer.ToString()
                    : null;
            }
            finally
            {
                WindowsAPI.CloseHandle(handle);
            }
        }

        private static ImageSource? GetWindowIcon(nint hWnd)
        {
            nint hIcon = IntPtr.Zero;
            if (WindowsAPI.SendMessageTimeout(hWnd, WindowsAPI.WM_GETICON, WindowsAPI.ICON_SMALL2,
                    IntPtr.Zero, 0x0002, 200, out nint result) != IntPtr.Zero)
                hIcon = result;
            if (hIcon == IntPtr.Zero)
                hIcon = WindowsAPI.GetClassLongPtr(hWnd, WindowsAPI.GCLP_HICONSM);
            if (hIcon == IntPtr.Zero)
                hIcon = WindowsAPI.GetClassLongPtr(hWnd, WindowsAPI.GCLP_HICON);

            // Window and class icons belong to the window, so they must not be destroyed here.
            return hIcon == IntPtr.Zero ? null : ToImageSource(hIcon);
        }

        private static ImageSource? GetProcessIcon(int processId)
        {
            string? path = GetProcessPath(processId);
            if (string.IsNullOrEmpty(path))
                return null;

            var info = new SHFILEINFO();
            nint result = WindowsAPI.SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf(info),
                WindowsAPI.SHGFI_ICON | WindowsAPI.SHGFI_SMALLICON);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                return ToImageSource(info.hIcon);
            }
            finally
            {
                WindowsAPI.DestroyIcon(info.hIcon);
            }
        }

        private static ImageSource? ToImageSource(nint hIcon)
        {
            try
            {
                ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
