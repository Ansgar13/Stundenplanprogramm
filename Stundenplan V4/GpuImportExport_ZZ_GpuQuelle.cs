// ============================================================================
// EIGENSTÄNDIGE DATEI — einfach so ins Projekt legen (z. B. neben
// GpuImportExport.cs). Sie erweitert die Klasse GpuImportExport als
// "partial class".
//
// ▶ EINZIGE nötige Änderung an der bestehenden GpuImportExport.cs:
//   die Klassendeklaration von
//       public static class GpuImportExport
//   auf
//       public static partial class GpuImportExport
//   ändern (nur das Wort "partial" ergänzen).
//
// Danach kompiliert dieser Teil eigenständig; ZZLehrerName(...) liegt in der
// anderen partiellen Hälfte derselben Klasse und ist hier verfügbar.
// ============================================================================

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Stundenplan_V2
{
    public static partial class GpuImportExport
    {
        // ================================================================
        // GPU-QUELLE-MODUS (ZZ-Trick direkt aus einer bestehenden GPU002.TXT)
        //
        // Anders als der UV-basierte Export baut dieser Modus KEINE Zeilen aus
        // den 46 UV-Feldern neu zusammen. Er schreibt die gewählte GPU002-
        // Quelldatei ZEILENWEISE BYTE-IDENTISCH in die Zieldatei (inkl. BOM,
        // Zeilenenden und Zeichensatz der Quelle) und ergänzt zu jeder Zeile,
        // deren U-Nr in der gewählten Lösung verplant ist, eine zusätzliche
        // Zeile mit dem Dummy-Lehrer "ZZ<UNr>" statt des Originallehrers
        // (Feld 6) und Wochenstd. Kla./Le. (Felder 3+4) auf 0. Alle übrigen
        // Felder behalten ihre Original-Bytes EXAKT — dadurch entstehen beim
        // Reimport nach Untis keine ungewollten Mini-Änderungen an der UV.
        //
        // Es findet also bewusst KEIN Round-Trip über Dekodieren/Neukodieren
        // statt: die Original- wie auch die nicht getauschten Felder der
        // ZZ-Zeile werden byteweise kopiert. Nur die drei getauschten Felder
        // bestehen aus reinem ASCII (Ziffer '0' bzw. "ZZ<UNr>") und sind daher
        // in UTF-8 wie in Windows-1252 identisch.
        //
        // verplanteUNrn: U-Nrn mit mindestens einem belegten Slot in der
        //   gewählten Lösung. Nur für diese wird eine ZZ-Zeile erzeugt; Zeilen
        //   nicht verplanter U-Nrn werden trotzdem 1:1 durchgeschrieben.
        //   null = keine Einschränkung (dann bekäme jede Zeile eine ZZ-Zeile).
        // exportierteUNrn: distinkte U-Nrn, für die eine ZZ-Zeile geschrieben
        //   wurde — Grundlage für die begleitende GPU016_ZZ.
        // Rückgabe: Gesamtzahl geschriebener Zeilen (Durchschrieb + ZZ).
        // ================================================================
        public static int ErzeugeGpu002AusGpuDatei(
            string gpuQuellPfad, string zielPfad,
            ISet<int> verplanteUNrn,
            out List<string> hinweise,
            out List<int> exportierteUNrn)
        {
            hinweise = new List<string>();
            var zzUNrn = new HashSet<int>();
            int durchgeschrieben = 0;
            int zzZeilen = 0;
            int zuKurzUebersprungen = 0;

            byte[] q = File.ReadAllBytes(gpuQuellPfad);
            var aus = new List<byte>(q.Length * 2);

            // Prägenden Zeilenumbruch der Datei bestimmen (nur für den
            // Sonderfall, dass die letzte Zeile ohne Umbruch endet und trotzdem
            // eine ZZ-Zeile bekommt — dann braucht die ZZ-Zeile davor/danach
            // einen Umbruch, damit sie nicht an der Originalzeile klebt).
            byte[] eolDefault = { (byte)'\n' };
            for (int p = 0; p + 1 < q.Length; p++)
            {
                if (q[p] == (byte)'\r' && q[p + 1] == (byte)'\n') { eolDefault = new[] { (byte)'\r', (byte)'\n' }; break; }
                if (q[p] == (byte)'\n') break;
            }

            int i = 0;
            // Führendes UTF-8-BOM einmal unverändert übernehmen und beim
            // Zeilen-Parsen überspringen (sonst würde es das erste U-Nr-Feld
            // verfälschen und die erste Zeile bekäme keine ZZ-Zeile).
            if (q.Length >= 3 && q[0] == 0xEF && q[1] == 0xBB && q[2] == 0xBF)
            {
                aus.Add(0xEF); aus.Add(0xBB); aus.Add(0xBF);
                i = 3;
            }

            while (i < q.Length)
            {
                int zeilStart = i;
                while (i < q.Length && q[i] != (byte)'\n') i++;
                int eolEnde = (i < q.Length) ? i + 1 : i;   // schließt '\n' mit ein
                int inhaltEnde = i;                          // vor dem '\n'
                if (inhaltEnde > zeilStart && q[inhaltEnde - 1] == (byte)'\r')
                    inhaltEnde--;                            // '\r' gehört zum EOL
                i = eolEnde;

                // 1) Originalzeile IMMER byte-identisch durchschreiben (Inhalt + EOL).
                for (int p = zeilStart; p < eolEnde; p++) aus.Add(q[p]);
                durchgeschrieben++;

                // Leere / reine Whitespace-Zeile: keine ZZ-Zeile.
                bool leer = true;
                for (int p = zeilStart; p < inhaltEnde; p++)
                    if (q[p] != (byte)' ' && q[p] != (byte)'\t') { leer = false; break; }
                if (leer) continue;

                // 2) U-Nr aus Feld 1 lesen; ungültig/nicht verplant -> keine ZZ-Zeile.
                int unr = LiesUNrAusBytes(q, zeilStart, inhaltEnde);
                if (unr <= 0) continue;
                if (verplanteUNrn != null && !verplanteUNrn.Contains(unr)) continue;

                // 3) ZZ-Variante der Zeile bauen (Felder 3,4 -> "0"; Feld 6 -> "ZZ<UNr>").
                byte[] zzInhalt = BaueZzZeileAusBytes(q, zeilStart, inhaltEnde, unr);
                if (zzInhalt == null) { zuKurzUebersprungen++; continue; } // < 6 Felder

                // Hatte die Originalzeile kein EOL (Dateiende ohne Umbruch),
                // erst einen Umbruch setzen, damit die ZZ-Zeile eigenständig ist.
                if (eolEnde == inhaltEnde) aus.AddRange(eolDefault);

                aus.AddRange(zzInhalt);

                // ZZ-Zeile mit dem EOL der Originalzeile abschließen (bzw. mit
                // dem prägenden EOL, falls die Originalzeile keins hatte).
                if (eolEnde > inhaltEnde)
                    for (int p = inhaltEnde; p < eolEnde; p++) aus.Add(q[p]);
                else
                    aus.AddRange(eolDefault);

                zzZeilen++;
                zzUNrn.Add(unr);
            }

            File.WriteAllBytes(zielPfad, aus.ToArray());

            exportierteUNrn = zzUNrn.OrderBy(u => u).ToList();
            hinweise.Add(
                $"GPU-Quelle '{Path.GetFileName(gpuQuellPfad)}' byte-identisch durchgeschrieben: " +
                $"{durchgeschrieben} Zeile(n). ZZ-Lehrer-Zeilen ergänzt: {zzZeilen} " +
                $"(für {zzUNrn.Count} verplante U-Nr(n)).");
            if (zuKurzUebersprungen > 0)
                hinweise.Add($"{zuKurzUebersprungen} Zeile(n) mit weniger als 6 Feldern übersprungen (keine ZZ-Zeile).");

            return durchgeschrieben + zzZeilen;
        }

        // Liest die U-Nr aus dem ersten Feld (Bytes bis zum ersten ';') einer
        // GPU002-Zeile — encoding-unabhängig, da Ziffern, Anführungszeichen und
        // Semikolon in UTF-8 wie in Windows-1252 dieselben Einzelbytes sind.
        // Liefert 0, wenn das Feld keine (ggf. in "..." gefasste) reine Zahl ist.
        private static int LiesUNrAusBytes(byte[] buf, int start, int end)
        {
            int feldEnde = start;
            while (feldEnde < end && buf[feldEnde] != (byte)';') feldEnde++;

            int val = 0; bool ziffer = false;
            for (int p = start; p < feldEnde; p++)
            {
                byte c = buf[p];
                if (c >= (byte)'0' && c <= (byte)'9') { val = val * 10 + (c - (byte)'0'); ziffer = true; }
                else if (c == (byte)'"' || c == (byte)' ' || c == (byte)'\t') continue;
                else return 0;   // unerwartetes Zeichen -> keine gültige U-Nr
            }
            return ziffer ? val : 0;
        }

        // Baut die ZZ-Variante einer GPU002-Zeile auf Byte-Ebene: alle Felder
        // bleiben byte-identisch, nur Feld 3+4 (Index 2/3, Wochenstd. Kla./Le.)
        // werden auf "0" und Feld 6 (Index 5, Lehrer) auf "ZZ<UNr>" gesetzt.
        // Der ZZ-Name ist reines ASCII, daher zeichensatz-neutral. Ein evtl.
        // vorhandenes abschließendes ';' (leeres 47. Feld) bleibt erhalten.
        // Liefert null, wenn die Zeile weniger als 6 Felder hat (dann gibt es
        // kein Lehrerfeld -> keine sinnvolle ZZ-Zeile).
        private static byte[] BaueZzZeileAusBytes(byte[] buf, int start, int end, int unr)
        {
            var grenzen = new List<(int von, int bis)>();
            int von = start;
            for (int p = start; p < end; p++)
                if (buf[p] == (byte)';') { grenzen.Add((von, p)); von = p + 1; }
            grenzen.Add((von, end)); // letztes Feld nach dem letzten ';'

            if (grenzen.Count < 6) return null;

            byte[] zz = Encoding.ASCII.GetBytes("\"" + ZZLehrerName(unr) + "\"");
            byte[] nullByte = { (byte)'0' };

            var aus = new List<byte>(end - start + 16);
            for (int idx = 0; idx < grenzen.Count; idx++)
            {
                if (idx > 0) aus.Add((byte)';');
                if (idx == 2 || idx == 3) aus.AddRange(nullByte);
                else if (idx == 5) aus.AddRange(zz);
                else for (int p = grenzen[idx].von; p < grenzen[idx].bis; p++) aus.Add(buf[p]);
            }
            return aus.ToArray();
        }
    }
}
