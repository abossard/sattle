using System.Text;
using System.Text.Json;

namespace Sattle.Cli;

/// <summary>Follows complete UTF-8 envelopes written by a local adapter. Positions are byte offsets after a newline.</summary>
internal sealed class AppendFileSource(string path) : ILiveSource
{
    private readonly string file = Path.GetFullPath(path);
    public string Id => "file:" + file;

    public IEnumerable<LiveEvent?> Read(string? after, CancellationToken ct)
    {
        using var available = new AutoResetEvent(false);
        using var watcher = new FileSystemWatcher(Path.GetDirectoryName(file)!, Path.GetFileName(file));
        watcher.Changed += (_, _) => available.Set();
        watcher.Error += (_, _) => available.Set();
        watcher.EnableRaisingEvents = true;
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var offset = after is null ? 0L : JsonSerializer.Deserialize<long>(after);
        if (offset > input.Length) throw new IOException("Input is shorter than its saved position");
        input.Position = offset;
        using var line = new MemoryStream();
        var buffer = new byte[65536];
        WaitHandle[] wake = [available, ct.WaitHandle];
        while (!ct.IsCancellationRequested)
        {
            var count = input.Read(buffer, 0, buffer.Length);
            if (count == 0)
            {
                yield return null;
                WaitHandle.WaitAny(wake, TimeSpan.FromSeconds(2));
                continue;
            }
            for (var i = 0; i < count; i++)
            {
                offset++;
                if (buffer[i] != '\n') { line.WriteByte(buffer[i]); continue; }
                var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, checked((int)line.Length)).TrimEnd('\r');
                line.SetLength(0);
                if (text.Length > 0) yield return new LiveEvent(text, JsonSerializer.Serialize(offset));
            }
        }
    }
}
