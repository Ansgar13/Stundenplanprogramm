using ClosedXML.Excel;
using System.Collections.Generic;

namespace Stundenplan_V2
{
    public static class UnrPlanExporter
    {
        public static void Erzeuge(
     string excelPfad,
     List<UnterrichtsBlock> blocks,
     List<ZeitSlot> slots,
     int[,] belegung)
        {
            using var wb = new XLWorkbook(excelPfad);

            IXLWorksheet sheet;

            if (wb.Worksheets.Any(x => x.Name == "Unr-Plan"))
                wb.Worksheet("Unr-Plan").Delete();

            sheet = wb.AddWorksheet("Unr-Plan");

            sheet.Cell(1, 1).Value = "WTag";
            sheet.Cell(1, 2).Value = "Stunde";
            sheet.Cell(1, 3).Value = "UNr";

            for (int s = 0; s < slots.Count; s++)
            {
                sheet.Cell(s + 2, 1).Value = slots[s].WTag;
                sheet.Cell(s + 2, 2).Value = slots[s].Stunde;

                var unrList = new List<int>();

                for (int b = 0; b < blocks.Count; b++)
                {
                    if (belegung[b, s] == 1)
                        unrList.Add(blocks[b].UNr);
                }

                int col = 3;

                foreach (var unr in unrList)
                {
                    sheet.Cell(s + 2, col).Value = unr;
                    col++;
                }
            }

            sheet.Columns().AdjustToContents();

            wb.Save();
        }
    }
}