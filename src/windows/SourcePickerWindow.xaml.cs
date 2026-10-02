using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.audio;

namespace LiveCaptionsTranslator
{
    /// <summary>
    /// Picker for the single window or process to translate.
    ///
    /// Everything that touches the audio COM APIs runs on the thread pool, never on the UI
    /// thread. WPF windows are STA, and a synchronous cross-apartment call made from a UI
    /// thread that is not pumping messages deadlocks the application outright.
    /// </summary>
    public partial class SourcePickerWindow : FluentWindow
    {
        private readonly ObservableCollection<AudioSource> windows = new();
        private readonly ObservableCollection<AudioSource> sessions = new();
        private readonly System.Timers.Timer peakTimer;

        private bool isBusy;

        public SourcePickerWindow()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();

            WindowList.ItemsSource = windows;
            SessionList.ItemsSource = sessions;
            StatusText.Text = "Scanning windows and audio devices...";

            // Peaks are read off the UI thread as well; WPF marshals the resulting
            // PropertyChanged notifications back to the binding engine by itself.
            peakTimer = new System.Timers.Timer(250) { AutoReset = true };
            peakTimer.Elapsed += (_, _) =>
            {
                foreach (AudioSource source in sessions.ToArray())
                    source.RefreshPeak();
            };

            Loaded += async (_, _) => await ReloadAsync();
            WindowAudioIsolator.StateChanged += OnIsolatorStateChanged;
            Closed += (_, _) =>
            {
                peakTimer.Stop();
                peakTimer.Dispose();
                WindowAudioIsolator.StateChanged -= OnIsolatorStateChanged;
            };
        }

        private void OnIsolatorStateChanged()
        {
            // Posted, not invoked: this fires from a worker thread that the UI thread may be
            // waiting on, and a synchronous marshal would deadlock the pair.
            Dispatcher.BeginInvoke(UpdateStatus);
        }

        private async Task ReloadAsync()
        {
            peakTimer.Stop();
            SetBusy(true);

            (List<AudioEndpointInfo> endpoints, List<AudioSource> windowList, List<AudioSource> sessionList) =
                await Task.Run(() => (
                    AudioEndpoints.List(),
                    AudioSourceEnumerator.EnumerateWindows(),
                    AudioSourceEnumerator.EnumerateSessions()));

            string saved = Translator.Setting?.IsolationEndpointId ?? string.Empty;
            EndpointComboBox.ItemsSource = endpoints;
            EndpointComboBox.SelectedItem =
                endpoints.FirstOrDefault(e => e.Id == saved && e.IsUsable)
                ?? endpoints.FirstOrDefault(e => e.IsUsable);

            if (!endpoints.Any(e => e.IsUsable))
            {
                EndpointHint.Text =
                    "This PC has only one active playback device, so there is nowhere to isolate audio to. " +
                    "Enable a spare output (an unused HDMI or S/PDIF port), or install a virtual audio cable.";
            }

            string? remembered = Translator.Setting?.IsolationTargetProcess;

            windows.Clear();
            foreach (AudioSource source in windowList)
                windows.Add(source);
            WindowList.SelectedItem = windows.FirstOrDefault(s => s.ProcessName == remembered);

            sessions.Clear();
            foreach (AudioSource source in sessionList)
                sessions.Add(source);
            SessionList.SelectedItem = sessions.FirstOrDefault(s => s.ProcessName == remembered);

            SetBusy(false);
            UpdateStatus();
            peakTimer.Start();
        }

        private AudioSource? SelectedSource =>
            SourceTabs.SelectedIndex == 0
                ? WindowList.SelectedItem as AudioSource
                : SessionList.SelectedItem as AudioSource;

        private void SetBusy(bool busy)
        {
            isBusy = busy;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
            StartButton.IsEnabled = !busy;
            StopButton.IsEnabled = !busy;
        }

        private void UpdateStatus()
        {
            if (isBusy)
                return;

            bool hasUsableEndpoint = EndpointComboBox.SelectedItem is AudioEndpointInfo { IsUsable: true };

            StopButton.Visibility = WindowAudioIsolator.IsActive ? Visibility.Visible : Visibility.Collapsed;
            StartButton.IsEnabled = hasUsableEndpoint;

            StatusText.Text = WindowAudioIsolator.IsActive
                ? $"Currently translating: {WindowAudioIsolator.Target?.DisplayName}"
                : "Translating all system audio";
        }

        private void EndpointComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EndpointComboBox.SelectedItem is AudioEndpointInfo endpoint)
                EndpointHint.Text = endpoint.Hint;
            UpdateStatus();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await ReloadAsync();
        }

        private async void SourceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedSource != null)
                await StartIsolationAsync();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            await StartIsolationAsync();
        }

        private async Task StartIsolationAsync()
        {
            AudioSource? source = SelectedSource;
            if (source == null)
            {
                SnackbarHost.Show("No source selected", "Pick a window or a process first.",
                    SnackbarType.Warning, timeout: 2);
                return;
            }
            if (EndpointComboBox.SelectedItem is not AudioEndpointInfo endpoint || !endpoint.IsUsable)
            {
                SnackbarHost.Show("No isolation device", EndpointHint.Text, SnackbarType.Warning, timeout: 4);
                return;
            }

            SetBusy(true);
            try
            {
                // Restarts Live Captions and opens two WASAPI streams; seconds of blocking COM
                // work that must stay off the UI thread.
                await Task.Run(() => WindowAudioIsolator.Start(source, endpoint.Id));

                // Recorded because isolation changes system state that has to be undone later;
                // if the app dies, this is the only trace of what was left behind.
                utils.CrashLogger.Info(
                    $"isolation: on, capturing {source.ProcessName} (pid {source.ProcessId}), " +
                    $"Live Captions pinned to {endpoint.Name}");

                if (Translator.Setting != null)
                {
                    Translator.Setting.IsolationEndpointId = endpoint.Id;
                    Translator.Setting.IsolationTargetProcess = source.ProcessName;
                }

                SnackbarHost.Show("Window isolation on",
                    $"Only \"{source.DisplayName}\" is being translated now.", SnackbarType.Success, timeout: 3);

                SetBusy(false);
                Close();
            }
            catch (Exception ex)
            {
                SetBusy(false);
                UpdateStatus();
                utils.CrashLogger.Write("WindowAudioIsolator.Start", ex);
                SnackbarHost.Show("[ERROR] Could not isolate this window", ex.Message,
                    SnackbarType.Error, timeout: 6, closeButton: true);
            }
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true);
            await Task.Run(WindowAudioIsolator.Stop);
            SetBusy(false);
            UpdateStatus();

            SnackbarHost.Show("Window isolation off",
                "Back to translating all system audio.", SnackbarType.Info, timeout: 3);
        }
    }
}
