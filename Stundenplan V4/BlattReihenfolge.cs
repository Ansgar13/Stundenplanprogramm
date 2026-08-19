using System;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Erzwingt eine feste Reihenfolge der Arbeitsblätter (Tabs):
    ///   1. alle übrigen Blätter (bestehende Reihenfolge bleibt, ganz links)
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

            int pos = 1;
            foreach (var ws in uebrige)       ws.Position = pos++;
            foreach (var ws in stundenplaene) ws.Position = pos++;
            foreach (var ws in nuho)          ws.Position = pos++;
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
