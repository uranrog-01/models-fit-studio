using System.IO;
using System.Text;

namespace ModelFitStudio.Core;

/// <summary>
/// What a GGUF file's header says about the model: its architecture, trained length, and
/// whether it carries its own draft layers (MTP), which let llama.cpp guess several tokens
/// and check them in one pass. Reads the header only, never the weights.
/// </summary>
public sealed record GgufInfo(string Arch, long TrainedContext, int NextnLayers)
{
    /// <summary>The model drafts for itself: llama.cpp's "--spec-type draft-mtp" works with it.</summary>
    public bool HasMtp => NextnLayers > 0;

    public static GgufInfo? TryRead(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            return Read(f);
        }
        catch { return null; }
    }

    /// <summary>Reads the key-value part of a GGUF header; null when the stream is not GGUF.</summary>
    public static GgufInfo? Read(Stream s)
    {
        using var r = new BinaryReader(s, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != 0x46554747) return null;   // "GGUF"
        var version = r.ReadUInt32();
        if (version < 2) return null;
        r.ReadUInt64();                                    // tensor count
        var count = r.ReadUInt64();

        string arch = "";
        long trained = 0;
        int nextn = 0;
        var numbers = new Dictionary<string, long>(StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            var key = ReadString(r);
            var type = r.ReadUInt32();
            if (key == "general.architecture" && type == 8) { arch = ReadString(r); continue; }
            if (key.EndsWith(".context_length", StringComparison.Ordinal) || key.EndsWith(".nextn_predict_layers", StringComparison.Ordinal))
            {
                if (Integer(r, type) is { } v) numbers[key] = v;
                continue;
            }
            Skip(r, type);
        }
        if (arch.Length > 0)
        {
            numbers.TryGetValue(arch + ".context_length", out trained);
            if (numbers.TryGetValue(arch + ".nextn_predict_layers", out var n)) nextn = (int)n;
        }
        return new GgufInfo(arch, trained, nextn);
    }

    private static string ReadString(BinaryReader r)
    {
        var len = r.ReadUInt64();
        if (len > 1 << 20) throw new InvalidDataException("GGUF string too long");
        return Encoding.UTF8.GetString(r.ReadBytes((int)len));
    }

    private static long? Integer(BinaryReader r, uint type) => type switch
    {
        0 => r.ReadByte(), 1 => r.ReadSByte(), 2 => r.ReadUInt16(), 3 => r.ReadInt16(),
        4 => r.ReadUInt32(), 5 => r.ReadInt32(), 10 => (long)r.ReadUInt64(), 11 => r.ReadInt64(),
        _ => SkipNull(r, type),
    };

    private static long? SkipNull(BinaryReader r, uint type)
    {
        Skip(r, type);
        return null;
    }

    private static void Skip(BinaryReader r, uint type)
    {
        switch (type)
        {
            case 0: case 1: case 7: r.BaseStream.Seek(1, SeekOrigin.Current); break;
            case 2: case 3: r.BaseStream.Seek(2, SeekOrigin.Current); break;
            case 4: case 5: case 6: r.BaseStream.Seek(4, SeekOrigin.Current); break;
            case 10: case 11: case 12: r.BaseStream.Seek(8, SeekOrigin.Current); break;
            case 8: r.BaseStream.Seek((long)r.ReadUInt64(), SeekOrigin.Current); break;
            case 9:
                var inner = r.ReadUInt32();
                var n = r.ReadUInt64();
                if (inner == 8) { for (ulong i = 0; i < n; i++) r.BaseStream.Seek((long)r.ReadUInt64(), SeekOrigin.Current); }
                else if (inner == 9) { for (ulong i = 0; i < n; i++) Skip(r, 9); }
                else r.BaseStream.Seek((long)n * Width(inner), SeekOrigin.Current);
                break;
            default: throw new InvalidDataException("unknown GGUF value type " + type);
        }
    }

    private static int Width(uint type) => type switch
    {
        0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8,
        _ => throw new InvalidDataException("unknown GGUF array type " + type),
    };
}
