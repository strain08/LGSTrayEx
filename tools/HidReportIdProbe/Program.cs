using System.Runtime.InteropServices;

namespace HidReportIdProbe;

internal static class Program
{
    // Report IDs we care about cross-referencing:
    //   0x10/0x11 — HID++ 2.0 short/long (Bolt, Unifying, direct HID++)
    //   0x50/0x51 — Centurion SHORT/LONG (G522, PRO X 2)
    private static readonly byte[] InterestingReportIds = [0x10, 0x11, 0x50, 0x51];

    private static StreamWriter? _logFile;

    private static int Main(string[] args)
    {
        bool runBlind = args.Contains("--blind", StringComparer.OrdinalIgnoreCase);
        bool noPause = args.Contains("--no-pause", StringComparer.OrdinalIgnoreCase);
        ushort vid = 0x046D; // Logitech
        if (args.FirstOrDefault(a => a.StartsWith("--vid=")) is { } vidArg)
            vid = Convert.ToUInt16(vidArg["--vid=".Length..], 16);

        string exeDir = AppContext.BaseDirectory;
        string logPath = Path.Combine(exeDir, $"HidReportIdProbe-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        try { _logFile = new StreamWriter(logPath) { AutoFlush = true }; }
        catch (Exception ex) { Console.Error.WriteLine($"(could not open log file '{logPath}': {ex.Message})"); }

        Log($"HidReportIdProbe v1  —  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        Log($"OS: {Environment.OSVersion}  .NET: {Environment.Version}  CWD: {exeDir}");
        Log($"Enumerating VID 0x{vid:X4}{(runBlind ? "  [BLIND PROBE ENABLED]" : "")}");
        Log("");

        int exitCode = 0;
        try
        {
            if (HidApi.HidInit() != 0)
            {
                Log("ERROR: hid_init failed (hidapi.dll missing or wrong arch?)");
                exitCode = 1;
            }
            else
            {
                unsafe
                {
                    HidDeviceInfo* head = HidApi.HidEnumerate(vid, 0);
                    if (head == null)
                    {
                        Log("No matching HID devices found.");
                    }
                    else
                    {
                        int idx = 0;
                        for (HidDeviceInfo* it = head; it != null; it = it->Next, idx++)
                            ReportOne(idx, ref *it, runBlind);
                        HidApi.HidFreeEnumeration(head);
                    }
                }
                HidApi.HidExit();
            }
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            exitCode = 2;
        }
        finally
        {
            Log("");
            Log($"Output saved to: {logPath}");
            _logFile?.Dispose();
        }

        if (!noPause)
        {
            Console.WriteLine();
            Console.WriteLine("Press Enter to close...");
            Console.ReadLine();
        }
        return exitCode;
    }

    private static void Log(string line)
    {
        Console.WriteLine(line);
        _logFile?.WriteLine(line);
    }

    private static unsafe void ReportOne(int index, ref HidDeviceInfo info, bool runBlind)
    {
        string path = HidApi.PathAsString(info.Path);
        string manuf = HidApi.WidePathOrEmpty(info.ManufacturerString);
        string prod  = HidApi.WidePathOrEmpty(info.ProductString);

        Log($"#{index,3}  PID 0x{info.ProductId:X4}  intf={info.InterfaceNumber}  page=0x{info.UsagePage:X4} usage=0x{info.Usage:X4}");
        Log($"      product: {prod}   manuf: {manuf}");
        Log($"      path: {path}");

        var descriptor = ReadDescriptor(path);
        if (descriptor.Error != null)
        {
            Log($"      HidP: ERROR — {descriptor.Error}");
        }
        else
        {
            Log($"      HidP caps: page=0x{descriptor.UsagePage:X4} usage=0x{descriptor.Usage:X4}  inLen={descriptor.InputReportByteLength}  outLen={descriptor.OutputReportByteLength}  featLen={descriptor.FeatureReportByteLength}");
            Log($"      HidP input  IDs: {FormatIds(descriptor.InputReportIds)}");
            Log($"      HidP output IDs: {FormatIds(descriptor.OutputReportIds)}");
            Log($"      HidP feat   IDs: {FormatIds(descriptor.FeatureReportIds)}");
            if (descriptor.InterestingReportIds().Length > 0)
                Log($"      *** HID++/Centurion IDs declared: {FormatIds(descriptor.InterestingReportIds())}");
        }

        if (runBlind)
        {
            var blind = BlindProbe(ref info);
            Log($"      blind probe accepted: {FormatIds(blind)}");
            if (descriptor.Error == null)
                CompareAndFlag(descriptor, blind);
        }
        Log("");
    }

    private static unsafe byte[] BlindProbe(ref HidDeviceInfo info)
    {
        nint handle = HidApi.HidOpenPath(info.Path);
        if (handle == 0) return [];

        var accepted = new List<byte>();
        try
        {
            foreach (byte id in InterestingReportIds)
            {
                // Send a well-formed-ish 64-byte frame: report ID + minimal padding.
                // Match the production probe shape so the test is representative.
                byte[] frame = new byte[64];
                frame[0] = id;
                int w = HidApi.HidWrite(handle, frame, (nuint)frame.Length);
                if (w > 0) accepted.Add(id);
            }
        }
        finally
        {
            HidApi.HidClose(handle);
        }
        return [.. accepted];
    }

    private static DescriptorInfo ReadDescriptor(string path)
    {
        using var handle = HidP.CreateFile(
            path,
            0, // no read/write access needed for descriptor query
            HidP.FILE_SHARE_READ | HidP.FILE_SHARE_WRITE,
            0,
            HidP.OPEN_EXISTING,
            0,
            0);

        if (handle.IsInvalid)
            return new DescriptorInfo { Error = $"CreateFile failed (Win32 {Marshal.GetLastWin32Error()})" };

        if (!HidP.HidD_GetPreparsedData(handle, out nint preparsed) || preparsed == 0)
            return new DescriptorInfo { Error = "HidD_GetPreparsedData returned null" };

        try
        {
            if (HidP.HidP_GetCaps(preparsed, out HIDP_CAPS caps) != HidP.HIDP_STATUS_SUCCESS)
                return new DescriptorInfo { Error = "HidP_GetCaps failed" };

            var info = new DescriptorInfo
            {
                Usage = caps.Usage,
                UsagePage = caps.UsagePage,
                InputReportByteLength = caps.InputReportByteLength,
                OutputReportByteLength = caps.OutputReportByteLength,
                FeatureReportByteLength = caps.FeatureReportByteLength,
            };

            CollectIds(preparsed, HIDP_REPORT_TYPE.HidP_Input, caps.NumberInputValueCaps, caps.NumberInputButtonCaps, info.InputReportIds);
            CollectIds(preparsed, HIDP_REPORT_TYPE.HidP_Output, caps.NumberOutputValueCaps, caps.NumberOutputButtonCaps, info.OutputReportIds);
            CollectIds(preparsed, HIDP_REPORT_TYPE.HidP_Feature, caps.NumberFeatureValueCaps, caps.NumberFeatureButtonCaps, info.FeatureReportIds);

            return info;
        }
        finally
        {
            HidP.HidD_FreePreparsedData(preparsed);
        }
    }

    private static void CollectIds(nint preparsed, HIDP_REPORT_TYPE type, ushort nValueCaps, ushort nButtonCaps, HashSet<byte> sink)
    {
        if (nValueCaps > 0)
        {
            var arr = new HIDP_VALUE_CAPS[nValueCaps];
            ushort len = nValueCaps;
            if (HidP.HidP_GetValueCaps(type, arr, ref len, preparsed) == HidP.HIDP_STATUS_SUCCESS)
                for (int i = 0; i < len; i++) sink.Add(arr[i].ReportID);
        }
        if (nButtonCaps > 0)
        {
            var arr = new HIDP_BUTTON_CAPS[nButtonCaps];
            ushort len = nButtonCaps;
            if (HidP.HidP_GetButtonCaps(type, arr, ref len, preparsed) == HidP.HIDP_STATUS_SUCCESS)
                for (int i = 0; i < len; i++) sink.Add(arr[i].ReportID);
        }
    }

    private static void CompareAndFlag(DescriptorInfo descriptor, byte[] blindAccepted)
    {
        var declaredOutput = descriptor.OutputReportIds;
        var blindSet = blindAccepted.ToHashSet();
        var interesting = InterestingReportIds.ToHashSet();

        // Diff scoped to the IDs we actually probed.
        var declaredButNotAccepted = declaredOutput.Intersect(interesting).Except(blindSet).ToArray();
        var acceptedButNotDeclared = blindSet.Except(declaredOutput).ToArray();

        if (declaredButNotAccepted.Length == 0 && acceptedButNotDeclared.Length == 0)
        {
            Log($"      diff: blind probe and HidP descriptor agree");
            return;
        }

        if (declaredButNotAccepted.Length > 0)
            Log($"      diff: declared as output but blind write rejected: {FormatIds(declaredButNotAccepted)}");
        if (acceptedButNotDeclared.Length > 0)
            Log($"      diff: blind write accepted but not declared as output: {FormatIds(acceptedButNotDeclared)}");
    }

    private static string FormatIds(IEnumerable<byte> ids)
    {
        var arr = ids.OrderBy(b => b).ToArray();
        return arr.Length == 0 ? "(none)" : string.Join(", ", arr.Select(b => $"0x{b:X2}"));
    }

    private sealed class DescriptorInfo
    {
        public string? Error;
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public readonly HashSet<byte> InputReportIds = [];
        public readonly HashSet<byte> OutputReportIds = [];
        public readonly HashSet<byte> FeatureReportIds = [];

        public byte[] InterestingReportIds()
        {
            var all = new HashSet<byte>(InputReportIds);
            all.UnionWith(OutputReportIds);
            all.UnionWith(FeatureReportIds);
            return [.. all.Intersect(Program.InterestingReportIds).OrderBy(b => b)];
        }
    }
}
