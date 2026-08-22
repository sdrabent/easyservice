using System.Text;
using EasyService.Core;

namespace EasyService.Tests;

/// <summary>
/// Tests for reading a file that somebody else is still writing. The cases that bite are the
/// ones nobody notices by looking: a character split across two reads, and a rotation that
/// replaces the file while the window is open.
/// </summary>
internal static class LogTailTests
{
    private static string _dir = "";

    public static IEnumerable<(string Name, Action Test)> All(string root)
    {
        _dir = Path.Combine(root, "logtail");
        Directory.CreateDirectory(_dir);

        yield return ("Angehängte Zeilen kommen an", AppendedLinesArrive);
        yield return ("Ein Umlaut über die Lesegrenze bleibt heil", SplitCharacterSurvives);
        yield return ("Rotation wird bemerkt und die neue Datei gelesen", RotationIsNoticed);
        yield return ("Die Vorschau liest nur das Ende der Datei", PreviewStartsNearTheEnd);
    }

    private static string NewFile(string name, string content = "")
    {
        var path = Path.Combine(_dir, $"{name}-{Guid.NewGuid():N}.log");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static void AppendedLinesArrive()
    {
        var path = NewFile("append", "erste\nzweite\n");

        using var tail = new LogTail(maxLines: 100);
        Assert(tail.Open(path), $"Datei liess sich nicht oeffnen: {tail.Error}");
        Assert(tail.Lines.Count == 2, $"erwartet: 2 Zeilen, gelesen: {tail.Lines.Count}");
        Assert(tail.Poll() == TailChange.None, "unveraenderte Datei meldet eine Aenderung");

        File.AppendAllText(path, "dritte\n", new UTF8Encoding(false));
        Assert(tail.Poll() == TailChange.Appended, "die angehaengte Zeile wurde nicht bemerkt");
        Assert(tail.Lines.Count == 3 && tail.Lines[2] == "dritte", "die angehaengte Zeile fehlt oder ist falsch");

        // Eine Zeile ohne Zeilenende gilt noch nicht als Zeile - sonst stuende sie halb da.
        File.AppendAllText(path, "unfertig", new UTF8Encoding(false));
        tail.Poll();
        Assert(tail.Lines.Count == 3, "eine unfertige Zeile wurde vorzeitig uebernommen");

        File.AppendAllText(path, " und fertig\n", new UTF8Encoding(false));
        tail.Poll();
        Assert(tail.Lines.Count == 4 && tail.Lines[3] == "unfertig und fertig",
            $"die vervollstaendigte Zeile stimmt nicht: {tail.Lines[^1]}");
    }

    private static void SplitCharacterSurvives()
    {
        var path = NewFile("split");
        using var tail = new LogTail(maxLines: 100);
        Assert(tail.Open(path), "Datei liess sich nicht oeffnen");

        // "ä" ist in UTF-8 zwei Bytes. Sie kommen getrennt an, mit einem Poll dazwischen -
        // genau der Fall, in dem ein frisch erzeugter Decoder Buchstabensalat liefert.
        var bytes = Encoding.UTF8.GetBytes("Grün\n");
        var boundary = 2;   // mitten im "ü"
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(bytes, 0, boundary + 1);
            stream.Flush();
        }
        tail.Poll();

        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(bytes, boundary + 1, bytes.Length - boundary - 1);
            stream.Flush();
        }
        tail.Poll();

        Assert(tail.Lines.Count == 1, $"erwartet: 1 Zeile, gelesen: {tail.Lines.Count}");
        Assert(tail.Lines[0] == "Grün", $"der Umlaut hat die Lesegrenze nicht ueberlebt: \"{tail.Lines[0]}\"");
    }

    private static void RotationIsNoticed()
    {
        var path = NewFile("rotate", "alt eins\nalt zwei\nalt drei\n");

        using var tail = new LogTail(maxLines: 100);
        Assert(tail.Open(path), "Datei liess sich nicht oeffnen");
        Assert(tail.Lines.Count == 3, "die Ausgangsdatei wurde nicht vollstaendig gelesen");

        // Rotation: dieselbe Datei, kuerzerer Inhalt.
        File.WriteAllText(path, "neu\n", new UTF8Encoding(false));

        Assert(tail.Poll() == TailChange.Rotated, "die Rotation wurde nicht bemerkt");
        Assert(tail.Lines.Count == 1 && tail.Lines[0] == "neu",
            $"nach der Rotation steht Falsches da: {string.Join(" | ", tail.Lines)}");
    }

    private static void PreviewStartsNearTheEnd()
    {
        var content = new StringBuilder();
        for (var i = 1; i <= 500; i++) content.Append($"Zeile {i}\n");
        var path = NewFile("preview", content.ToString());

        // Eine Vorschau will das Ende, nicht die Datei: nur die letzten 200 Bytes.
        using var tail = new LogTail(maxLines: 50, maxInitialBytes: 200);
        Assert(tail.Open(path), "Datei liess sich nicht oeffnen");

        Assert(tail.Lines.Count > 0, "die Vorschau hat gar nichts gelesen");
        Assert(tail.Lines.Count < 100, $"die Vorschau hat zu viel gelesen: {tail.Lines.Count} Zeilen");
        Assert(tail.Lines[^1] == "Zeile 500", $"die letzte Zeile fehlt: {tail.Lines[^1]}");

        // Die erste gelesene Zeile darf keine halbe sein.
        Assert(tail.Lines[0].StartsWith("Zeile "), $"angebrochene erste Zeile: \"{tail.Lines[0]}\"");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
