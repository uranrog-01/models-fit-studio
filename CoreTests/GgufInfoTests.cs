using System.Text;
using ModelFitStudio.Core;
using Xunit;

namespace CoreTests;

/// <summary>Reading a GGUF header: the architecture, the trained length, and whether the model drafts for itself (MTP).</summary>
public class GgufInfoTests
{
    // a GGUF v3 header with the given key-values (values: string, uint, or string[] for a token list)
    private static MemoryStream Header(params (string key, object value)[] kv)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); w.Write((ulong)b.Length); w.Write(b); }
        w.Write(0x46554747u);
        w.Write(3u);
        w.Write(0UL);
        w.Write((ulong)kv.Length);
        foreach (var (key, value) in kv)
        {
            Str(key);
            switch (value)
            {
                case string s: w.Write(8u); Str(s); break;
                case uint u: w.Write(4u); w.Write(u); break;
                case float f: w.Write(6u); w.Write(f); break;
                case string[] list: w.Write(9u); w.Write(8u); w.Write((ulong)list.Length); foreach (var t in list) Str(t); break;
                case int[] ints: w.Write(9u); w.Write(5u); w.Write((ulong)ints.Length); foreach (var i in ints) w.Write(i); break;
            }
        }
        w.Flush();
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void A_model_with_draft_layers_is_seen()
    {
        var info = GgufInfo.Read(Header(
            ("general.architecture", "qwen35"),
            ("general.name", "Qwen3.5 2B MTP"),
            ("tokenizer.ggml.tokens", new[] { "a", "b", "<|im_end|>" }),
            ("tokenizer.ggml.token_type", new[] { 1, 1, 3 }),
            ("qwen35.rope.freq_base", 1e6f),
            ("qwen35.context_length", 262144u),
            ("qwen35.nextn_predict_layers", 1u)));
        Assert.NotNull(info);
        Assert.Equal("qwen35", info!.Arch);
        Assert.Equal(262144, info.TrainedContext);
        Assert.True(info.HasMtp);
    }

    [Fact]
    public void A_plain_model_does_not_draft()
    {
        var info = GgufInfo.Read(Header(("general.architecture", "qwen3"), ("qwen3.context_length", 40960u)));
        Assert.False(info!.HasMtp);
        Assert.Equal(40960, info.TrainedContext);
    }

    [Fact]
    public void Not_a_gguf_file_is_null()
    {
        Assert.Null(GgufInfo.Read(new MemoryStream(Encoding.ASCII.GetBytes("PK\u0003\u0004 not a model at all"))));
    }
}
