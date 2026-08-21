using System.Text;

namespace EasyService.Core;

/// <summary>
/// Append-only file writer with size- and time-based rotation. Opened with
/// FileShare.ReadWrite|Delete so the built-in log viewer can tail the file while
/// the service keeps writing, and so rotation never fails on a locked file.
/// </summary>
public sealed class LogWriter : IDisposable
{
    private readonly object _lock = new();
    private readonly string _path;
    private readonly bool _timestamp;
    private readonly long _rotateBytes;
    private readonly TimeSpan _rotateInterval;
    private readonly int _keep;
    private readonly bool _rotate;

    private FileStream? _stream;
    private DateTime _openedUtc;
    private StringBuilder _pending = new();

    public LogWriter(string path, bool append, bool timestamp, bool rotate, long rotateBytes, int rotateSeconds, int keep)
    {
        _path = path;
        _timestamp = timestamp;
        _rotate = rotate;
        _rotateBytes = rotateBytes > 0 ? rotateBytes : 0;
        _rotateInterval = rotateSeconds > 0 ? TimeSpan.FromSeconds(rotateSeconds) : TimeSpan.Zero;
        _keep = keep;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Open(append);
    }

    private void Open(bool append)
    {
        _stream = new FileStream(_path, append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
        _openedUtc = DateTime.UtcNow;
    }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_lock)
        {
            var payload = _timestamp ? Stamp(text) : text;
            if (payload.Length == 0) return;
            WriteRaw(Encoding.UTF8.GetBytes(payload));
        }
    }

    /// <summary>Writes one complete line, always timestamped. Used for supervisor events.</summary>
    public void WriteLine(string line)
    {
        lock (_lock)
        {
            var stamped = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{System.Environment.NewLine}";
            WriteRaw(Encoding.UTF8.GetBytes(stamped));
        }
    }

    private void WriteRaw(byte[] bytes)
    {
        if (_stream is null) return;
        try
        {
            RotateIfNeeded(bytes.Length);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }
        catch (IOException)
        {
            // Disk full / file removed underneath us: drop the chunk rather than killing the service.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string Stamp(string text)
    {
        // Prefix every complete line; hold an incomplete tail back until its newline arrives.
        _pending.Append(text);
        var buffer = _pending.ToString();
        var lastBreak = buffer.LastIndexOf('\n');
        if (lastBreak < 0)
        {
            if (_pending.Length > 64 * 1024)   // pathological single line: flush anyway
            {
                _pending.Clear();
                return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {buffer}";
            }
            return "";
        }

        var complete = buffer[..(lastBreak + 1)];
        _pending = new StringBuilder(buffer[(lastBreak + 1)..]);

        var sb = new StringBuilder(complete.Length + 64);
        var stamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ";
        foreach (var line in complete.Split('\n'))
        {
            if (line.Length == 0) continue;
            sb.Append(stamp).Append(line.TrimEnd('\r')).Append(System.Environment.NewLine);
        }
        return sb.ToString();
    }

    private void RotateIfNeeded(int incoming)
    {
        if (!_rotate || _stream is null) return;

        var bySize = _rotateBytes > 0 && _stream.Length + incoming > _rotateBytes;
        var byTime = _rotateInterval > TimeSpan.Zero && DateTime.UtcNow - _openedUtc >= _rotateInterval;
        if (!bySize && !byTime) return;
        if (_stream.Length == 0) return;

        Rotate();
    }

    public void Rotate()
    {
        lock (_lock)
        {
            if (_stream is null) return;
            try
            {
                _stream.Flush();
                _stream.Dispose();
                _stream = null;

                var dir = Path.GetDirectoryName(Path.GetFullPath(_path))!;
                var baseName = Path.GetFileNameWithoutExtension(_path);
                var ext = Path.GetExtension(_path);
                var archive = Path.Combine(dir, $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");

                var counter = 1;
                while (File.Exists(archive))
                    archive = Path.Combine(dir, $"{baseName}-{DateTime.Now:yyyyMMdd-HHmmss}-{counter++}{ext}");

                File.Move(_path, archive);
                PruneArchives(dir, baseName, ext);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                try { Open(append: true); } catch (IOException) { }
            }
        }
    }

    private void PruneArchives(string dir, string baseName, string ext)
    {
        if (_keep <= 0) return;
        try
        {
            var archives = Directory.GetFiles(dir, $"{baseName}-*{ext}")
                                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                                    .Skip(_keep)
                                    .ToList();
            foreach (var old in archives)
            {
                try { File.Delete(old); } catch { /* best effort */ }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_timestamp && _pending.Length > 0)
            {
                var tail = _pending.ToString();
                _pending.Clear();
                try
                {
                    var bytes = Encoding.UTF8.GetBytes($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tail}{System.Environment.NewLine}");
                    _stream?.Write(bytes, 0, bytes.Length);
                }
                catch { /* closing anyway */ }
            }
            try { _stream?.Flush(); } catch { }
            _stream?.Dispose();
            _stream = null;
        }
    }

    /// <summary>Lists the current file plus its rotated archives, newest first.</summary>
    public static List<string> FindLogFiles(string path)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(path)) return result;
        try
        {
            var full = Path.GetFullPath(System.Environment.ExpandEnvironmentVariables(path));
            var dir = Path.GetDirectoryName(full);
            if (dir is null || !Directory.Exists(dir)) return result;

            if (File.Exists(full)) result.Add(full);

            var baseName = Path.GetFileNameWithoutExtension(full);
            var ext = Path.GetExtension(full);
            result.AddRange(Directory.GetFiles(dir, $"{baseName}-*{ext}")
                                     .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
        }
        return result;
    }
}
