using System;
using System.Linq;
using ClosedXML.Excel;

namespace Stundenplan_V2
{
    /// <summary>
    /// Optimiert die Spaltenbreiten einer Arbeitsmappe vor dem Speichern:
    /// passt die tatsächlich genutzten Spalten an ihren Inhalt an und deckelt sie
    /// bei <paramref name="maxBreite"/>, damit keine übermäßig breiten Spalten
    /// entstehen (häufige Ursache: langer Zellinhalt oder ein global gesetztes
    /// ColumnWidth). Fehlertolerant je Blatt.
    ///
    /// Ausgenommen sind die Plan-Raster (Name beginnt mit "LP_", "KP_" oder
    /// "NuHo…"): sie haben bewusst gesetzte, gleichmäßige Rasterbreiten, die
    /// nicht am Inhalt ausgerichtet werden sollen.
    /// </summary>
    public static class SpaltenBreiten
    {
        public static void Optimieren(XLWorkbook wb, double maxBreite = 45)
        {
            if (wb == null)
                return;

            foreach (var ws in wb.Worksheets)
            {
                try
                {
                    if (IstRaster(ws.Name)) continue;      // Plan-Raster unangetastet
                    if (ws.RangeUsed() == null) continue;  // leeres Blatt überspringen

                    ws.ColumnsUsed().AdjustToContents();

                    // Übertriebene Breiten deckeln (z.B. lange Detail-/Wrap-Texte).
                    // Spalten nach dem Anpassen frisch abfragen.
                    foreach (var col in ws.ColumnsUsed())
                        if (col.Width > maxBreite)
                            col.Width = maxBreite;
                }
                catch
                {
                    // Fehlertolerant: einzelnes Blatt überspringen, Rest weiter.
                }
            }
        }

        private static bool IstRaster(string name) =>
            name != null &&
            (name.StartsWith("LP_",  StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("KP_",  StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("NuHo", StringComparison.OrdinalIgnoreCase));
    }
}
