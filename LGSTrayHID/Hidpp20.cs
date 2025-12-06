namespace LGSTrayHID
{
    public readonly struct Hidpp20
    {
        private readonly byte[] _data;

        public Hidpp20(byte[] data)
        {
            this._data = data;
        }

        public static explicit operator byte[](Hidpp20 msg) => msg._data;

        public static implicit operator Hidpp20(byte[] data) => new(data);

        public byte this[int index] => _data[index];

        public int Length => _data.Length;

        public byte GetDeviceIdx()
        {
            return _data[1];
        }

        public byte GetFeatureIndex()
        {
            return _data[2];
        }

        public byte GetFunctionId()
        {
            return (byte)((_data[3] & 0xF0) >> 4);
        }

        public byte GetSoftwareId()
        {
            return (byte)(_data[3] & 0x0F);
        }

        public Span<byte> GetParams()
        {
            return _data.AsSpan(4);
        }

        public byte GetParam(int paramIdx)
        {
            return _data[4 + paramIdx];
        }

        // ========== Phase 2: Response Parsing Helpers ==========

        /// <summary>
        /// Check if this response is a HID++ error response.
        /// Error responses have feature index 0x8F.
        /// </summary>
        /// <returns>True if this is an error response</returns>
        public bool IsError()
        {
            return GetFeatureIndex() == Protocol.HidppResponse.ERROR;
        }

        /// <summary>
        /// Check if this is a device announcement message (hotplug arrival).
        /// Device announcements have feature index 0x41.
        /// </summary>
        /// <returns>True if this is a device announcement</returns>
        public bool IsDeviceAnnouncement()
        {
            return GetFeatureIndex() == Protocol.HidppResponse.DEVICE_ANNOUNCEMENT;
        }

        /// <summary>
        /// Check if this message is a battery event broadcast.
        /// Battery events have function index 0x00 and are NOT matched to a pending request.
        /// </summary>
        /// <param name="featureIndex">The feature index to check against (from device's feature map)</param>
        /// <returns>True if this is a battery event for the given feature</returns>
        /// <remarks>
        /// Battery events are distinguished from query responses by:
        /// 1. Function ID is 0x00 (BATTERY_STATUS_BROADCAST)
        /// 2. Not matched to a pending WriteRead20 request (caller's responsibility)
        /// 3. Feature index matches a known battery feature (0x1000, 0x1001, 0x1004)
        /// </remarks>
        public bool IsBatteryEvent(byte featureIndex)
        {
            return GetFeatureIndex() == featureIndex
                && GetFunctionId() == Protocol.BatteryEventFunction.BATTERY_STATUS_BROADCAST;
        }

        /// <summary>
        /// Get the error code from an error response.
        /// Only valid if IsError() returns true.
        /// </summary>
        /// <returns>Error code byte</returns>
        /// <exception cref="InvalidOperationException">Thrown if this is not an error response</exception>
        public byte GetErrorCode()
        {
            if (!IsError())
            {
                throw new InvalidOperationException("Cannot get error code from non-error response");
            }
            return GetParam(0);
        }

        /// <summary>
        /// Parse a 16-bit parameter from two consecutive bytes (big-endian).
        /// Commonly used for feature IDs and other multi-byte values.
        /// </summary>
        /// <param name="offset">Parameter offset (0-2)</param>
        /// <returns>16-bit unsigned value</returns>
        /// <example>
        /// // Instead of: ushort featureId = (ushort)((ret.GetParam(0) << 8) + ret.GetParam(1));
        /// // Use: ushort featureId = ret.GetParam16(0);
        /// </example>
        public ushort GetParam16(int offset)
        {
            return (ushort)((GetParam(offset) << 8) | GetParam(offset + 1));
        }

        /// <summary>
        /// Get the feature ID from a feature enumeration response.
        /// This is a convenience method that calls GetParam16(0).
        /// </summary>
        /// <returns>Feature ID as 16-bit unsigned value</returns>
        public ushort GetFeatureId()
        {
            return GetParam16(0);
        }

        /// <summary>
        /// Check if this response matches the given request.
        /// Matching is based on feature index and software ID.
        /// </summary>
        /// <param name="request">The original request command</param>
        /// <returns>True if the response matches the request</returns>
        public bool MatchesRequest(Hidpp20 request)
        {
            return GetFeatureIndex() == request.GetFeatureIndex()
                && GetSoftwareId() == request.GetSoftwareId();
        }
    }
}
