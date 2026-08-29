using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Normalisiert die Arbeitsmappe unmittelbar vor dem Speichern:
    ///   1. Feste Prioritäts-Reihenfolge ganz links (siehe <see cref="Prioritaet"/>):
    ///      UV, Verl, Diag, PM, Fix UNrn, StD, ZWL, ZWK, Chkfix, Tausch, Plan.
    ///   2. Danach alle übrigen Blätter (bisherige Reihenfolge bleibt).
    ///   3. NuHo-Blätter (Name == "NuHo" oder beginnt mit "NuHo…", inkl. "NuHoKP_")
    ///      immer ganz ans Ende.
    /// Zusätzlich bleibt der aktive/ausgewählte Reiter erhalten, damit
    /// Zwischenspeicherungen nicht auf ein frisch geschriebenes Blatt springen,
    /// und die Spaltenbreiten werden über <see cref="SpaltenBreiten"/> optimiert.
    /// Wird vor jedem Speichern aufgerufen, damit die Mappe dauerhaft konsistent bleibt.
    /// </summary>
    public static class BlattReihenfolge
    {
        // Reihenfolge = Zielposition. Vergleich normalisiert (Kleinschreibung +
        // Leerzeichen entfernt), damit z.B. "Fix UNrn" == "FixUnrn" und "StD" == "std"
        // sicher erkannt werden. Nur exakte (normalisierte) Namensgleichheit zählt,
        // damit z.B. "Plan" nicht versehentlich "PlanX" trifft.
        private static readonly string[] Prioritaet =
        {
            "uv", "verl", "diag", "pm", "fixunrn", "std",
            "zwl", "zwk", "chkfix", "tausch", "plan"
        };

        private static string Norm(string name) =>
            (name ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

        public static void Anwenden(XLWorkbook wb)
        {
            if (wb == null)
                return;

            // Aktiven Reiter merken (wird nach dem Umsortieren wiederhergestellt).
            var aktiv = wb.Worksheets.FirstOrDefault(ws => ws.TabActive);

            // Aktuelle Reihenfolge als Basis (stabil je Gruppe beibehalten).
            var blaetter = wb.Worksheets.OrderBy(ws => ws.Position).ToList();

            // NuHo zuerst bestimmen: "NuHoKP_" beginnt mit "NuHo" – so landet es
            // korrekt in der NuHo-Gruppe (ganz rechts) und nicht als übriges Blatt.
            var nuho = blaetter.Where(ws => IstNuHo(ws.Name)).ToList();
            var rest = blaetter.Where(ws => !IstNuHo(ws.Name)).ToList();

            // Prioritätsblätter in fester Reihenfolge (nur die tatsächlich vorhandenen).
            var prio = new List<IXLWorksheet>();
            foreach (var token in Prioritaet)
            {
                var ws = rest.FirstOrDefault(w => Norm(w.Name) == token);
                if (ws != null) prio.Add(ws);
            }
            var prioNamen = new HashSet<string>(prio.Select(w => w.Name));
            var uebrige = rest.Where(w => !prioNamen.Contains(w.Name)).ToList();

            int pos = 1;
            foreach (var ws in prio)    ws.Position = pos++;
            foreach (var ws in uebrige) ws.Position = pos++;
            foreach (var ws in nuho)    ws.Position = pos++;

            // Aktiven Reiter wiederherstellen. Das Umsetzen der Position ändert den
            // aktiven Tab zwar nicht, aber Zwischenspeicherungen mit frisch
            // angelegten Blättern können ihn verschieben – daher erneut setzen.
            aktiv?.SetTabActive();

            // Spaltenbreiten optimieren (Datentabellen; Plan-Raster ausgenommen).
            SpaltenBreiten.Optimieren(wb);
        }

        private static bool IstNuHo(string name)
        {
            return name != null &&
                   name.StartsWith("NuHo", StringComparison.OrdinalIgnoreCase);
        }
    }
}
