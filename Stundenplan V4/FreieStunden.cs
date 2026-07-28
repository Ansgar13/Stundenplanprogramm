using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Stundenplan_V2
{
    /// <summary>
    /// Zentrale Helfer fuer das Kriterium "freie Stunden" (ein Teilband von
    /// Stunden, das an N Tagen der Woche fuer einen Lehrer komplett frei bleiben
    /// soll). Analog zu den freien Tagen, aber auf ein Stundenband [von..bis]
    /// beschraenkt. Ein Tag zaehlt als "freies Band", wenn der Lehrer an diesem
    /// Tag in KEINER Stunde des Bandes Unterricht hat.
    ///
    /// Alles, was sowohl Loader, Solver, Validator, Diagnose als auch der
    /// Plan-Editor brauchen, liegt hier gebuendelt, damit die Zaehlweise ueberall
    /// garantiert identisch ist (so wie ZaehleFreieTage es fuer die freien Tage
    /// tut).
    /// </summary>
    public static class FreieStunden
    {
        /// <summary>
        /// Parst einen Stundenbereich der Form "5-11" (5.-11. Stunde) oder "1-1"
        /// (nur 1. Stunde). Tolerant gegen Leerzeichen und verschiedene
        /// Bindestrich-Varianten. Eine reine Zahl "3" wird als "3-3" gelesen.
        /// Rueckgabe: true bei gueltigem Bereich (1 &lt;= von &lt;= bis).
        /// </summary>
        public static bool TryParseBereich(string roh, out int von, out int bis)
        {
            von = 0; bis = 0;
            if (string.IsNullOrWhiteSpace(roh)) return false;

            // Alle ueblichen Strich-Zeichen auf '-' vereinheitlichen.
            string s = roh.Trim()
                          .Replace('\u2013', '-')   // en dash
                          .Replace('\u2014', '-')   // em dash
                          .Replace('\u2212', '-');  // minus sign
            s = new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            if (s.Length == 0) return false;

            string[] teile = s.Split('-');

            if (teile.Length == 1)
            {
                if (!int.TryParse(teile[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out von))
                    return false;
                bis = von;
            }
            else if (teile.Length == 2)
            {
                if (!int.TryParse(teile[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out von) ||
                    !int.TryParse(teile[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out bis))
                    return false;
            }
            else
            {
                return false;
            }

            if (von < 1 || bis < von) return false;
            return true;
        }

        /// <summary>Formatiert einen Bereich wieder als "von-bis" (bzw. "n" wenn von==bis).</summary>
        public static string FormatBereich(int von, int bis)
            => von == bis ? von.ToString() : $"{von}-{bis}";

        /// <summary>
        /// Zaehlt fuer einen Lehrer die Tage, an denen das Stundenband [von..bis]
        /// komplett frei ist (kein Unterricht des Lehrers in einem Band-Slot).
        /// Ein Tag, an dem das GESAMTE Band per ZWL (-3) gesperrt ist, zaehlt
        /// bewusst NICHT als frei gewaehltes Band — exakt wie bei den freien
        /// Tagen ("istFixFrei"). So bleibt die Zaehlweise konsistent.
        /// </summary>
        public static int ZaehleFreieBandTage(
            string lehrer,
            int[,] belegung,
            List<UnterrichtsBlock> blocks,
            List<ZeitSlot> slots,
            List<string> tage,
            int von,
            int bis)
        {
            int B = blocks.Count;
            int S = slots.Count;
            int frei = 0;

            foreach (var tag in tage)
            {
                // Slot-Indizes dieses Tages, die im Band liegen.
                var bandSlots = new List<int>();
                for (int s = 0; s < S; s++)
                    if (slots[s].WTag == tag && slots[s].Stunde >= von && slots[s].Stunde <= bis)
                        bandSlots.Add(s);

                if (bandSlots.Count == 0) continue; // an diesem Tag existiert das Band nicht

                // Beruehrt das Band an diesem Tag AUCH NUR EINE per ZWL (-3)
                // gesperrte Stunde, zaehlt der Tag NICHT als frei gewaehltes Band
                // -> exakt wie im Solver (freeBand=0, siehe PlanenIntern/LNS). So
                // ist das Band strikt zusaetzlich zu den ZWL-Sperren. Frueher stand
                // hier .All (nur ein KOMPLETT gesperrtes Band schloss den Tag aus);
                // das war laxer als der Solver und meldete Verletzungen zu spaet.
                bool bandBeruehrtZwlFrei = bandSlots.Any(s =>
                    slots[s].LehrerWunsch != null &&
                    slots[s].LehrerWunsch.TryGetValue(lehrer, out int lw) && lw == -3);
                if (bandBeruehrtZwlFrei) continue;

                bool hatUnterrichtImBand = false;
                for (int b = 0; b < B && !hatUnterrichtImBand; b++)
                {
                    if (!blocks[b].Teile.Any(t => t.Lehrer == lehrer)) continue;
                    foreach (int s in bandSlots)
                        if (belegung[b, s] == 1) { hatUnterrichtImBand = true; break; }
                }

                if (!hatUnterrichtImBand) frei++;
            }

            return frei;
        }
    }
}
