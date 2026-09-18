using System.Diagnostics;
using System.IO;

namespace OniTool.Core;

// Wraps vgmstream-cli (RE Engine / Wwise audio -> wav).
public static class AudioConverter
{
    public static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wem", ".bnk", ".pck", ".wav", ".ogg", ".opus", ".fsb", ".xwb",
        ".awb", ".acb", ".hca", ".at9", ".wsp", ".mus", ".sound", ".stream"
    };

    static string BinDir => Path.Combine(AppContext.BaseDirectory, "bin");
    static string Vgmstream => Path.Combine(BinDir, "vgmstream", "vgmstream-cli.exe");

    public static bool HasVgmstream => File.Exists(Vgmstream);

    public static bool IsAudio(string path)
    {
        var ext = Path.GetExtension(path);
        if (AudioExts.Contains(ext)) return true;
        // RE Engine versioned names e.g. foo.wem.something / foo.stream.xxx
        foreach (var e in AudioExts)
            if (Path.GetFileName(path).Contains(e + ".", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static int Run(string exe, string args, out string stdout, out string stderr, int timeoutMs = 120000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        stdout = p.StandardOutput.ReadToEnd();
        stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return -1; }
        return p.ExitCode;
    }

    static string Q(string s) => "\"" + s + "\"";

    // Convert to wav. Returns wav path, or throws.
    public static string ToWav(string path, string outWav)
    {
        if (!HasVgmstream) throw new InvalidOperationException("vgmstream-cli not found");
        Directory.CreateDirectory(Path.GetDirectoryName(outWav)!);
        int rc = Run(Vgmstream, $"-o {Q(outWav)} {Q(path)}", out _, out var err);
        if (rc != 0 || !File.Exists(outWav))
            throw new Exception($"vgmstream failed ({rc}): {err}");
        return outWav;
    }

    public static string? Probe(string path)
    {
        if (!HasVgmstream) return null;
        int rc = Run(Vgmstream, $"-m {Q(path)}", out var so, out _, 30000);
        return rc == 0 ? so : null;
    }
}
