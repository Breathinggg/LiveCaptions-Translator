using System.Runtime.InteropServices;

namespace AudioRouteProbe
{
    public enum EDataFlow
    {
        eRender = 0,
        eCapture = 1,
        eAll = 2
    }

    public enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2
    }

    /// <summary>
    /// Per-process default audio endpoint override, i.e. the thing behind
    /// Settings > System > Sound > Volume mixer > "Output device" of a single app.
    ///
    /// The only way to reach it is the undocumented `Windows.Media.Internal.AudioPolicyConfig`
    /// WinRT activation factory. .NET 5 dropped the built-in WinRT interop layer, so
    /// `UnmanagedType.HString` and `ComInterfaceType.InterfaceIsIInspectable` are unavailable
    /// here: the factory is called through its raw vtable instead, and HSTRINGs are created
    /// and released by hand.
    /// </summary>
    public sealed unsafe class AudioPolicyConfig : IDisposable
    {
        private const string RUNTIME_CLASS = "Windows.Media.Internal.AudioPolicyConfig";

        // Windows 11 21H2 and later. Older builds expose {2a59116d-...} with a shorter vtable;
        // Live Captions only exists on Windows 11, so the newer variant is the only one needed.
        private static Guid IID_AudioPolicyConfigFactory = new("ab3d4648-e242-459f-b02f-541c70306324");

        // IUnknown (3) + IInspectable (3) + 19 unrelated methods precede the three we want.
        private const int VTBL_SET_PERSISTED_DEFAULT_ENDPOINT = 25;
        private const int VTBL_GET_PERSISTED_DEFAULT_ENDPOINT = 26;
        private const int VTBL_CLEAR_ALL_PERSISTED_ENDPOINTS = 27;

        private const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";
        private const string DEVINTERFACE_AUDIO_RENDER = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        private const string DEVINTERFACE_AUDIO_CAPTURE = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

        private const int RO_INIT_MULTITHREADED = 1;

        private IntPtr factory;

        public AudioPolicyConfig()
        {
            // Ignore the result: S_FALSE means already initialised, RPC_E_CHANGED_MODE means
            // the thread is STA, and both are fine for activating a factory.
            RoInitialize(RO_INIT_MULTITHREADED);

            IntPtr classId = CreateHString(RUNTIME_CLASS);
            try
            {
                int hr = RoGetActivationFactory(classId, ref IID_AudioPolicyConfigFactory, out factory);
                if (hr < 0)
                    throw new COMException($"RoGetActivationFactory(\"{RUNTIME_CLASS}\") failed.", hr);
            }
            finally
            {
                WindowsDeleteString(classId);
            }
        }

        private IntPtr* Vtbl => *(IntPtr**)factory;

        /// <summary>
        /// Routes <paramref name="processId"/> to <paramref name="mmDeviceId"/>.
        /// Passing null or an empty id restores the process to the system default endpoint.
        /// </summary>
        public void SetDefaultEndpoint(int processId, string? mmDeviceId, EDataFlow flow = EDataFlow.eRender)
        {
            IntPtr deviceId = IntPtr.Zero;
            if (!string.IsNullOrWhiteSpace(mmDeviceId))
                deviceId = CreateHString(PackDeviceId(mmDeviceId, flow));

            try
            {
                var set = (delegate* unmanaged[Stdcall]<IntPtr, uint, EDataFlow, ERole, IntPtr, int>)
                    Vtbl[VTBL_SET_PERSISTED_DEFAULT_ENDPOINT];

                // Both roles have to be written; Windows picks one depending on how the
                // consuming app asks for its default endpoint.
                foreach (ERole role in new[] { ERole.eMultimedia, ERole.eConsole })
                {
                    int hr = set(factory, (uint)processId, flow, role, deviceId);
                    if (hr < 0)
                        throw new COMException(
                            $"SetPersistedDefaultAudioEndpoint(pid={processId}, role={role}) failed.", hr);
                }
            }
            finally
            {
                if (deviceId != IntPtr.Zero)
                    WindowsDeleteString(deviceId);
            }
        }

        /// <summary>
        /// Returns the endpoint id currently pinned to <paramref name="processId"/>,
        /// or null when the process follows the system default.
        /// </summary>
        public string? GetDefaultEndpoint(int processId, EDataFlow flow = EDataFlow.eRender)
        {
            var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, EDataFlow, ERole, IntPtr*, int>)
                Vtbl[VTBL_GET_PERSISTED_DEFAULT_ENDPOINT];

            IntPtr deviceId;
            int hr = get(factory, (uint)processId, flow, ERole.eMultimedia, &deviceId);
            if (hr < 0)
                throw new COMException($"GetPersistedDefaultAudioEndpoint(pid={processId}) failed.", hr);

            try
            {
                string raw = HStringToString(deviceId);
                return string.IsNullOrEmpty(raw) ? null : UnpackDeviceId(raw);
            }
            finally
            {
                WindowsDeleteString(deviceId);
            }
        }

        /// <summary>Drops every per-app routing override on the machine, not just ours.</summary>
        public void ClearAll()
        {
            var clear = (delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[VTBL_CLEAR_ALL_PERSISTED_ENDPOINTS];
            int hr = clear(factory);
            if (hr < 0)
                throw new COMException("ClearAllPersistedApplicationDefaultEndpoints failed.", hr);
        }

        private static string PackDeviceId(string mmDeviceId, EDataFlow flow)
        {
            string devInterface = flow == EDataFlow.eRender
                ? DEVINTERFACE_AUDIO_RENDER
                : DEVINTERFACE_AUDIO_CAPTURE;
            return $"{MMDEVAPI_TOKEN}{mmDeviceId}{devInterface}";
        }

        private static string UnpackDeviceId(string deviceId)
        {
            if (deviceId.StartsWith(MMDEVAPI_TOKEN))
                deviceId = deviceId.Substring(MMDEVAPI_TOKEN.Length);
            if (deviceId.EndsWith(DEVINTERFACE_AUDIO_RENDER))
                deviceId = deviceId.Substring(0, deviceId.Length - DEVINTERFACE_AUDIO_RENDER.Length);
            if (deviceId.EndsWith(DEVINTERFACE_AUDIO_CAPTURE))
                deviceId = deviceId.Substring(0, deviceId.Length - DEVINTERFACE_AUDIO_CAPTURE.Length);
            return deviceId;
        }

        private static IntPtr CreateHString(string value)
        {
            int hr = WindowsCreateString(value, (uint)value.Length, out IntPtr hstring);
            if (hr < 0)
                throw new COMException($"WindowsCreateString(\"{value}\") failed.", hr);
            return hstring;
        }

        private static string HStringToString(IntPtr hstring)
        {
            if (hstring == IntPtr.Zero)
                return string.Empty;
            IntPtr buffer = WindowsGetStringRawBuffer(hstring, out uint length);
            return buffer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(buffer, (int)length) ?? string.Empty;
        }

        public void Dispose()
        {
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
                factory = IntPtr.Zero;
            }
        }

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoInitialize(int initType);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString, uint length, out IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll", ExactSpelling = true)]
        private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    }
}
