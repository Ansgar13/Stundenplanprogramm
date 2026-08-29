using System;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Erzwingt eine feste Reihenfolge der Arbeitsblätter (Tabs):
    ///   1. alle übrigen Blätter (bestehende Reihenfolge bleibt, ganz links);
    ///      darin gilt zusätzlich: "Spätschwelle" und "Ausnahmen ZWK" wandern
    ///      direkt hinter das Blatt "Plan" (in dieser Reihenfolge).
    ///   2. Stundenpläne  (Name beginnt mit "LP_" oder "KP_")
    ///   3. NuHo-Blätter  (Name == "NuHo" oder beginnt mit "NuHo…", inkl. "NuHoKP_") → ganz rechts
    /// Wird vor jedem Speichern aufgerufen, damit die Reihenfolge dauerhaft konsistent bleibt.
    /// </summary>
    public static class BlattReihenfolge
    {
        public static void Anwenden(XLWorkbook wb)
        {
            if (wb == null)
                return;

            // Aktuelle Reihenfolge als Basis (stabil je Gruppe beibehalten).
            var blaetter = wb.Worksheets.OrderBy(ws => ws.Position).ToList();

            // Reihenfolge der Prüfungen ist wichtig:
            // "NuHoKP_" beginnt mit "NuHo" UND enthält "KP" – daher zuerst NuHo prüfen,
            // damit "NuHoKP_…" korrekt in die NuHo-Gruppe (ganz rechts) fällt und nur
            // reine "KP_…"-Blätter als Stundenplan gelten.
            var uebrige = blaetter.Where(ws => !IstNuHo(ws.Name) && !IstStundenplan(ws.Name)).ToList();
            var stundenplaene = blaetter.Where(ws => !IstNuHo(ws.Name) && IstStundenplan(ws.Name)).ToList();
            var nuho = blaetter.Where(ws => IstNuHo(ws.Name)).ToList();

            // Innerhalb der übrigen Blätter: "Spätschwelle" und "Ausnahmen ZWK"
            // direkt hinter das Blatt "Plan" ziehen (in dieser Reihenfolge). Alle
            // anderen Blätter behalten ihre relative Reihenfolge. Fehlt "Plan",
            // bleibt alles an seiner bisherigen Stelle.
            uebrige = OrdneHinterPlan(uebrige);

            int pos = 1;
            foreach (var ws in uebrige)       ws.Position = pos++;
            foreach (var ws in stundenplaene) ws.Position = pos++;
            foreach (var ws in nuho)          ws.Position = pos++;
        }

        // Sortiert die übrigen Blätter so, dass "Spätschwelle" und "Ausnahmen ZWK"
        // direkt hinter "Plan" stehen. Ist kein "Plan"-Blatt vorhanden, wird die
        // Liste unverändert zurückgegeben.
        private static System.Collections.Generic.List<IXLWorksheet> OrdneHinterPlan(
            System.Collections.Generic.List<IXLWorksheet> uebrige)
        {
            if (!uebrige.Any(ws => IstPlan(ws.Name)))
                return uebrige;

            // Die beiden Sonderblätter herausnehmen (jeweils höchstens eins).
            var spaet = uebrige.FirstOrDefault(ws => IstSpaetSchwelle(ws.Name));
            var ausn  = uebrige.FirstOrDefault(ws => IstAusnahmenZwk(ws.Name));

            var ergebnis = new System.Collections.Generic.List<IXLWorksheet>();
            foreach (var ws in uebrige)
            {
                // Die Sonderblätter an ihrer alten Stelle überspringen …
                if (ws == spaet || ws == ausn) continue;

                ergebnis.Add(ws);

                // … und direkt hinter "Plan" wieder einfügen.
                if (IstPlan(ws.Name))
                {
                    if (spaet != null) ergebnis.Add(spaet);
                    if (ausn  != null) ergebnis.Add(ausn);
                }
            }
            return ergebnis;
        }

        private static bool IstPlan(string name)
        {
            return name != null &&
                   name.Equals("Plan", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IstSpaetSchwelle(string name)
        {
            return name != null &&
                   (name.Equals("Spätschwelle", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SpätSchwelle", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("SpaetSchwelle", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IstAusnahmenZwk(string name)
        {
            return name != null &&
                   name.Equals("Ausnahmen ZWK", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IstNuHo(string name)
        {
            return name != null &&
                   name.StartsWith("NuHo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IstStundenplan(string name)
        {
            return name != null &&
                   (name.StartsWith("LP_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("KP_", StringComparison.OrdinalIgnoreCase));
        }
    }
}
