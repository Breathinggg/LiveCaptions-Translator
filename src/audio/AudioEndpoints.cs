using NAudio.CoreAudioApi;

namespace LiveCaptionsTranslator.audio
{
    /// <summary>
    /// A render endpoint offered as the isolation channel: the device Live Captions will be
    /// pinned to and that nothing else is expected to play into.
    /// </summary>
    public class AudioEndpointInfo
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsSystemDefault { get; init; }
        public bool IsVirtual { get; init; }

        /// <summary>Channel count of the device's mix format, 0 when it could not be read.</summary>
        public int Channels { get; init; }

        /// <summary>
        /// Speech is mono or stereo. A multi-channel endpoint still works, but the relay has to
        /// upmix into it, so a plain stereo device is the safer default to preselect.
        /// </summary>
        public bool IsStereo => Channels is > 0 and <= 2;

        /// <summary>
        /// The system default is never usable: everything else plays into it, which is exactly
        /// the mixing this feature exists to avoid.
        /// </summary>
        public bool IsUsable => !IsSystemDefault;

        public string Hint => IsSystemDefault
            ? "Cannot be used - this is the system default, everything plays into it"
            : IsVirtual
                ? IsStereo
                    ? "Recommended - virtual device, silent and unused by other apps"
                    : $"Virtual device, but {Channels} channels; a stereo one is a safer choice"
                : "Usable - make sure nothing else plays into this device";

        public override string ToString() => Name;
    }

    public static class AudioEndpoints
    {
        // Virtual cables are the ideal isolation channel: audio pushed into them is inaudible
        // and no other application routes to them by accident.
        private static readonly string[] VIRTUAL_DEVICE_HINTS =
            { "CABLE", "Voicemeeter", "VoiceMeeter", "Virtual", "VB-Audio", "Synchronous Audio Router" };

        public static List<AudioEndpointInfo> List()
        {
            using var enumerator = new MMDeviceEnumerator();

            string defaultId = string.Empty;
            try
            {
                defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch (Exception)
            {
                // No default endpoint at all; every device stays selectable.
            }

            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Select(device => new AudioEndpointInfo
                {
                    Id = device.ID,
                    Name = device.FriendlyName,
                    IsSystemDefault = device.ID == defaultId,
                    IsVirtual = VIRTUAL_DEVICE_HINTS.Any(hint =>
                        device.FriendlyName.Contains(hint, StringComparison.OrdinalIgnoreCase)),
                    Channels = GetChannelCount(device)
                })
                .OrderByDescending(e => e.IsUsable && e.IsVirtual && e.IsStereo)
                .ThenByDescending(e => e.IsUsable && e.IsVirtual)
                .ThenByDescending(e => e.IsUsable && e.IsStereo)
                .ThenByDescending(e => e.IsUsable)
                .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static int GetChannelCount(MMDevice device)
        {
            try
            {
                return device.AudioClient.MixFormat.Channels;
            }
            catch (Exception)
            {
                // Some endpoints refuse activation until something plays into them.
                return 0;
            }
        }

        /// <summary>Best default choice, or null when the machine has no usable second endpoint.</summary>
        public static AudioEndpointInfo? Recommended() => List().FirstOrDefault(e => e.IsUsable);

        public static MMDevice? GetDevice(string endpointId)
        {
            if (string.IsNullOrEmpty(endpointId))
                return null;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return enumerator.GetDevice(endpointId);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
