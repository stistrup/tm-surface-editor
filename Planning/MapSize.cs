using System.Collections.Concurrent;
using System.IO.Compression;

namespace TmSurfaceEditor;

// the saved map after shadows and compress: the original (its lightmap stands in for ours) + the files it embeds
internal sealed record MapSize(long Bytes, long Limit, int Unbuilt, List<MapSize.Piece> Pieces)
{
    // Bytes -1: not built yet; Blocks/Items: the originals it was placed for, by index
    public sealed record Piece(string Name, long Bytes, List<int> Blocks, List<int> Items);

    // rooms take 7 MiB
    public const long RoomLimit = 7 * 1024 * 1024;

    public static MapSize Of(string mapPath, ConversionRun run, Dictionary<object, int> blockOrigin, Dictionary<object, int> itemOrigin)
    {
        var uses = run.Customs.Select(c => ((object)c.Key, c.Value.FilePath)).Concat(run.PlacedFor);
        var pieces = uses.GroupBy(u => u.Item2).Select(g => new Piece(NameOf(g.Key), BytesOf(g.Key),
                IndicesIn(g.Select(u => u.Item1), blockOrigin), IndicesIn(g.Select(u => u.Item1), itemOrigin)))
            .OrderByDescending(p => p.Bytes).ToList();
        return new(new FileInfo(mapPath).Length + pieces.Sum(p => Math.Max(p.Bytes, 0)), RoomLimit, pieces.Count(p => p.Bytes < 0), pieces);
    }

    private static readonly ConcurrentDictionary<(string, long, DateTime), long> Deflated = new();

    // as the map's zip stores it
    private static long BytesOf(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return -1;
        return Deflated.GetOrAdd((path, file.Length, file.LastWriteTimeUtc), _ =>
        {
            var to = new CountingStream();
            using (var deflate = new DeflateStream(to, CompressionLevel.Optimal, leaveOpen: true))
            using (var from = file.OpenRead())
                from.CopyTo(deflate);
            return to.Length;
        });
    }

    private sealed class CountingStream : Stream
    {
        private long length;
        public override long Length => length;
        public override void Write(byte[] buffer, int offset, int count) => length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => length += buffer.Length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static string NameOf(string path) => Path.GetFileName(path).Split('.')[0];

    private static List<int> IndicesIn(IEnumerable<object> pieces, Dictionary<object, int> origin)
        => [.. pieces.Where(origin.ContainsKey).Select(p => origin[p]).Distinct().Order()];
}
