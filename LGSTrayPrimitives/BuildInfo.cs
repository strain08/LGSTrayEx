using System.Reflection;

namespace LGSTrayPrimitives;

public static class BuildInfo
{
    /// <summary>
    /// Returns a human-readable build provenance string from the assembly's
    /// <see cref="AssemblyInformationalVersionAttribute"/>, e.g. "abc1234 @ 20260320T103000Z".
    /// Returns an empty string when no build metadata is present.
    /// </summary>
    public static string FromAssembly(Assembly asm)
    {
        var infoVer = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                        .InformationalVersion;
        if (infoVer is null) return string.Empty;

        var plusIdx = infoVer.IndexOf('+');
        if (plusIdx < 0) return string.Empty;

        // SourceRevisionId format: "{sha}.{yyyyMMddTHHmmssZ}"
        var meta = infoVer[(plusIdx + 1)..];
        var dotIdx = meta.IndexOf('.');
        if (dotIdx < 0) return meta;

        var sha = meta[..dotIdx];
        var ts  = meta[(dotIdx + 1)..];
        return $"{sha} @ {ts}";
    }
}
