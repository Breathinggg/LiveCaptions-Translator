using System.Runtime.InteropServices;

namespace LiveCaptionsTranslator.audio
{
    /// <summary>
    /// The slice of WASAPI needed for process loopback capture.
    ///
    /// NAudio cannot be used here: its IAudioClient interop is internal, and process loopback
    /// needs the stream to be activated through ActivateAudioInterfaceAsync rather than through
    /// an IMMDevice, which is the only path NAudio exposes.
    /// </summary>
    internal static class WasapiInterop
    {
        /// <summary>Pseudo device path that activates a process-scoped loopback stream.</summary>
        public const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VAD\\Process_Loopback";

        public const int AUDCLNT_SHAREMODE_SHARED = 0;

        public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

        public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

        public const ushort WAVE_FORMAT_PCM = 1;

        public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
        public static extern void ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);
    }

    public enum ProcessLoopbackMode
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1
    }

    internal enum AudioClientActivationType
    {
        Default = 0,
        ProcessLoopback = 1
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public ProcessLoopbackMode ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioClientActivationParams
    {
        public AudioClientActivationType ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig]
        int GetActivateResult(
            out int activateResult,
            [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    /// <summary>
    /// Marker interface. ActivateAudioInterfaceAsync requires the completion handler to be
    /// agile, otherwise the callback never arrives on a non-MTA caller.
    /// </summary>
    [ComImport]
    [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAgileObject
    {
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity,
            ref WaveFormatEx format, IntPtr audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint numBufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out uint numPaddingFrames);

        [PreserveSig]
        int IsFormatSupported(int shareMode, ref WaveFormatEx format, IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr deviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr eventHandle);

        [PreserveSig]
        int GetService([MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint numFramesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint numFramesInNextPacket);
    }
}
