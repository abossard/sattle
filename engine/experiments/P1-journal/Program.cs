// P1: local journal with group commit. One writer thread owns an append-only segment file; one producer enqueues records of 100 bytes
// at a fixed rate into a ring; the writer waits until the group deadline T after the first record of the group, writes all queued
// records as length-prefixed CRC32 frames, flushes to stable storage, then acknowledges every record of the group.
//   grid  DIR  SYNC  T_MS  RATE  SECONDS          one cell (SYNC = fsync | full)
//   crash DIR  SYNC  T_MS  RATE  RUNS             SIGKILL at a random time, recover, check
//   child DIR  SYNC  T_MS  RATE                    (used by crash)
using System.Diagnostics;
using System.Runtime.InteropServices;

static class Native
{
    [DllImport("libc", SetLastError = true)] public static extern int fcntl(int fd, int cmd, int arg);
    [DllImport("libc", SetLastError = true)] public static extern int fsync(int fd);
    public const int F_FULLFSYNC = 51;
}

static class Crc
{
    static readonly uint[] T = Make();
    static uint[] Make() { var t = new uint[256]; for (uint i = 0; i < 256; i++) { var c = i; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; t[i] = c; } return t; }
    public static uint Of(ReadOnlySpan<byte> d) { var c = 0xFFFFFFFFu; foreach (var b in d) c = T[(c ^ b) & 0xFF] ^ (c >> 8); return ~c; }
}

sealed class Ring(int capacity)
{
    public readonly long[] Seq = new long[capacity]; public readonly long[] Enq = new long[capacity];
    public long Head, Tail; // Tail written by the producer, Head by the consumer
    public int Capacity => capacity;
}

sealed class Journal : IDisposable
{
    readonly FileStream fs; readonly string sync; readonly SafeHandleHolder handle;
    public Journal(string path, string sync) { fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.None); this.sync = sync; handle = new SafeHandleHolder(fs); }
    sealed class SafeHandleHolder(FileStream f) { public int Fd => (int)f.SafeFileHandle.DangerousGetHandle(); }
    public void Write(ReadOnlySpan<byte> b) => fs.Write(b);
    /// <summary>fsync: FileStream.Flush(true) (what .NET does on this platform) or F_FULLFSYNC through fcntl.</summary>
    public void Sync()
    {
        fs.Flush(false);
        if (sync == "full") { if (Native.fcntl(handle.Fd, Native.F_FULLFSYNC, 0) != 0) throw new IOException("F_FULLFSYNC failed " + Marshal.GetLastWin32Error()); }
        else fs.Flush(true);
    }
    public void Dispose() => fs.Dispose();
}

static class Program
{
    const int Payload = 100; const int Frame = 4 + 4 + 8 + Payload;
    static long ToNs(long ticks) => (long)(ticks * 1e9 / Stopwatch.Frequency);

    // returns (acked, fsyncs, latencies in microseconds)
    static (long Acked, long Fsyncs, int[] LatUs, long Produced, double Seconds) RunCell(string dir, string sync, double tMs, double rate, double seconds, double warm, string? ackFile)
    {
        var ring = new Ring(1 << 20); var path = Path.Combine(dir, $"p1-{Environment.ProcessId}.seg");
        using var journal = new Journal(path, sync);
        var stop = false; long fsyncs = 0, acked = 0;
        var lat = new int[(long)((seconds + warm) * rate * 1.2) + 1024]; var latN = 0L;
        var ackStream = ackFile is null ? null : new FileStream(ackFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.None);
        var t0 = Stopwatch.GetTimestamp(); var warmEnd = t0 + (long)(warm * Stopwatch.Frequency); var end = warmEnd + (long)(seconds * Stopwatch.Frequency);
        var tTicks = (long)(tMs / 1000.0 * Stopwatch.Frequency);
        var writer = new Thread(() =>
        {
            var buf = new byte[Frame * (1 << 20) / 4 + 64]; var payload = new byte[Payload]; new Random(7).NextBytes(payload);
            var ackBuf = new byte[8];
            while (true)
            {
                if (Volatile.Read(ref ring.Head) == Volatile.Read(ref ring.Tail)) { if (Volatile.Read(ref stop)) break; Thread.SpinWait(50); continue; }
                var first = ring.Enq[ring.Head & (ring.Capacity - 1)];
                var deadline = first + tTicks;
                while (true)
                {
                    var now = Stopwatch.GetTimestamp(); var rem = deadline - now; if (rem <= 0) break;
                    if (rem > Stopwatch.Frequency / 500) Thread.Sleep(1); else Thread.SpinWait(20);
                }
                var head = ring.Head; var tail = Volatile.Read(ref ring.Tail); var n = 0;
                var maxN = buf.Length / Frame;
                for (var i = head; i < tail && n < maxN; i++, n++)
                {
                    var o = n * Frame; var seq = ring.Seq[i & (ring.Capacity - 1)];
                    BitConverter.TryWriteBytes(buf.AsSpan(o, 4), Payload + 8); BitConverter.TryWriteBytes(buf.AsSpan(o + 8, 8), seq); payload.CopyTo(buf.AsSpan(o + 16));
                    BitConverter.TryWriteBytes(buf.AsSpan(o + 4, 4), Crc.Of(buf.AsSpan(o + 8, Payload + 8)));
                }
                journal.Write(buf.AsSpan(0, n * Frame)); journal.Sync(); fsyncs++;
                var ack = Stopwatch.GetTimestamp();
                if (ackStream is not null) { BitConverter.TryWriteBytes(ackBuf, ring.Seq[(head + n - 1) & (ring.Capacity - 1)]); ackStream.Position = 0; ackStream.Write(ackBuf); }
                for (var i = 0; i < n; i++) { var e = ring.Enq[(head + i) & (ring.Capacity - 1)]; if (e >= warmEnd && latN < lat.Length) lat[latN++] = (int)Math.Min(int.MaxValue, (ack - e) * 1_000_000 / Stopwatch.Frequency); }
                acked += n; Volatile.Write(ref ring.Head, head + n);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        writer.Start();
        long seqNo = 0; var step = Stopwatch.Frequency / rate;
        while (true)
        {
            var now = Stopwatch.GetTimestamp(); if (now >= end) break;
            var due = t0 + (long)(seqNo * step);
            if (now < due) { if (due - now > Stopwatch.Frequency / 2000) Thread.Sleep(0); else Thread.SpinWait(10); continue; }
            var tail = ring.Tail;
            if (tail - Volatile.Read(ref ring.Head) >= ring.Capacity - 1) { Thread.SpinWait(50); continue; }   // full: the offered load is not met
            ring.Seq[tail & (ring.Capacity - 1)] = seqNo; ring.Enq[tail & (ring.Capacity - 1)] = now;
            Volatile.Write(ref ring.Tail, tail + 1); seqNo++;
        }
        Volatile.Write(ref stop, true); writer.Join();
        var total = (Stopwatch.GetTimestamp() - warmEnd) / (double)Stopwatch.Frequency;
        ackStream?.Dispose(); File.Delete(path);
        return (acked, fsyncs, lat.AsSpan(0, (int)latN).ToArray(), seqNo, seconds);
    }

    static int Pct(int[] sorted, double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];

    static int Main(string[] a)
    {
        var mode = a[0]; var dir = a[1]; Directory.CreateDirectory(dir);
        if (mode == "grid")
        {
            var sync = a[2]; var t = double.Parse(a[3]); var rate = double.Parse(a[4]); var secs = double.Parse(a[5]);
            var r = RunCell(dir, sync, t, rate, secs, 5, null);
            var l = r.LatUs; Array.Sort(l);
            Console.WriteLine($"{sync} | T {t} ms | offered {rate:N0}/s | acked {r.Acked / (secs + 5):N0}/s over {secs + 5:F0} s | fsyncs {r.Fsyncs / (secs + 5):F0}/s | p50 {Pct(l, .5) / 1000.0:F2} ms | p99 {Pct(l, .99) / 1000.0:F2} ms | max {(l.Length > 0 ? l[^1] : 0) / 1000.0:F2} ms | samples {l.Length:N0}");
            return 0;
        }
        if (mode == "raw")
        {
            // the cost of the flush alone: append 4 KB, flush, 2,000 times
            var sync = a[2]; using var j = new Journal(Path.Combine(dir, "raw.seg"), sync); var buf = new byte[4096]; var xs = new List<double>();
            for (var i = 0; i < 2000; i++) { var t = Stopwatch.GetTimestamp(); j.Write(buf); j.Sync(); xs.Add((Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency); }
            xs.Sort(); Console.WriteLine($"{sync} | 4 KB append + flush x 2,000 | p50 {xs[1000]:F2} ms | p99 {xs[1980]:F2} ms | max {xs[^1]:F2} ms");
            File.Delete(Path.Combine(dir, "raw.seg")); return 0;
        }
        if (mode == "child")
        {
            RunCell(dir, a[2], double.Parse(a[3]), double.Parse(a[4]), 600, 0, Path.Combine(dir, "acked.bin"));
            return 0;
        }
        if (mode == "crash")
        {
            var sync = a[2]; var t = a[3]; var rate = a[4]; var runs = int.Parse(a[5]); var ok = 0; var rnd = new Random(20261111);
            for (var run = 0; run < runs; run++)
            {
                var rd = Path.Combine(dir, "crash"); if (Directory.Exists(rd)) Directory.Delete(rd, true); Directory.CreateDirectory(rd);
                var psi = new ProcessStartInfo(Environment.ProcessPath!, [typeof(Program).Assembly.Location, "child", rd, sync, t, rate]) { UseShellExecute = false };
                using var p = Process.Start(psi)!;
                Thread.Sleep(2000 + rnd.Next(0, 6000)); p.Kill(true); p.WaitForExit();
                var seg = Directory.GetFiles(rd, "p1-*.seg").Single(); var ackFile = Path.Combine(rd, "acked.bin");
                long lastAck = -1; if (File.Exists(ackFile) && new FileInfo(ackFile).Length >= 8) lastAck = BitConverter.ToInt64(File.ReadAllBytes(ackFile));
                var bytes = File.ReadAllBytes(seg); long pos = 0, good = 0, expect = 0; var dup = 0; var gap = 0; var badAt = -1L;
                while (pos + 8 <= bytes.Length)
                {
                    var len = BitConverter.ToInt32(bytes, (int)pos); var crc = BitConverter.ToUInt32(bytes, (int)pos + 4);
                    if (len != Payload + 8 || pos + 8 + len > bytes.Length || Crc.Of(bytes.AsSpan((int)pos + 8, len)) != crc) { badAt = pos; break; }
                    var seq = BitConverter.ToInt64(bytes, (int)pos + 8);
                    if (seq < expect) dup++; else if (seq > expect) gap++;
                    expect = seq + 1; good++; pos += 8 + len;
                }
                var torn = bytes.Length - pos;
                var pass = good - 1 >= lastAck && dup == 0 && gap == 0;
                if (pass) ok++;
                Console.WriteLine($"run {run + 1}: killed after file {bytes.Length:N0} bytes, {good:N0} good frames, torn tail {torn} bytes, last acknowledged {lastAck:N0}, last recovered {good - 1:N0}, duplicates {dup}, gaps {gap}, {(pass ? "OK" : "FAIL")}");
            }
            Console.WriteLine($"crash: {ok} of {runs} runs recovered with every acknowledged record present and none duplicated");
            return ok == runs ? 0 : 1;
        }
        return 2;
    }
}
