using System.Runtime.InteropServices;

using NAudio.Wave;

namespace LiveCaptionsTranslator.audio
{
    /// <summary>
    /// Captures the audio rendered by one process and its children, and nothing else.
    ///
    /// This is the same Windows facility OBS uses for its application audio capture source:
    /// ActivateAudioInterfaceAsync on a pseudo device, parameterised with a target process id.
    /// Requires Windows 10 build 20348 or later, which every machine that has Live Captions
    /// satisfies. Applications holding the endpoint in exclusive mode cannot be captured.
    /// </summary>
    public sealed class ProcessLoopbackCapture : IDisposable
    {
        // Live Captions resamples internally, so the capture format only needs to be something
        // every consumer accepts. 48 kHz stereo 16-bit is the safest common denominator.
        private const int SAMPLE_RATE = 48000;
        private const int CHANNELS = 2;
        private const int BITS_PER_SAMPLE = 16;

        private const long BUFFER_DURATION_HNS = 2_000_000;  // 200 ms, in 100 ns units.

        private readonly int targetProcessId;
        private readonly ProcessLoopbackMode mode;

        private Thread? captureThread;
        private volatile bool running;

        public WaveFormat WaveFormat { get; } = new WaveFormat(SAMPLE_RATE, BITS_PER_SAMPLE, CHANNELS);

        /// <summary>Raised on the capture thread with a buffer that is only valid for the duration of the call.</summary>
        public event Action<byte[], int>? DataAvailable;

        /// <summary>Raised once if capture could not be started or died unexpectedly.</summary>
        public event Action<Exception>? Faulted;

        public ProcessLoopbackCapture(int targetProcessId,
            ProcessLoopbackMode mode = ProcessLoopbackMode.IncludeTargetProcessTree)
        {
            this.targetProcessId = targetProcessId;
            this.mode = mode;
        }

        public void Start()
        {
            if (running)
                return;

            running = true;
            captureThread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = $"ProcessLoopbackCapture({targetProcessId})",
                Priority = ThreadPriority.AboveNormal
            };
            // ActivateAudioInterfaceAsync delivers its completion callback on an MTA thread;
            // an STA caller would deadlock waiting for it.
            captureThread.SetApartmentState(ApartmentState.MTA);
            captureThread.Start();
        }

        public void Stop()
        {
            running = false;
            captureThread?.Join(2000);
            captureThread = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private void CaptureLoop()
        {
            IAudioClient? audioClient = null;
            IAudioCaptureClient? captureClient = null;
            EventWaitHandle? bufferReady = null;

            try
            {
                audioClient = ActivateProcessLoopbackClient();

                var format = new WaveFormatEx
                {
                    wFormatTag = WasapiInterop.WAVE_FORMAT_PCM,
                    nChannels = CHANNELS,
                    nSamplesPerSec = SAMPLE_RATE,
                    wBitsPerSample = BITS_PER_SAMPLE,
                    nBlockAlign = (ushort)(CHANNELS * BITS_PER_SAMPLE / 8),
                    nAvgBytesPerSec = (uint)(SAMPLE_RATE * CHANNELS * BITS_PER_SAMPLE / 8),
                    cbSize = 0
                };

                int hr = audioClient.Initialize(
                    WasapiInterop.AUDCLNT_SHAREMODE_SHARED,
                    WasapiInterop.AUDCLNT_STREAMFLAGS_LOOPBACK |
                    WasapiInterop.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                    BUFFER_DURATION_HNS, 0, ref format, IntPtr.Zero);
                Marshal.ThrowExceptionForHR(hr);

                bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
                Marshal.ThrowExceptionForHR(
                    audioClient.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()));

                Marshal.ThrowExceptionForHR(
                    audioClient.GetService(WasapiInterop.IID_IAudioCaptureClient, out object service));
                captureClient = (IAudioCaptureClient)service;

                Marshal.ThrowExceptionForHR(audioClient.Start());

                int frameSize = format.nBlockAlign;
                byte[] buffer = new byte[SAMPLE_RATE * frameSize];  // one second of headroom

                while (running)
                {
                    // The event can be missed while the target is silent, so never block forever.
                    bufferReady.WaitOne(100);

                    while (running)
                    {
                        Marshal.ThrowExceptionForHR(captureClient.GetNextPacketSize(out uint packetFrames));
                        if (packetFrames == 0)
                            break;

                        hr = captureClient.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                        Marshal.ThrowExceptionForHR(hr);

                        int byteCount = (int)frames * frameSize;
                        if (byteCount > buffer.Length)
                            buffer = new byte[byteCount];

                        if ((flags & WasapiInterop.AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                            Array.Clear(buffer, 0, byteCount);
                        else
                            Marshal.Copy(data, buffer, 0, byteCount);

                        captureClient.ReleaseBuffer(frames);

                        if (byteCount > 0)
                            DataAvailable?.Invoke(buffer, byteCount);
                    }
                }

            }
            catch (Exception ex)
            {
                running = false;
                Faulted?.Invoke(ex);
            }
            finally
            {
                // Order matters. The audio engine holds the raw event handle for as long as the
                // stream is running, so the client has to be stopped and released before the
                // handle is closed. Closing it first lets the engine signal a handle the system
                // may already have recycled, which crashes the process at random and cannot be
                // caught. On the exception path Stop() has not run yet, so it is done here.
                if (audioClient != null)
                {
                    try
                    {
                        audioClient.Stop();
                        audioClient.Reset();
                    }
                    catch (Exception)
                    {
                        // The stream may never have reached a running state.
                    }
                }

                if (captureClient != null)
                    Marshal.ReleaseComObject(captureClient);
                if (audioClient != null)
                    Marshal.ReleaseComObject(audioClient);

                bufferReady?.Dispose();
            }
        }

        private IAudioClient ActivateProcessLoopbackClient()
        {
            var activationParams = new AudioClientActivationParams
            {
                ActivationType = AudioClientActivationType.ProcessLoopback,
                ProcessLoopbackParams = new AudioClientProcessLoopbackParams
                {
                    TargetProcessId = (uint)targetProcessId,
                    ProcessLoopbackMode = mode
                }
            };

            IntPtr paramsBlock = IntPtr.Zero;
            IntPtr propVariant = IntPtr.Zero;
            try
            {
                paramsBlock = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
                Marshal.StructureToPtr(activationParams, paramsBlock, false);
                propVariant = CreateBlobPropVariant(paramsBlock, Marshal.SizeOf<AudioClientActivationParams>());

                var handler = new ActivationHandler();
                WasapiInterop.ActivateAudioInterfaceAsync(
                    WasapiInterop.VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                    WasapiInterop.IID_IAudioClient,
                    propVariant,
                    handler,
                    out IActivateAudioInterfaceAsyncOperation operation);

                if (!handler.Completed.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Timed out activating the process loopback audio client.");

                Marshal.ReleaseComObject(operation);

                if (handler.Error != null)
                    throw handler.Error;
                Marshal.ThrowExceptionForHR(handler.ActivateResult);

                return handler.AudioClient
                       ?? throw new InvalidOperationException("Process loopback activation returned no interface.");
            }
            finally
            {
                if (propVariant != IntPtr.Zero)
                    Marshal.FreeHGlobal(propVariant);
                if (paramsBlock != IntPtr.Zero)
                    Marshal.FreeHGlobal(paramsBlock);
            }
        }

        /// <summary>
        /// Builds a VT_BLOB PROPVARIANT by hand. The union inside PROPVARIANT starts at offset 8
        /// and the BLOB pointer is pointer-aligned within it, so the layout differs between 32
        /// and 64 bit and cannot be expressed as a single blittable struct.
        /// </summary>
        private static IntPtr CreateBlobPropVariant(IntPtr blobData, int blobSize)
        {
            const ushort VT_BLOB = 65;

            int unionOffset = 8;
            int pointerOffset = unionOffset + IntPtr.Size;
            int totalSize = unionOffset + 2 * IntPtr.Size;

            IntPtr propVariant = Marshal.AllocHGlobal(totalSize);
            for (int i = 0; i < totalSize; i++)
                Marshal.WriteByte(propVariant, i, 0);

            Marshal.WriteInt16(propVariant, 0, unchecked((short)VT_BLOB));
            Marshal.WriteInt32(propVariant, unionOffset, blobSize);
            Marshal.WriteIntPtr(propVariant, pointerOffset, blobData);
            return propVariant;
        }

        private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            public readonly ManualResetEventSlim Completed = new(false);

            public IAudioClient? AudioClient;
            public int ActivateResult;
            public Exception? Error;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                try
                {
                    int hr = activateOperation.GetActivateResult(out ActivateResult, out object activated);
                    Marshal.ThrowExceptionForHR(hr);
                    if (ActivateResult >= 0)
                        AudioClient = (IAudioClient)activated;
                }
                catch (Exception ex)
                {
                    Error = ex;
                }
                finally
                {
                    Completed.Set();
                }
            }
        }
    }
}
