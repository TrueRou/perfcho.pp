using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Skinning;

namespace Perfcho.Performance.Infrastructure;

public sealed class ProcessorWorkingBeatmap : WorkingBeatmap
{
    private readonly Beatmap beatmap;

    public ProcessorWorkingBeatmap(byte[] content)
        : this(Decode(content))
    {
    }

    private ProcessorWorkingBeatmap(Beatmap beatmap)
        : base(beatmap.BeatmapInfo, null)
    {
        this.beatmap = beatmap;
    }

    private static Beatmap Decode(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var reader = new LineBufferedReader(stream);
        Decoder<Beatmap> decoder = Decoder.GetDecoder<Beatmap>(reader);
        return decoder.Decode(reader);
    }

    protected override IBeatmap GetBeatmap() => beatmap;
    public override Texture GetBackground() => null!;
    protected override Track GetBeatmapTrack() => null!;
    protected override ISkin GetSkin() => null!;
    public override Stream GetStream(string storagePath) => Stream.Null;
}
