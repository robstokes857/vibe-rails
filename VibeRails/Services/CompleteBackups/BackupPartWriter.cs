namespace VibeRails.Services.Backups;

/// <summary>Streams an archive into private, durable 4 MiB files. A manifest is published only after Finish.</summary>
internal sealed class BackupPartWriter(string directory) : Stream
{
    private readonly byte[] buffer = new byte[BackupFormat.PartBytes];
    private int used;
    public List<BackupPart> Parts { get; } = [];
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Parts.Sum(p => (long)p.Bytes) + used;
    public override long Position { get => Length; set => throw new NotSupportedException(); }
    public override void Write(byte[] bytes, int offset, int count) => Write(bytes.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var take = Math.Min(bytes.Length, buffer.Length - used);
            bytes[..take].CopyTo(buffer.AsSpan(used)); used += take; bytes = bytes[take..];
            if (used == buffer.Length) SavePart();
        }
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); Write(bytes.Span); return ValueTask.CompletedTask; }
    public void Finish() { if (used != 0) SavePart(); }
    private void SavePart()
    {
        if (Parts.Count == BackupFormat.MaxParts) throw new IOException("Backup exceeds the supported archive size (200 GiB).");
        var sha = BackupFormat.Hash(buffer.AsSpan(0, used));
        var path = Path.Combine(directory, sha + ".part");
        if (!File.Exists(path))
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(path, options);
            file.Write(buffer, 0, used); file.Flush(true);
        }
        Parts.Add(new(sha, used)); used = 0;
    }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
}
