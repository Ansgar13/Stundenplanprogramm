using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    // =====================================================================
    // ABWEICHUNGSEXPORTER
    // Vergleicht eine neue Lösung (oder mehrere) mit einem Ausgangsplan und
    // schreibt die Unterschiede in das Sheet "Abw" der Excel-Datei.
    //
    // Pro Lösung entsteht ein eigener Block:
    //   - Kopfzeile: Lösungsname fett
    //   - Eine Zeile pro geändertem Block:
    //       UNr | Lehrer | Fach | Klasse(n) | Vorher (Tag/Std) | Nachher (Tag/Std) | Art
    //   - Leerzeile als Trennzeile
    //
    // Änderungsarten (Farbe):
    //   verschoben  – Block lag im Ausgangsplan, liegt jetzt woanders (orange)
    //   entplant    – Block lag im Ausgangsplan, jetzt nirgends platziert (rot)
    //   neu platziert – Block war im Ausgangsplan nicht da, jetzt schon (grün)
    //   unverändert – wird nicht aufgelistet
    // =====================================================================
    public static class AbweichungsExporter
    {
        private static readonly XLColor FarbeKopf        = XLColor.FromArgb(0x1F, 0x38, 0x64);
        private static readonly XLColor FarbeVerschoben  = XLColor.FromArgb(0xFF, 0xC0, 0x00);
        private static readonly XLColor FarbeEntplant    = XLColor.FromArgb(0xFF, 0x63, 0x47);
        private static readonly XLColor FarbeNeu         = XLColor.FromArgb(0x70, 0xAD, 0x47);

        public static void Exportiere(
            string excelPfad,
            string ausgangsLabel,
            int[,] ausgangsplanBelegung,
            List<(string label, int[,] belegung, List<UnterrichtsBlock> blocks)> neueLösungen,
            List<ZeitSlot> slots,
            bool vorherLöschen = true)
        {
            using var wb = new XLWorkbook(excelPfad);

            if (wb.Worksheets.Any(ws => ws.Name == "Abw"))
            {
                if (vorherLöschen)
                    wb.Worksheet("Abw").Delete();
                else
                {
                    // Anhängen: letzte benutzte Zeile suchen
                }
            }

            var sheet = wb.Worksheets.Add("Abw");

            // Spaltenbreiten
            sheet.Column(1).Width = 8;   // UNr
            sheet.Column(2).Width = 10;  // Lehrer
            sheet.Column(3).Width = 10;  // Fach
            sheet.Column(4).Width = 18;  // Klasse(n)
            sheet.Column(5).Width = 16;  // Vorher
            sheet.Column(6).Width = 16;  // Nachher
            sheet.Column(7).Width = 16;  // Art

            int zeile = 1;

            foreach (var lösung in neueLösungen)
            {
                int B = lösung.blocks.Count;
                int S = slots.Count;
                int bAlt = ausgangsplanBelegung.GetLength(0);
                int sAlt = ausgangsplanBelegung.GetLength(1);

                // Kopfzeile
                var kopf = sheet.Cell(zeile, 1);
                kopf.Value = $"Vergleich: {ausgangsLabel} → {lösung.label}";
                kopf.Style.Font.Bold = true;
                kopf.Style.Font.FontColor = XLColor.White;
                kopf.Style.Fill.BackgroundColor = FarbeKopf;
                sheet.Range(zeile, 1, zeile, 7).Merge();
                zeile++;

                // Spaltenköpfe
                var header = new[] { "UNr", "Lehrer", "Fach", "Klasse(n)", "Vorher (Tag/Std)", "Nachher (Tag/Std)", "Art" };
                for (int c = 0; c < header.Length; c++)
                {
                    var zelle = sheet.Cell(zeile, c + 1);
                    zelle.Value = header[c];
                    zelle.Style.Font.Bold = true;
                    zelle.Style.Fill.BackgroundColor = XLColor.LightGray;
                }
                zeile++;

                int änderungen = 0;

                for (int b = 0; b < B; b++)
                {
                    var block = lösung.blocks[b];
                    string lehrer = string.Join(", ", block.Teile.Select(t => t.Lehrer).Distinct());
                    string fach   = string.Join(", ", block.Teile.Select(t => t.Fach).Distinct());
                    string klasse = string.Join(", ", block.Teile.SelectMany(t => t.Klassen).Distinct());

                    // Slots im Ausgangsplan für diesen Block
                    var vorherSlots = new List<int>();
                    if (b < bAlt)
                        for (int s = 0; s < S && s < sAlt; s++)
                            if (ausgangsplanBelegung[b, s] == 1)
                                vorherSlots.Add(s);

                    // Slots in der neuen Lösung
                    var nachherSlots = new List<int>();
                    for (int s = 0; s < S; s++)
                        if (lösung.belegung[b, s] == 1)
                            nachherSlots.Add(s);

                    // Unveränderte Slots herausrechnen
                    var gleicheSlots  = vorherSlots.Intersect(nachherSlots).ToHashSet();
                    var nurVorher     = vorherSlots.Where(s => !gleicheSlots.Contains(s)).ToList();
                    var nurNachher    = nachherSlots.Where(s => !gleicheSlots.Contains(s)).ToList();

                    if (nurVorher.Count == 0 && nurNachher.Count == 0) continue;

                    string FormatSlots(List<int> sl) =>
                        sl.Count == 0
                            ? "–"
                            : string.Join(", ", sl.Select(s => $"{slots[s].WTag}/Std.{slots[s].Stunde}"));

                    string art;
                    XLColor farbe;
                    if (nurVorher.Count > 0 && nurNachher.Count > 0)
                    {
                        art = "verschoben";
                        farbe = FarbeVerschoben;
                    }
                    else if (nurVorher.Count > 0 && nurNachher.Count == 0)
                    {
                        art = "entplant";
                        farbe = FarbeEntplant;
                    }
                    else
                    {
                        art = "neu platziert";
                        farbe = FarbeNeu;
                    }

                    sheet.Cell(zeile, 1).Value = block.UNr;
                    sheet.Cell(zeile, 2).Value = lehrer;
                    sheet.Cell(zeile, 3).Value = fach;
                    sheet.Cell(zeile, 4).Value = klasse;
                    sheet.Cell(zeile, 5).Value = FormatSlots(nurVorher);
                    sheet.Cell(zeile, 6).Value = FormatSlots(nurNachher);
                    sheet.Cell(zeile, 7).Value = art;

                    for (int c = 1; c <= 7; c++)
                        sheet.Cell(zeile, c).Style.Fill.BackgroundColor = farbe;

                    zeile++;
                    änderungen++;
                }

                if (änderungen == 0)
                {
                    sheet.Cell(zeile, 1).Value = "(keine Abweichungen – identisch mit Ausgangsplan)";
                    sheet.Cell(zeile, 1).Style.Font.Italic = true;
                    zeile++;
                }

                // Zusammenfassung
                sheet.Cell(zeile, 1).Value = $"Gesamt: {änderungen} geänderte Blöcke";
                sheet.Cell(zeile, 1).Style.Font.Bold = true;
                zeile++;

                // Leerzeile
                zeile++;
            }

            wb.Save();
        }
    }
}
