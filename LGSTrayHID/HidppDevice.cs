using LGSTrayPrimitives;
using LGSTrayPrimitives.MessageStructs;
using LGSTrayHID.Features;
using LGSTrayHID.Protocol;
using System.Text;

using static LGSTrayHID.HidppDevices;

#if DEBUG
using Log = System.Console;
#else
using Log = System.Diagnostics.Debug;
#endif

namespace LGSTrayHID
{
    public class HidppDevice
    {
        private const int INIT_PING_TIMEOUT = 5000;

        private readonly SemaphoreSlim _initSemaphore = new(1, 1);
        private IBatteryFeature? _batteryFeature;

        public string DeviceName { get; private set; } = string.Empty;
        public int DeviceType { get; private set; } = 3;
        public string Identifier { get; private set; } = string.Empty;

        private BatteryUpdateReturn lastBatteryReturn;
        private DateTimeOffset lastUpdate = DateTimeOffset.MinValue;

        private readonly HidppDevices _parent;
        public HidppDevices Parent => _parent;

        private readonly byte _deviceIdx;
        public byte DeviceIdx => _deviceIdx;

        private readonly Dictionary<ushort, byte> _featureMap = [];
        public Dictionary<ushort, byte> FeatureMap => _featureMap;

        public HidppDevice(HidppDevices parent, byte deviceIdx)
        {
            _parent = parent;
            _deviceIdx = deviceIdx;
        }

        public async Task InitAsync()
        {
            await _initSemaphore.WaitAsync();
            try
            {
                Hidpp20 ret;

                // Sync Ping with retry logic for sleeping devices
                const int maxRetries = 10;
                const int initialDelay = 2000; // 2 seconds
                bool pingSuccess = false;

                for (int retry = 0; retry < maxRetries && !pingSuccess; retry++)
                {
                    // Add delay before retry attempts (not on first attempt)
                    if (retry > 0)
                    {
                        int delay = initialDelay * (int)Math.Pow(2, retry - 1);
                        DiagnosticLogger.Log($"Retrying HID device index {_deviceIdx} after {delay}ms delay (attempt {retry + 1}/{maxRetries})");
                        await Task.Delay(delay);
                    }

                    // Ping test
                    int successCount = 0;
                    int successThresh = 3;
                    DiagnosticLogger.Log($"Starting ping test for HID device index {_deviceIdx}");
                    for (int i = 0; i < 10; i++)
                    {
                        var ping = await _parent.Ping20(_deviceIdx, INIT_PING_TIMEOUT);
                        if (ping)
                        {
                            successCount++;
                        }
                        else
                        {
                            successCount = 0;
                        }

                        if (successCount >= successThresh)
                        {
                            pingSuccess = true;
                            break;
                        }
                    }

                    // Log result if this is the last attempt and still failing
                    if (!pingSuccess && retry == maxRetries - 1)
                    {
                        DiagnosticLogger.LogWarning($"HID device index {_deviceIdx} failed ping test after {maxRetries} retries ({successCount}/{successThresh} successes)");
                        return;
                    }
                }

                DiagnosticLogger.Log($"HID device index {_deviceIdx} passed ping test");

                // Find IFeatureSet (0x0001) - get its feature index
                ret = await _parent.WriteRead20(_parent.DevShort,
                    Hidpp20Commands.GetFeatureIndex(_deviceIdx, HidppFeature.FEATURE_SET));
                _featureMap[HidppFeature.FEATURE_SET] = ret.GetParam(0);

                // Get Feature Count
                ret = await _parent.WriteRead20(_parent.DevShort,
                    Hidpp20Commands.GetFeatureCount(_deviceIdx, _featureMap[HidppFeature.FEATURE_SET]));
                int featureCount = ret.GetParam(0);

                // Enumerate Features
                for (byte i = 0; i <= featureCount; i++)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort,
                        Hidpp20Commands.EnumerateFeature(_deviceIdx, _featureMap[HidppFeature.FEATURE_SET], i));
                    ushort featureId = ret.GetFeatureId();

                    _featureMap[featureId] = i;
                }

                await InitPopulateAsync();
            }
            finally
            {
                _initSemaphore.Release();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0018:Inline variable declaration")]
        private async Task InitPopulateAsync()
        {
            Hidpp20 ret;
            byte featureId;

            DiagnosticLogger.Log($"Enumerating features for HID device index {_deviceIdx}");

            // Device name
            if (_featureMap.TryGetValue(HidppFeature.DEVICE_NAME, out featureId))
            {
                // Get device name length
                ret = await _parent.WriteRead20(_parent.DevShort,
                    Hidpp20Commands.GetDeviceNameLength(_deviceIdx, featureId));
                int nameLength = ret.GetParam(0);

                string name = "";

                // Read name in chunks (3 bytes at a time)
                while (name.Length < nameLength)
                {
                    ret = await _parent.WriteRead20(_parent.DevShort,
                        Hidpp20Commands.GetDeviceNameChunk(_deviceIdx, featureId, (byte)name.Length));
                    name += Encoding.UTF8.GetString(ret.GetParams());
                }

                DeviceName = name.TrimEnd('\0');

                // Check if device is filtered in settings
                foreach (var tag in GlobalSettings.settings.DisabledDevices)
                {
                    if (DeviceName.Contains(tag))
                    {
                        DiagnosticLogger.LogWarning($"HID device '{DeviceName}' filtered by disabledDevices config (matched: '{tag}')");
                        return;
                    }
                };

                // Get device type
                ret = await _parent.WriteRead20(_parent.DevShort,
                    Hidpp20Commands.GetDeviceType(_deviceIdx, featureId));
                DeviceType = ret.GetParam(0);
            }
            else
            {
                // Device does not have a name/Hidpp error ignore it
                DiagnosticLogger.LogWarning($"HID device index {_deviceIdx} missing feature 0x0005 (device name), ignoring");
                return;
            }

            if (_featureMap.TryGetValue(HidppFeature.DEVICE_FW_INFO, out featureId))
            {
                // Get device firmware info (unit ID, model ID, serial support flag)
                ret = await _parent.WriteRead20(_parent.DevShort,
                    Hidpp20Commands.GetDeviceFwInfo(_deviceIdx, featureId));

                string unitId = BitConverter.ToString(ret.GetParams().ToArray(), 1, 4).Replace("-", string.Empty);
                string modelId = BitConverter.ToString(ret.GetParams().ToArray(), 7, 5).Replace("-", string.Empty);

                bool serialNumberSupported = (ret.GetParam(14) & 0x1) == 0x1;
                string? serialNumber = null;
                if (serialNumberSupported)
                {
                    // Get device serial number
                    ret = await _parent.WriteRead20(_parent.DevShort,
                        Hidpp20Commands.GetSerialNumber(_deviceIdx, featureId));
                    serialNumber = BitConverter.ToString(ret.GetParams().ToArray(), 0, 11).Replace("-", string.Empty);
                }

                Identifier = serialNumber ?? $"{unitId}-{modelId}";
            }
            else
            {
                // Device does not have a serial identifier the device name as a hash identifier
                Identifier = $"{DeviceName.GetHashCode():X04}";
            }

            // Select battery feature using factory pattern
            _batteryFeature = BatteryFeatureFactory.GetBatteryFeature(FeatureMap);
            
            // Log battery feature presence
            if (_batteryFeature != null)
            {
                DiagnosticLogger.Log($"[{DeviceName}] Battery feature found: {_batteryFeature.FeatureName} (ID: {_batteryFeature.FeatureId:X})");
            }
            else
            {
                DiagnosticLogger.LogWarning($"[{DeviceName}] No battery feature found.");
            }            

            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.INIT,
                new InitMessage(Identifier, DeviceName, _batteryFeature != null, (DeviceType)DeviceType)
            );

            DiagnosticLogger.Log($"HID device registered - {Identifier} ({DeviceName})");

            await Task.Delay(1000);

            _ = Task.Run(async () =>
            {
                if (_batteryFeature == null) { return; }

                while (true)
                {
                    var now = DateTimeOffset.Now;
#if DEBUG
                    var expectedUpdateTime = lastUpdate.AddSeconds(1);
#else
                    var expectedUpdateTime = lastUpdate.AddSeconds(GlobalSettings.settings.PollPeriod);
#endif
                    if (now < expectedUpdateTime)
                    {
                        await Task.Delay((int)(expectedUpdateTime - now).TotalMilliseconds);
                    }

                    await UpdateBattery();
                    await Task.Delay(GlobalSettings.settings.RetryTime * 1000);
                    DiagnosticLogger.Log($"Polling battery for device {DeviceName}");
                }
            });
        }

        public async Task UpdateBattery(bool forceIpcUpdate = false)
        {
            if (Parent.Disposed) { 
                DiagnosticLogger.Log($"[{DeviceName}] Parent disposed, skipping battery update.");
                return; 
            }
            if (_batteryFeature == null) {
                DiagnosticLogger.Log($"[{DeviceName}] No battery feature available, skipping battery update.");
                return;
            }

            var ret = await _batteryFeature.GetBatteryAsync(this);

            if (ret == null) { 
                DiagnosticLogger.Log($"[{DeviceName}] Battery update returned null, skipping.");
                return; 
            }

            var batStatus = ret.Value;
            DiagnosticLogger.Log($"[{DeviceName}] Battery level {batStatus.batteryPercentage}.");

            lastUpdate = DateTimeOffset.Now;

            if (forceIpcUpdate || (batStatus == lastBatteryReturn))
            {                
                // Don't report if no change
                return;
            }            
            lastBatteryReturn = batStatus;
            HidppManagerContext.Instance.SignalDeviceEvent(
                IPCMessageType.UPDATE,
                new UpdateMessage(Identifier, batStatus.batteryPercentage, batStatus.status, batStatus.batteryMVolt, lastUpdate)
            );
        }
    }
}
