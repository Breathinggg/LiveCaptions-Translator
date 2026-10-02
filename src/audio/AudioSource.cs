using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

using NAudio.CoreAudioApi;

namespace LiveCaptionsTranslator.audio
{
    public enum AudioSourceKind
    {
        /// <summary>Picked from the list of visible windows.</summary>
        Window,
        /// <summary>Picked from the list of processes currently rendering audio.</summary>
        Session
    }

    /// <summary>
    /// One entry in the source picker. Both kinds ultimately resolve to a process id, which is
    /// what process loopback capture needs; the two lists exist because neither alone is
    /// sufficient. A window is the natural thing for a user to point at, but browsers and UWP
    /// apps render audio from a child process that owns no window, so those only ever show up
    /// in the session list.
    /// </summary>
    public class AudioSource : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private float peak;

        public AudioSourceKind Kind { get; init; }
        public int ProcessId { get; init; }
        public string ProcessName { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public nint WindowHandle { get; init; }
        public ImageSource? Icon { get; init; }

        /// <summary>Live output level, only meaningful for session sources.</summary>
        public float Peak
        {
            get => peak;
            set
            {
                peak = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PeakPercent));
            }
        }

        public double PeakPercent => Math.Min(100.0, Math.Sqrt(peak) * 100.0);

        public bool IsSession => Kind == AudioSourceKind.Session;

        public string DisplayName => string.IsNullOrWhiteSpace(Title) ? ProcessName : Title;

        public string Subtitle => $"{ProcessName}.exe  ·  PID {ProcessId}";

        /// <summary>Held only for session sources, to read the live peak meter.</summary>
        internal AudioSessionControl? SessionControl { get; init; }

        /// <summary>
        /// Also the accessible name of the list item: without it, assistive tools and UI
        /// Automation report the type name instead of the application being offered.
        /// </summary>
        public override string ToString() => $"{DisplayName} ({ProcessName}.exe, PID {ProcessId})";

        public void RefreshPeak()
        {
            if (SessionControl == null)
                return;
            try
            {
                Peak = SessionControl.AudioMeterInformation.MasterPeakValue;
            }
            catch (Exception)
            {
                // The session went away between enumeration and now.
                Peak = 0;
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }
}
