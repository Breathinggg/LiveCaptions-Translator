using System.IO;

using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioRouteProbe
{
    /// <summary>
    /// Plays one wav file to one specific endpoint, on repeat, until cancelled.
    /// Two of these aimed at two different endpoints is the whole experiment: whichever
    /// sentence shows up in Live Captions tells us which endpoint it is really listening to.
    /// </summary>
    public sealed class WavPlayer
    {
        private readonly MMDevice device;
        private readonly byte[] wav;

        public WavPlayer(MMDevice device, string wavPath)
        {
            if (!File.Exists(wavPath))
                throw new FileNotFoundException($"Missing test audio: {wavPath}. Run tools/make-test-audio.ps1 first.");

            this.device = device;
            wav = File.ReadAllBytes(wavPath);
        }

        public async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PlayOnceAsync(token);
                    // A gap between repetitions lets Live Captions finalise a sentence.
                    await Task.Delay(1200, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{device.FriendlyName}] playback failed: {ex.Message}");
                    return;
                }
            }
        }

        private async Task PlayOnceAsync(CancellationToken token)
        {
            using var reader = new WaveFileReader(new MemoryStream(wav));
            using var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);

            var finished = new TaskCompletionSource();
            output.PlaybackStopped += (_, _) => finished.TrySetResult();

            output.Init(reader);
            output.Play();

            using (token.Register(() =>
            {
                try { output.Stop(); } catch (Exception) { /* already stopped */ }
            }))
            {
                await finished.Task;
            }
        }
    }
}
