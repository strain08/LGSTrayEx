using LGSTrayHID.Centurion.Transport;

namespace LGSTrayHID.Tests;

/// <summary>
/// Converts dash-separated hex strings from the diagnostic log into 64-byte frame buffers.
///
/// Paste frames directly from log lines, e.g.:
///   RX (64 bytes): 50-23-06-00-00-0F-03-00-01-00-00-...
///   TX [0x50]:     50-05-00-00-0F-00-01...
///
/// Trailing "..." is stripped. Frames shorter than 64 bytes are zero-padded.
/// </summary>
internal static class LogFrame
{
    /// <summary>
    /// Parse a dash-separated hex string from the diagnostic log into a 64-byte frame buffer.
    /// </summary>
    public static byte[] Parse(string hexDash)
    {
        // Strip trailing "..." (TX frames in the log are truncated)
        var trimmed = hexDash.AsSpan().TrimEnd();
        if (trimmed.EndsWith("..."))
            trimmed = trimmed[..^3].TrimEnd('-');

        var buf = new byte[FrameLayout.FRAME_SIZE];
        int i = 0;
        foreach (var part in trimmed.ToString().Split('-'))
        {
            if (i >= buf.Length) break;
            buf[i++] = Convert.ToByte(part.Trim(), 16);
        }
        return buf;
    }
}
