using System.Text;

namespace EasyService.Core;

/// <summary>What a <see cref="LogTail.Poll"/> found.</summary>
public enum TailChange
{
    /// <summary>The file has not grown.</summary>
    None,

    /// <summary>New lines arrived.</summary>
    Appended,

    /// <summary>The file was rotated away or replaced; the tail reopened it from the start.</summary>
    Rotated,
}

/// <summary>
/// Reads a log file while somebody else keeps writing to it.
///
/// Two details matter and are easy to get wrong. The file is opened with
/// FileShare.ReadWrite | FileShare.Delete, because the supervisor has it open for writing and
/// rotation renames it out from under us - without Delete sharing, rotation would fail while
/// the window is open, which turns a viewer into a fault. And the decoder is kept across
/// reads: a UTF-8 character can be split across two reads, and a fresh decoder each time
/// turns the umlaut on that boundary into rubbish.
/// </summary>
public sealed class LogTail : IDisposable
{
    private readonly int _maxLines;
    private readonly long _maxInitialBytes;
    private readonly List<string> _lines = new();

    private FileStream? _stream;
    private long _position;
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private string _partial = "";

    /// <param name="maxLines">How many lines to keep in memory; older ones fall off the front.</param>
    /// <param name="maxInitialBytes">
    /// When opening a file that is already large, start this many bytes before its end instead
    /// of reading all of it. 0 reads the whole file. A preview only wants the last screenful;
    /// a viewer with a search box wants everything.
    /// </param>
    public LogTail(int maxLines, long maxInitialBytes = 0)
    {
        _maxLines = Math.Max(1, maxLines);
        _maxInitialBytes = Math.Max(0, maxInitialBytes);
    }

    public string Path { get; private set; } = "";

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Why the last <see cref="Open"/> failed, or null.</summary>
    public string? Error { get; private set; }

    public bool IsOpen => _stream is not null;

    /// <summary>Opens a file and reads what is already in it. False when it could not be opened.</summary>
    public bool Open(string path)
    {
        Close();
        Path = path;
        Error = null;

        try
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                     FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e)
        {
            Error = e.Message;
            return false;
        }

        if (_maxInitialBytes > 0 && _stream.Length > _maxInitialBytes)
        {
            // Mitten in eine Zeile zu springen ist in Ordnung: die angebrochene erste Zeile
            // wird beim Lesen verworfen, weil sie nicht vollstaendig ist.
            _position = _stream.Length - _maxInitialBytes;
            ReadNew(dropFirstPartialLine: true);
        }
        else
        {
            ReadNew(dropFirstPartialLine: false);
        }

        return true;
    }

    /// <summary>Picks up whatever was written since the last call.</summary>
    public TailChange Poll()
    {
        if (_stream is null || Path.Length == 0) return TailChange.None;

        try
        {
            var info = new FileInfo(Path);

            // Kleiner als unsere Leseposition heisst: die Datei wurde ersetzt, nicht ergaenzt.
            if (!info.Exists || info.Length < _position)
            {
                var path = Path;
                Close();
                _lines.Clear();
                return Open(path) ? TailChange.Rotated : TailChange.None;
            }

            if (info.Length == _position) return TailChange.None;

            var before = _lines.Count;
            ReadNew(dropFirstPartialLine: false);
            return _lines.Count != before ? TailChange.Appended : TailChange.None;
        }
        catch (IOException)
        {
            return TailChange.None;   // die Datei ist gerade in Bewegung, beim naechsten Mal wieder
        }
    }

    private void ReadNew(bool dropFirstPartialLine)
    {
        if (_stream is null) return;

        _stream.Seek(_position, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var chars = new char[64 * 1024];
        var sb = new StringBuilder(_partial);
        _partial = "";

        int read;
        while ((read = _stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var count = _decoder.GetChars(buffer, 0, read, chars, 0);
            sb.Append(chars, 0, count);
            _position += read;
        }

        var text = sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = text.Split('\n');

        var first = dropFirstPartialLine && parts.Length > 1 ? 1 : 0;
        for (var i = first; i < parts.Length - 1; i++) _lines.Add(parts[i]);
        _partial = parts[^1];

        if (_lines.Count > _maxLines) _lines.RemoveRange(0, _lines.Count - _maxLines);
    }

    private void Close()
    {
        _stream?.Dispose();
        _stream = null;
        _position = 0;
        _partial = "";
        _decoder = Encoding.UTF8.GetDecoder();
    }

    public void Dispose()
    {
        Close();
        _lines.Clear();
    }
}
