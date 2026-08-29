using System;
using System.Collections.Generic;

namespace Stundenplan_V2
{
    /// <summary>
    /// Ausnahmen von der harten ZWK-Sperre (-3). Quelle: Sheet "Ausnahmen ZWK"
    /// (Spalte A = Fach, Spalte B = kommaseparierte Klassen). Es koennen
    /// mehrere Faecher (mehrere Zeilen) genannt werden.
    ///
    /// Bedeutung: Fuer die betreffenden Unterrichte des gelisteten Fachs werden
    /// die Klassen-Zeitwuensche (-3) der genannten Klassen NICHT als Sperre
    /// gewertet — der Unterricht darf also auch in sonst gesperrten Slots liegen.
    ///
    /// WICHTIG — block-weite Wirkung: Die Ausnahme gilt fuer die GESAMTE UNr.
    /// Enthaelt ein Block (UNr) irgendeinen Teilunterricht mit einem hier
    /// gelisteten Fach, so ist die -3-Sperre der dort genannten Klassen fuer
    /// den ganzen Block abgeschaltet — auch fuer weitere Teilunterrichte
    /// derselben UNr mit anderem Fach.
    ///
    /// Es wird AUSSCHLIESSLICH die harte -3-Sperre gelockert. Alle uebrigen
    /// Regeln (Klassenkollision, Fachraeume, Doppelstunden, Bewertung) bleiben
    /// unveraendert. Fuer Klassen existiert ohnehin nur der Wert -3 (harte
    /// Sperre); -2/weiche Klassenwuensche gibt es nicht.
    ///
    /// Statische "Aktuell"-Instanz analog zu den uebrigen einmalig beim Laden
    /// gesetzten Konfigurationen (z. B. PlanBewertung.ErsteSpaeteStunde):
    /// ExcelLoader setzt sie bei jedem Laden neu, alle Pruefstellen lesen sie.
    /// </summary>
    public sealed class AusnahmenZwk
    {
        // Fach (Ordinal, Gross/Klein egal) -> Klassen (exakt wie in ZWK/UV).
        private readonly Dictionary<string, HashSet<string>> _proFach;

        /// <summary>Leere Ausnahme = altes Verhalten (keine Lockerung).</summary>
        public static readonly AusnahmenZwk Leer = new AusnahmenZwk(null);

        /// <summary>
        /// Aktuell geltende Ausnahmen. Wird beim Excel-Laden gesetzt und an
        /// allen -3-Pruefstellen gelesen. Default = Leer (unveraendert).
        /// </summary>
        public static AusnahmenZwk Aktuell { get; set; } = Leer;

        public AusnahmenZwk(Dictionary<string, HashSet<string>> proFach)
        {
            // Comparer hart erzwingen, damit Aufrufer sich nicht darum kuemmern
            // muessen: Fach case-insensitiv (wie DoppelSelberTagFaecher etc.),
            // Klassen exakt (wie die Schluessel in ZeitSlot.KlassenWunsch).
            _proFach = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (proFach != null)
            {
                foreach (var kv in proFach)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                    var set = new HashSet<string>(kv.Value, StringComparer.Ordinal);
                    set.RemoveWhere(string.IsNullOrWhiteSpace);
                    if (set.Count > 0) _proFach[kv.Key.Trim()] = set;
                }
            }
        }

        public bool IstLeer => _proFach.Count == 0;

        /// <summary>Anzahl gelisteter Faecher (fuer Diagnose/Log).</summary>
        public int FachAnzahl => _proFach.Count;

        /// <summary>
        /// Ist die -3-Klassensperre der Klasse <paramref name="klasse"/> fuer
        /// diesen Block ignoriert? Trifft zu, sobald IRGENDEIN Teilunterricht
        /// des Blocks ein gelistetes Fach traegt, dessen Klassenliste die
        /// Klasse enthaelt (block-weite Wirkung, s. Klassenkommentar).
        /// </summary>
        public bool IstIgnoriert(UnterrichtsBlock block, string klasse)
        {
            if (_proFach.Count == 0 || block == null || string.IsNullOrEmpty(klasse))
                return false;

            foreach (var t in block.Teile)
            {
                if (t?.Fach == null) continue;
                if (_proFach.TryGetValue(t.Fach, out var klassen) && klassen.Contains(klasse))
                    return true;
            }
            return false;
        }
    }
}
