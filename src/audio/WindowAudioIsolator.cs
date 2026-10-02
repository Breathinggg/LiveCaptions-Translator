using System.Diagnostics;

using NAudio.CoreAudioApi;
using NAudio.Wave;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.audio
{
    /// <summary>
    /// Restricts Live Captions to a single window or process.
    ///
    /// Live Captions has no audio source setting: it transcribes whatever reaches the default
    /// playback device, which is why several sound sources bleed into each other. The way out
    /// is to stop feeding it the default device at all:
    ///
    ///   target process --(plays normally)--> your speakers        untouched, no added latency
    ///          |
    ///          +--(process loopback copy)--> isolation endpoint --> Live Captions
    ///
    /// Live Captions is pinned to the isolation endpoint with a per-process default endpoint
    /// override, so it hears the copy and nothing else. The target application itself is never
    /// rerouted, which means a crash can never leave the user unable to hear it.
    ///
    /// The isolation endpoint has to be a render device nothing else plays into. A virtual
    /// cable is ideal because it is silent, but any spare physical output (an unused HDMI or
    /// S/PDIF port) works too.
    /// </summary>
    public static class WindowAudioIsolator
    {
        // Enough slack to ride out scheduling hiccups without adding audible delay for
        // Live Captions, which is the only consumer of this stream.
        private static readonly TimeSpan BUFFER_LIMIT = TimeSpan.FromMilliseconds(400);

        private static readonly object gate = new();

        private static ProcessLoopbackCapture? capture;
        private static BufferedWaveProvider? bufferedAudio;
        private static WasapiOut? output;
        private static System.Timers.Timer? watchdog;

        /// <summary>
        /// Tracks the override separately from <see cref="IsActive"/>: pinning happens before
        /// the relay exists, so a failure in between still has to be undone.
        /// </summary>
        private static bool liveCaptionsPinned;

        public static bool IsActive { get; private set; }
        public static AudioSource? Target { get; private set; }
        public static string? EndpointId { get; private set; }

        /// <summary>Raised whenever <see cref="IsActive"/> or <see cref="Target"/> changes.</summary>
        public static event Action? StateChanged;

        /// <summary>Raised with a user-facing message when isolation could not start or stopped by itself.</summary>
        public static event Action<string>? Failed;

        public static void Start(AudioSource target, string endpointId)
        {
            Stop();

            MMDevice? device = AudioEndpoints.GetDevice(endpointId)
                ?? throw new InvalidOperationException("The selected isolation device is no longer available.");

            using (var enumerator = new MMDeviceEnumerator())
            {
                string defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
                if (device.ID == defaultId)
                    throw new InvalidOperationException(
                        "The isolation device cannot be the system default playback device, " +
                        "because every other application plays into it as well.");
            }

            lock (gate)
            {
                Target = target;
                EndpointId = endpointId;

                try
                {
                    int captureProcessId = AudioSourceEnumerator.ResolveCaptureProcessId(target.ProcessId);
                    if (captureProcessId != target.ProcessId)
                        CrashLogger.Info($"isolation: {target.ProcessName} (pid {target.ProcessId}) has other " +
                            $"applications in its process tree, capturing only pid {captureProcessId}");

                    PinLiveCaptionsTo(endpointId);
                    StartRelay(captureProcessId, device);
                    StartWatchdog(captureProcessId);
                    IsActive = true;
                }
                catch (Exception)
                {
                    StopCore();
                    throw;
                }
            }

            StateChanged?.Invoke();
        }

        public static void Stop()
        {
            bool wasActive;
            lock (gate)
            {
                wasActive = IsActive;
                StopCore();
            }

            if (wasActive)
                StateChanged?.Invoke();
        }

        private static void StopCore()
        {
            watchdog?.Stop();
            watchdog?.Dispose();
            watchdog = null;

            capture?.Dispose();
            capture = null;

            output?.Dispose();
            output = null;
            bufferedAudio = null;

            IsActive = false;
            Target = null;
            EndpointId = null;

            // Keyed off the override itself, not off IsActive: Start() pins Live Captions
            // before the relay exists, and a failure in between must still be undone.
            if (liveCaptionsPinned)
                ReleaseLiveCaptions();
        }

        /// <summary>
        /// Drops an override left behind by a crash and restarts Live Captions if it found one.
        ///
        /// The override is persisted per application, so it outlives the process that set it.
        /// Left in place, Live Captions would come up pinned to a silent virtual cable with
        /// nothing feeding it and simply never caption anything again, with no visible reason.
        /// Runs at startup; costs nothing when there is nothing to clean up.
        /// </summary>
        public static void CleanupStaleOverride()
        {
            try
            {
                bool cleared = false;
                using (var policy = new AudioPolicyConfig())
                {
                    foreach (Process process in Process.GetProcessesByName(LiveCaptionsHandler.PROCESS_NAME))
                    {
                        if (policy.GetDefaultEndpoint(process.Id) == null)
                            continue;
                        policy.SetDefaultEndpoint(process.Id, null);
                        cleared = true;
                    }
                }

                if (cleared)
                    RestartLiveCaptions();
            }
            catch (Exception)
            {
                // Best effort: a stale override is recoverable, a crash at startup is not.
            }
        }

        /// <summary>
        /// Releases the override without restarting Live Captions, for use on application exit
        /// where the process is about to be killed anyway.
        /// </summary>
        public static void ReleaseForShutdown()
        {
            watchdog?.Stop();
            capture?.Dispose();
            capture = null;
            output?.Dispose();
            output = null;
            bufferedAudio = null;

            try
            {
                using var policy = new AudioPolicyConfig();
                foreach (Process process in Process.GetProcessesByName(LiveCaptionsHandler.PROCESS_NAME))
                    policy.SetDefaultEndpoint(process.Id, null);
                liveCaptionsPinned = false;
            }
            catch (Exception)
            {
                // Nothing useful can be done during shutdown; the next start cleans up.
            }
        }

        private static void PinLiveCaptionsTo(string endpointId)
        {
            using var policy = new AudioPolicyConfig();

            Process[] processes = Process.GetProcessesByName(LiveCaptionsHandler.PROCESS_NAME);
            if (processes.Length == 0)
                throw new InvalidOperationException("Live Captions is not running.");
            foreach (Process process in processes)
                policy.SetDefaultEndpoint(process.Id, endpointId);
            liveCaptionsPinned = true;

            // Live Captions opens its capture stream once at startup, so the new endpoint only
            // takes effect after a restart. The override is persisted per application, not per
            // process id, so the fresh instance comes up already pinned.
            RestartLiveCaptions();
        }

        private static void ReleaseLiveCaptions()
        {
            try
            {
                using var policy = new AudioPolicyConfig();
                foreach (Process process in Process.GetProcessesByName(LiveCaptionsHandler.PROCESS_NAME))
                    policy.SetDefaultEndpoint(process.Id, null);
                liveCaptionsPinned = false;
                RestartLiveCaptions();
            }
            catch (Exception ex)
            {
                Failed?.Invoke($"Could not restore Live Captions to the default device: {ex.Message}");
            }
        }

        private static void RestartLiveCaptions()
        {
            Translator.Window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Translator.Window);
            LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
        }

        private static void StartRelay(int processId, MMDevice device)
        {
            capture = new ProcessLoopbackCapture(processId);

            bufferedAudio = new BufferedWaveProvider(capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            capture.DataAvailable += (buffer, count) =>
            {
                BufferedWaveProvider? sink = bufferedAudio;
                if (sink == null)
                    return;

                // Capture and render run off independent clocks. Without this the backlog only
                // ever grows, and Live Captions ends up transcribing audio from minutes ago.
                if (sink.BufferedDuration > BUFFER_LIMIT)
                    sink.ClearBuffer();

                sink.AddSamples(buffer, 0, count);
            };
            capture.Faulted += ex => FailAsync($"Audio capture stopped: {ex.Message}");

            output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
            output.Init(bufferedAudio);
            output.Play();

            capture.Start();
        }

        private static void StartWatchdog(int processId)
        {
            watchdog = new System.Timers.Timer(2000) { AutoReset = true };
            watchdog.Elapsed += (_, _) =>
            {
                try
                {
                    Process.GetProcessById(processId);
                }
                catch (ArgumentException)
                {
                    FailAsync("The captured application has exited, so window isolation was turned off.");
                }
            };
            watchdog.Start();
        }

        private static void FailAsync(string message)
        {
            // Never tear down from a capture or timer callback: Stop() joins those very threads.
            Task.Run(() =>
            {
                Stop();
                Failed?.Invoke(message);
            });
        }
    }
}
