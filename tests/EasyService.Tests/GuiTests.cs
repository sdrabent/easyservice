using EasyService.Core;

namespace EasyService.Tests;

/// <summary>
/// Tests for the parts of the main window that are easy to break and hard to notice: the
/// list has to stay multi-selectable, health must be visible without relying on colour, and
/// an empty list has to say something instead of showing bare column headers.
///
/// These build the real window, so they cover the wiring, not a copy of it.
/// </summary>
internal static class GuiTests
{
    public static IEnumerable<(string Name, Action Test)> All()
    {
        yield return ("Die Dienstliste erlaubt Mehrfachauswahl", ListAllowsMultiSelect);
        yield return ("Zustand hat ein Symbol, nicht nur eine Farbe", StatusHasIcons);
        yield return ("Eine leere Liste erklärt sich", EmptyListExplainsItself);
    }

    private static void WithMainForm(Action<Gui.MainForm> check)
    {
        using var form = new Gui.MainForm();
        form.Size = new Size(1100, 600);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-4000, -4000);   // kein Fenster vor der Nase des Benutzers
        form.Show();
        try
        {
            Application.DoEvents();
            check(form);
        }
        finally
        {
            form.Hide();
        }
    }

    private static ListView List(Control root) =>
        Descend(root).OfType<ListView>().FirstOrDefault()
        ?? throw new Exception("keine Dienstliste im Fenster gefunden");

    private static void ListAllowsMultiSelect() => WithMainForm(form =>
    {
        var list = List(form);
        Assert(list.MultiSelect, "die Liste laesst nur einen Dienst gleichzeitig auswaehlen");
    });

    private static void StatusHasIcons() => WithMainForm(form =>
    {
        var list = List(form);
        Assert(list.SmallImageList is not null, "der Liste fehlen die Zustandssymbole");

        // Ok, Warnung, Kritisch, Unbekannt und "nichts zu sagen".
        Assert(list.SmallImageList!.Images.Count == 5,
            $"erwartet: 5 Zustandssymbole, vorhanden: {list.SmallImageList.Images.Count}");

        // Die Symbole muessen sich auch ohne Farbe unterscheiden: gleiche Form waere fuer
        // Rotgruenblinde dasselbe Bild.
        var shapes = Enumerable.Range(0, 4).Select(i => Silhouette(list.SmallImageList.Images[i])).ToList();
        Assert(shapes.Distinct().Count() == shapes.Count,
            "zwei Zustandssymbole haben dieselbe Form und unterscheiden sich nur durch die Farbe");
    });

    /// <summary>Coarse fingerprint of which pixels are painted at all - colour deliberately ignored.</summary>
    private static string Silhouette(Image image)
    {
        using var bitmap = new Bitmap(image, new Size(8, 8));
        var sb = new System.Text.StringBuilder(64);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
                sb.Append(bitmap.GetPixel(x, y).A > 40 ? '#' : '.');
        return sb.ToString();
    }

    private static void EmptyListExplainsItself() => WithMainForm(form =>
    {
        var list = List(form);

        // Ein Filter, der auf nichts passt: die Liste ist leer, das Fenster darf es nicht
        // bei zwoelf Spaltenueberschriften belassen.
        var filter = Descend(form).OfType<ToolStrip>()
                                  .SelectMany(t => t.Items.OfType<ToolStripTextBox>())
                                  .FirstOrDefault()
                     ?? throw new Exception("kein Filterfeld gefunden");
        filter.Text = "kein-dienst-heisst-so-" + Guid.NewGuid().ToString("N");
        Application.DoEvents();

        Assert(list.Items.Count == 0, $"der Filter greift nicht: {list.Items.Count} Zeilen uebrig");
        Assert(!list.Visible, "die leere Liste steht immer noch im Weg");

        var visibleText = Descend(form).OfType<Label>()
                                       .Where(l => l.Visible && l.Text.Length > 0)
                                       .Select(l => l.Text)
                                       .ToList();
        Assert(visibleText.Count > 0, "der Leerzustand sagt nichts");
    });

    private static IEnumerable<Control> Descend(Control control) =>
        new[] { control }.Concat(control.Controls.Cast<Control>().SelectMany(Descend))
                         .Concat(Descend(control as ToolStrip));

    private static IEnumerable<Control> Descend(ToolStrip? strip) =>
        strip is null
            ? Enumerable.Empty<Control>()
            : strip.Items.OfType<ToolStripControlHost>().Select(h => h.Control).Where(c => c is not null);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
