using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public class UnterrichtsBlock
    {
        public int UNr { get; set; }
        public int Wst { get; set; }
        public string Zeilentext { get; set; } = "";

        // NEU: ZeilenText-2 (zusätzliche Spalte in U-Verteilung)
        public string Zeilentext2 { get; set; } = "";
        public int WochenDoppelstunden { get; set; }
        public bool DoppelÜberPauseErlaubt { get; set; } = false; // (E)-Spalte: x = erlaubt

        // NEU: KKK = Klassen-Konflikt-Kennzeichen.
        // Blöcke mit gleichem nicht-leeren KKK dürfen gleichzeitig
        // (im selben Slot) verplant werden, auch wenn sie dieselbe
        // Klasse haben (z.B. Religion/Ethik parallel).
        public string KKK { get; set; } = "";

        // NEU: WochenGruppe = "A", "B" oder "" (jede Woche).
        // Blöcke mit unterschiedlichen Werten ("A" vs "B") kollidieren
        // NIE und dürfen denselben Slot / Lehrer-Slot / Fachraum teilen.
        public string WochenGruppe { get; set; } = "";

        public Dictionary<string, int> TagesDoppelstunden { get; set; } = new();
        public List<TeilUnterricht> Teile { get; set; } = new();

        // FACHRAUM-BEDARF dieses Blocks fuer eine Fachgruppe.
        // Zaehlt JEDEN Teilunterricht dieser Fachgruppe einzeln: zwei
        // Sportunterrichte unter derselben UNr belegen damit auch zwei
        // Sport-Fachraeume (frueher zaehlte der Block pauschal 1×, egal
        // wie viele Teile). Ein Teil mit mehreren Klassen bleibt EIN
        // Bedarf (= ein Raum), da es ein gemeinsamer Unterricht ist.
        // Diese Methode ist die EINZIGE Definition der Zaehlung — Solver
        // (RoomConstraint), Pruefung (PlanValidator), Diagnose
        // (StundenplanEngine) und Editor (PlanEditorDialog) rufen sie auf,
        // damit alle vier exakt gleich rechnen.
        public int FachraumBedarf(string fachGruppe)
            => Teile.Count(t => t.FachGruppe == fachGruppe);
    }
    public class TeilUnterricht
    {
        public int UNr { get; set; }
        public string Lehrer { get; set; } = "";
        public string Fach { get; set; } = "";
        public List<string> Klassen { get; set; } = new();
        public int MinDoppel { get; set; }
        public int MaxDoppel { get; set; }
        public string FachGruppe { get; set; }
        public int AktuelleDoppelstunden { get; set; }
        public string Ltkz { get; set; } = "";
        public bool DoppelÜberPauseErlaubt { get; set; } = false; // (E)-Spalte
    }

    // Ein in der UV mit "i"/"x" ignorierter (Teil-)Unterricht.
    // Diese Zeilen werden vom Solver nicht geladen; sie dienen nur der
    // Anzeige im Parkbereich des Plan-Editors ("Ignorierte anzeigen").
    public class IgnorierterUnterricht
    {
        public int UNr { get; set; }
        public string Lehrer { get; set; } = "";
        public string Fach { get; set; } = "";
        public List<string> Klassen { get; set; } = new();
        public int Wst { get; set; }
    }


}
