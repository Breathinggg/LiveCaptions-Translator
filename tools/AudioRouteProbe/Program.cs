using System.Diagnostics;
using System.IO;
using System.Text;

using NAudio.CoreAudioApi;
using NAudio.Wave;

using LiveCaptionsTranslator.audio;

namespace AudioRouteProbe
{
    internal static class Program
    {
        /// <summary>
        /// One test clip: the wav to play and the words that identify it in a transcript.
        /// Within a language the two clips share no vocabulary on purpose.
        /// </summary>
        private sealed record Clip(string Wav, string Text, string[] Keywords);

        private static readonly Dictionary<string, (Clip Target, Clip Distractor)> CLIPS = new()
        {
            ["en"] = (
                new Clip("target_en.wav",
                    "The quick brown fox jumps over the lazy dog near the river bank.",
                    new[] { "fox", "lazy", "river", "brown" }),
                new Clip("distractor_en.wav",
                    "Sphinx of black quartz, judge my vow, said the zebra to the wizard.",
                    new[] { "sphinx", "quartz", "zebra", "wizard" })),
            ["ja"] = (
                new Clip("target_ja.wav",
                    "赤い風船が公園の空へゆっくり飛んでいきました。",
                    new[] { "風船", "公園", "赤い", "空" }),
                new Clip("distractor_ja.wav",
                    "黒い自転車で図書館まで手紙を届けました。",
                    new[] { "自転車", "図書館", "手紙", "黒い" })),
            ["zh"] = (
                new Clip("target_zh.wav",
                    "红色的气球慢慢飞向公园的天空。",
                    new[] { "气球", "公园", "天空", "红色" }),
                new Clip("distractor_zh.wav",
                    "他骑着黑色自行车把信件送到图书馆。",
                    new[] { "自行车", "图书馆", "信件", "黑色" })),
        };

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "list":
                        return ListEndpoints();
                    case "get":
                        return GetOverride(args);
                    case "set":
                        return SetOverride(args);
                    case "clear":
                        return ClearOverride(args);
                    case "experiment":
                        return Experiment(args);
                    case "isolate":
                        return Isolate(args);
                    case "diag":
                        return Diag(args);
                    case "play":
                        return Play(args);
                    case "capture":
                        return Capture(args);
                    default:
                        PrintUsage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
                return 2;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("""
                AudioRouteProbe - checks whether Live Captions honours per-process endpoint overrides.

                  list                                  List active render endpoints with their index.
                  get   <pid|processName>               Show the endpoint currently pinned to a process.
                  set   <pid|processName> <index>       Pin a process to an endpoint.
                  clear <pid|processName>               Release a process back to the system default.

                  experiment <pinnedIdx> <defaultIdx> [seconds] [--lang en|ja|zh] [--solo] [--swap]

                        Pins Live Captions to <pinnedIdx>, then plays two different sentences into
                        <pinnedIdx> and <defaultIdx> at the same time and reports which one reached
                        the captions.
                          --lang  language of the test clips; must match the recognizer Live Captions
                                  is currently set to, otherwise the transcript is meaningless.
                          --solo  play only into the pinned endpoint.
                          --swap  exchange the two clips while keeping the pinning, as a control run.

                  isolate <isolationIdx> [seconds] [--lang en|ja|zh]

                        End to end check of the shipping pipeline. Starts a separate player
                        process on the normal speakers, captures only that process with
                        WASAPI process loopback, relays it to <isolationIdx>, and plays a
                        second sentence on the same speakers from this process. Live Captions
                        must transcribe only the captured process.
                """);
        }

        private static List<MMDevice> ActiveRenderEndpoints()
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        }

        private static int ListEndpoints()
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;

            List<MMDevice> devices = ActiveRenderEndpoints();
            for (int i = 0; i < devices.Count; i++)
            {
                string marker = devices[i].ID == defaultId ? " [SYSTEM DEFAULT]" : "";
                Console.WriteLine($"  [{i,2}] {devices[i].FriendlyName}{marker}");
                Console.WriteLine($"       {devices[i].ID}");
            }
            return 0;
        }

        private static int ResolveProcessId(string token)
        {
            if (int.TryParse(token, out int pid))
                return pid;

            Process[] processes = Process.GetProcessesByName(token);
            if (processes.Length == 0)
                throw new ArgumentException($"No running process named \"{token}\".");
            return processes[0].Id;
        }

        private static int GetOverride(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            int pid = ResolveProcessId(args[1]);
            using var policy = new AudioPolicyConfig();
            string? endpointId = policy.GetDefaultEndpoint(pid);

            if (endpointId == null)
            {
                Console.WriteLine($"pid {pid}: follows the system default endpoint.");
                return 0;
            }

            string name = ActiveRenderEndpoints().FirstOrDefault(d => d.ID == endpointId)?.FriendlyName
                          ?? "(endpoint not currently active)";
            Console.WriteLine($"pid {pid}: pinned to {name}");
            Console.WriteLine($"          {endpointId}");
            return 0;
        }

        private static int SetOverride(string[] args)
        {
            if (args.Length < 3)
            {
                PrintUsage();
                return 1;
            }

            int pid = ResolveProcessId(args[1]);
            MMDevice device = ActiveRenderEndpoints()[int.Parse(args[2])];

            using var policy = new AudioPolicyConfig();
            policy.SetDefaultEndpoint(pid, device.ID);

            Console.WriteLine($"pid {pid} -> {device.FriendlyName}");
            Console.WriteLine($"read back: {policy.GetDefaultEndpoint(pid) ?? "(none)"}");
            return 0;
        }

        private static int ClearOverride(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            int pid = ResolveProcessId(args[1]);
            using var policy = new AudioPolicyConfig();
            policy.SetDefaultEndpoint(pid, null);

            Console.WriteLine($"pid {pid} released. read back: {policy.GetDefaultEndpoint(pid) ?? "(none)"}");
            return 0;
        }

        private static int Experiment(string[] args)
        {
            if (args.Length < 3)
            {
                PrintUsage();
                return 1;
            }

            List<MMDevice> devices = ActiveRenderEndpoints();
            MMDevice pinned = devices[int.Parse(args[1])];
            MMDevice systemDefault = devices[int.Parse(args[2])];
            int seconds = args.Length > 3 && int.TryParse(args[3], out int s) ? s : 45;
            bool solo = args.Contains("--solo");
            bool swap = args.Contains("--swap");

            int langIndex = Array.IndexOf(args, "--lang");
            string lang = langIndex >= 0 && langIndex + 1 < args.Length ? args[langIndex + 1] : "en";
            if (!CLIPS.TryGetValue(lang, out var clips))
                throw new ArgumentException($"Unknown language \"{lang}\". Use en, ja or zh.");

            // --swap keeps the pinning but exchanges the clips. If isolation is real, the
            // captions must follow the endpoint, not the sentence.
            Clip intoPinned = swap ? clips.Distractor : clips.Target;
            Clip intoDefault = swap ? clips.Target : clips.Distractor;

            Console.WriteLine($"language           : {lang}{(swap ? "   [SWAPPED CONTROL RUN]" : "")}");
            Console.WriteLine($"pinned endpoint    : {pinned.FriendlyName}");
            Console.WriteLine($"default endpoint   : {systemDefault.FriendlyName}");
            Console.WriteLine();

            using var policy = new AudioPolicyConfig();

            Console.WriteLine("[1/5] launching Live Captions...");
            var (_, warmupPid) = LiveCaptionsReader.Launch();
            Console.WriteLine($"      pid {warmupPid}");

            Console.WriteLine("[2/5] pinning it to the isolation endpoint...");
            policy.SetDefaultEndpoint(warmupPid, pinned.ID);
            string? readBack = policy.GetDefaultEndpoint(warmupPid);
            Console.WriteLine($"      read back: {readBack ?? "(none)"}");
            if (readBack != pinned.ID)
            {
                Console.WriteLine();
                Console.WriteLine("VERDICT: the override did not stick. Endpoint routing is not viable.");
                return 3;
            }

            // Live Captions opens its capture stream at startup, so it has to be restarted for
            // a new endpoint to take effect. This also shows whether the override survives a
            // restart, which decides whether we set it once or re-apply on every launch.
            Console.WriteLine("[3/5] restarting Live Captions so it picks the endpoint up...");
            var (window, pid) = LiveCaptionsReader.Launch();
            string? persisted = policy.GetDefaultEndpoint(pid);
            Console.WriteLine($"      new pid {pid}, override after restart: {persisted ?? "(none)"}");
            bool survivesRestart = persisted == pinned.ID;
            if (!survivesRestart)
            {
                Console.WriteLine("      override is per-pid only; re-applying to the new pid.");
                policy.SetDefaultEndpoint(pid, pinned.ID);
            }

            Console.WriteLine($"[4/5] speaking for {seconds}s...");
            Console.WriteLine($"      into pinned  : \"{intoPinned.Text}\"");
            if (!solo)
                Console.WriteLine($"      into default : \"{intoDefault.Text}\"");
            Console.WriteLine();

            string assets = AppContext.BaseDirectory;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var players = new List<Task>
            {
                new WavPlayer(pinned, Path.Combine(assets, intoPinned.Wav)).RunAsync(cts.Token)
            };
            if (!solo)
                players.Add(new WavPlayer(systemDefault, Path.Combine(assets, intoDefault.Wav)).RunAsync(cts.Token));

            string captions = string.Empty;
            string lastLine = string.Empty;
            while (!cts.Token.IsCancellationRequested)
            {
                string current = LiveCaptionsReader.GetCaptions(window);
                if (!string.IsNullOrWhiteSpace(current))
                {
                    captions = current;
                    // The caption block holds the whole visible history; only the tail is new.
                    string tail = current.Split('\n').Last().Trim();
                    if (tail.Length > 0 && tail != lastLine)
                    {
                        Console.WriteLine($"      captions> {tail}");
                        lastLine = tail;
                    }
                }
                Thread.Sleep(250);
            }

            try
            {
                Task.WaitAll(players.ToArray(), TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
                // Cancellation noise.
            }

            Console.WriteLine();
            Console.WriteLine("[5/5] releasing the override...");
            policy.SetDefaultEndpoint(pid, null);

            string heard = captions.ToLowerInvariant();
            int pinnedHits = intoPinned.Keywords.Count(k => heard.Contains(k.ToLowerInvariant()));
            int defaultHits = intoDefault.Keywords.Count(k => heard.Contains(k.ToLowerInvariant()));

            Console.WriteLine();
            Console.WriteLine("--- full caption text ---");
            Console.WriteLine(captions);
            Console.WriteLine("-------------------------");
            Console.WriteLine();
            Console.WriteLine($"keywords from the pinned endpoint's clip  : {pinnedHits}/{intoPinned.Keywords.Length}");
            Console.WriteLine($"keywords from the default endpoint's clip : {defaultHits}/{intoDefault.Keywords.Length}");
            Console.WriteLine($"override survives restart                 : {survivesRestart}");
            Console.WriteLine();

            if (pinnedHits > 0 && defaultHits == 0)
            {
                Console.WriteLine("VERDICT: isolation works. Live Captions followed the per-process override");
                Console.WriteLine("         and heard only the pinned endpoint.");
                return 0;
            }
            if (pinnedHits > 0 && defaultHits > 0)
            {
                Console.WriteLine("VERDICT: no isolation. Live Captions heard both endpoints.");
                return 4;
            }
            if (pinnedHits == 0 && defaultHits > 0)
            {
                Console.WriteLine("VERDICT: override ignored. Live Captions stayed on the system default.");
                return 5;
            }
            Console.WriteLine("VERDICT: inconclusive - nothing recognisable was captured. Check that Live");
            Console.WriteLine($"         Captions is set to \"{lang}\" and is not paused.");
            return 6;
        }

        /// <summary>
        /// Exercises the shipping pipeline: process loopback capture of one process, relayed
        /// into an endpoint that only Live Captions listens to, while a second sound plays on
        /// the same speakers. This is the user's actual scenario - several sources at once.
        /// </summary>
        private static int Isolate(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            List<MMDevice> devices = ActiveRenderEndpoints();
            MMDevice isolation = devices[int.Parse(args[1])];
            int seconds = args.Length > 2 && int.TryParse(args[2], out int s) ? s : 45;

            int langIndex = Array.IndexOf(args, "--lang");
            string lang = langIndex >= 0 && langIndex + 1 < args.Length ? args[langIndex + 1] : "ja";
            if (!CLIPS.TryGetValue(lang, out var clips))
                throw new ArgumentException($"Unknown language \"{lang}\". Use en, ja or zh.");

            using var enumerator = new MMDeviceEnumerator();
            MMDevice speakers = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            string assets = AppContext.BaseDirectory;

            Console.WriteLine($"isolation endpoint : {isolation.FriendlyName}");
            Console.WriteLine($"speakers           : {speakers.FriendlyName}  (both clips play here)");
            Console.WriteLine();

            Console.WriteLine("[1/5] starting the process to be captured...");
            Process player = StartLoopingPlayer(Path.Combine(assets, clips.Target.Wav));
            Console.WriteLine($"      pid {player.Id} is now playing \"{clips.Target.Text}\" on the speakers");

            using var policy = new AudioPolicyConfig();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            ProcessLoopbackCapture? capture = null;
            WasapiOut? relay = null;

            try
            {
                Console.WriteLine("[2/5] pinning Live Captions to the isolation endpoint...");
                var (_, warmupPid) = LiveCaptionsReader.Launch();
                policy.SetDefaultEndpoint(warmupPid, isolation.ID);
                var (window, pid) = LiveCaptionsReader.Launch();
                Console.WriteLine($"      pid {pid}, override: {policy.GetDefaultEndpoint(pid) ?? "(none)"}");

                Console.WriteLine("[3/5] capturing that process only, relaying into the isolation endpoint...");
                capture = new ProcessLoopbackCapture(player.Id);
                var buffered = new BufferedWaveProvider(capture.WaveFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true,
                    ReadFully = true
                };

                long captured = 0;
                capture.DataAvailable += (buffer, count) =>
                {
                    captured += count;
                    if (buffered.BufferedDuration > TimeSpan.FromMilliseconds(400))
                        buffered.ClearBuffer();
                    buffered.AddSamples(buffer, 0, count);
                };
                capture.Faulted += ex => Console.WriteLine($"      capture faulted: {ex.Message}");

                relay = new WasapiOut(isolation, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
                relay.Init(buffered);
                relay.Play();
                capture.Start();

                Console.WriteLine("[4/5] adding a second, uncaptured sound on the same speakers...");
                Console.WriteLine($"      \"{clips.Distractor.Text}\"");
                Console.WriteLine();
                var distractor = new WavPlayer(speakers, Path.Combine(assets, clips.Distractor.Wav))
                    .RunAsync(cts.Token);

                string captions = string.Empty;
                string lastLine = string.Empty;
                while (!cts.Token.IsCancellationRequested)
                {
                    string current = LiveCaptionsReader.GetCaptions(window);
                    if (!string.IsNullOrWhiteSpace(current))
                    {
                        captions = current;
                        string tail = current.Split('\n').Last().Trim();
                        if (tail.Length > 0 && tail != lastLine)
                        {
                            Console.WriteLine($"      captions> {tail}");
                            lastLine = tail;
                        }
                    }
                    Thread.Sleep(250);
                }

                try
                {
                    distractor.Wait(TimeSpan.FromSeconds(10));
                }
                catch (AggregateException)
                {
                    // Cancellation noise.
                }

                Console.WriteLine();
                Console.WriteLine($"[5/5] captured {captured / 1024} KB from pid {player.Id}; cleaning up...");
                policy.SetDefaultEndpoint(pid, null);

                string heard = captions.ToLowerInvariant();
                int capturedHits = clips.Target.Keywords.Count(k => heard.Contains(k.ToLowerInvariant()));
                int uncapturedHits = clips.Distractor.Keywords.Count(k => heard.Contains(k.ToLowerInvariant()));

                Console.WriteLine();
                Console.WriteLine("--- full caption text ---");
                Console.WriteLine(captions);
                Console.WriteLine("-------------------------");
                Console.WriteLine();
                Console.WriteLine($"keywords from the captured process   : {capturedHits}/{clips.Target.Keywords.Length}");
                Console.WriteLine($"keywords from the uncaptured process : {uncapturedHits}/{clips.Distractor.Keywords.Length}");
                Console.WriteLine();

                if (captured == 0)
                {
                    Console.WriteLine("VERDICT: process loopback captured no audio at all.");
                    return 7;
                }
                if (capturedHits > 0 && uncapturedHits == 0)
                {
                    Console.WriteLine("VERDICT: works. Two sounds played on the same speakers, and Live Captions");
                    Console.WriteLine("         transcribed only the process that was captured.");
                    return 0;
                }
                if (capturedHits > 0)
                {
                    Console.WriteLine("VERDICT: leaking. Live Captions also heard the uncaptured process.");
                    return 4;
                }
                Console.WriteLine("VERDICT: inconclusive - the captured process never reached the captions.");
                return 6;
            }
            finally
            {
                capture?.Dispose();
                relay?.Dispose();
                try
                {
                    player.Kill();
                    player.WaitForExit(3000);
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }
        }

        /// <summary>
        /// Runs the shipping capture engine against one process and reports how loud the
        /// captured audio actually is. Distinguishes "capture never started", "capture runs but
        /// only returns silence", and "capture works".
        /// </summary>
        private static int Capture(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            int targetPid = ResolveProcessId(args[1]);
            int seconds = args.Length > 2 && int.TryParse(args[2], out int s) ? s : 8;
            bool excludeMode = args.Contains("--exclude");

            Console.WriteLine($"capturing pid {targetPid} ({GetProcessName(targetPid)}) for {seconds}s" +
                              (excludeMode ? " [EXCLUDE mode]" : " [INCLUDE tree]"));

            long totalBytes = 0;
            long nonSilentBytes = 0;
            short peak = 0;
            Exception? fault = null;

            using var capture = new ProcessLoopbackCapture(targetPid,
                excludeMode ? ProcessLoopbackMode.ExcludeTargetProcessTree
                            : ProcessLoopbackMode.IncludeTargetProcessTree);

            capture.DataAvailable += (buffer, count) =>
            {
                totalBytes += count;
                bool silent = true;
                for (int i = 0; i + 1 < count; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    int magnitude = Math.Abs((int)sample);
                    if (magnitude > peak)
                        peak = (short)Math.Min(magnitude, short.MaxValue);
                    if (magnitude > 32)
                        silent = false;
                }
                if (!silent)
                    nonSilentBytes += count;
            };
            capture.Faulted += ex => fault = ex;

            capture.Start();
            Thread.Sleep(seconds * 1000);
            capture.Stop();

            Console.WriteLine();
            if (fault != null)
            {
                Console.WriteLine($"VERDICT: capture faulted - {fault.GetType().Name}: {fault.Message}");
                return 9;
            }

            Console.WriteLine($"bytes delivered   : {totalBytes / 1024} KB");
            Console.WriteLine($"of which non-silent: {nonSilentBytes / 1024} KB");
            Console.WriteLine($"peak sample       : {peak} / 32767  ({peak / 327.67:F1}%)");
            Console.WriteLine();

            if (totalBytes == 0)
            {
                Console.WriteLine("VERDICT: the stream ran but delivered no packets at all.");
                return 7;
            }
            if (nonSilentBytes == 0)
            {
                Console.WriteLine("VERDICT: packets arrive but are pure silence - this process tree is");
                Console.WriteLine("         not the one rendering the audio, or it is not being included.");
                return 8;
            }
            Console.WriteLine("VERDICT: capture works.");
            return 0;
        }

        /// <summary>
        /// Renders a clip into one endpoint and reports whether its meter actually moved.
        /// Separates "the relay never captured anything" from "the endpoint swallowed it",
        /// which look identical from outside.
        /// </summary>
        private static int Play(string[] args)
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            List<MMDevice> devices = ActiveRenderEndpoints();
            MMDevice device = devices[int.Parse(args[1])];
            int seconds = args.Length > 2 && int.TryParse(args[2], out int s) ? s : 8;

            WaveFormat mix;
            try
            {
                mix = device.AudioClient.MixFormat;
                Console.WriteLine($"{device.FriendlyName}: mix format {mix.SampleRate} Hz, " +
                                  $"{mix.Channels} ch, {mix.BitsPerSample} bit, {mix.Encoding}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{device.FriendlyName}: mix format unavailable ({ex.Message})");
            }

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            Task playing = new WavPlayer(device, Path.Combine(AppContext.BaseDirectory, "target_ja.wav"))
                .RunAsync(cts.Token);

            double peak = 0;
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                try
                {
                    peak = Math.Max(peak, device.AudioMeterInformation.MasterPeakValue);
                }
                catch (Exception)
                {
                }
                Thread.Sleep(50);
            }

            try
            {
                playing.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException ex)
            {
                Console.WriteLine($"playback error: {ex.InnerException?.Message}");
            }

            Console.WriteLine($"peak on this endpoint while playing: {peak:F4}");
            Console.WriteLine(peak > 0.0001
                ? "VERDICT: this endpoint accepts our stereo stream."
                : "VERDICT: nothing reached this endpoint - rendering into it silently fails.");
            return peak > 0.0001 ? 0 : 8;
        }

        /// <summary>
        /// Samples live signal levels so "no captions" can be traced to a specific link in the
        /// chain: is the target actually playing, and is anything arriving at the endpoint
        /// Live Captions was pinned to.
        /// </summary>
        private static int Diag(string[] args)
        {
            int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 8;

            using var enumerator = new MMDeviceEnumerator();
            List<MMDevice> devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

            var endpointPeaks = new double[devices.Count];
            var sessionPeaks = new Dictionary<string, double>();

            Console.WriteLine($"sampling for {seconds}s...");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                for (int i = 0; i < devices.Count; i++)
                {
                    try
                    {
                        endpointPeaks[i] = Math.Max(endpointPeaks[i], devices[i].AudioMeterInformation.MasterPeakValue);
                    }
                    catch (Exception)
                    {
                        // Endpoint went away mid-sample.
                    }

                    try
                    {
                        SessionCollection sessions = devices[i].AudioSessionManager.Sessions;
                        for (int j = 0; j < sessions.Count; j++)
                        {
                            AudioSessionControl session = sessions[j];
                            int pid = (int)session.GetProcessID;
                            if (pid == 0)
                                continue;
                            string key = $"{GetProcessName(pid)} (pid {pid}) -> {devices[i].FriendlyName}";
                            double peak = session.AudioMeterInformation.MasterPeakValue;
                            sessionPeaks[key] = Math.Max(sessionPeaks.GetValueOrDefault(key), peak);
                        }
                    }
                    catch (Exception)
                    {
                        // Not every endpoint allows session enumeration.
                    }
                }
                Thread.Sleep(50);
            }

            Console.WriteLine();
            Console.WriteLine("--- render endpoints (peak) ---");
            for (int i = 0; i < devices.Count; i++)
            {
                if (endpointPeaks[i] > 0.0001)
                    Console.WriteLine($"  [{i,2}] {endpointPeaks[i],8:F4}  {devices[i].FriendlyName}");
            }
            if (endpointPeaks.All(p => p <= 0.0001))
                Console.WriteLine("  (silence everywhere)");

            Console.WriteLine();
            Console.WriteLine("--- application sessions (peak) ---");
            foreach (var entry in sessionPeaks.Where(e => e.Value > 0.0001).OrderByDescending(e => e.Value))
                Console.WriteLine($"  {entry.Value,8:F4}  {entry.Key}");
            if (!sessionPeaks.Any(e => e.Value > 0.0001))
                Console.WriteLine("  (no application produced sound)");

            return 0;
        }

        private static string GetProcessName(int processId)
        {
            try
            {
                return Process.GetProcessById(processId).ProcessName;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>
        /// A separate process whose audio we can capture. It plays to the default device, so
        /// the machine is genuinely mixing two sources during the test.
        /// </summary>
        private static Process StartLoopingPlayer(string wavPath)
        {
            string script =
                $"$p = New-Object System.Media.SoundPlayer '{wavPath}'; " +
                "while ($true) { $p.PlaySync(); Start-Sleep -Milliseconds 700 }";

            var player = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Could not start the player process.");

            // Give SoundPlayer time to open its stream, otherwise loopback activation races it.
            Thread.Sleep(2000);
            return player;
        }
    }
}
