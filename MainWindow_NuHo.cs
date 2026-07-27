// ============================================================================
// EIGENSTAENDIGE DATEI — ins Projekt legen.
//
// Erweitert MainWindow als "partial class" (MainWindow ist bereits partiell:
// public partial class MainWindow : Window in MainWindow.cs). KEINE Aenderung
// an der Klassendeklaration noetig.
//
// Enthaelt die Orchestrierung der NuHo-Ausgaben (Tabelle "NuHo" +
// "NuHoKP_<label>"-Blaetter) und kleine Helfer fuer die Rank-Spalte.
//
// ▶ Aufruf am Ende jeder Planerstellung: siehe INTEGRATION.txt — jeweils EINE
//   Zeile   ErzeugeNuHoAusgaben(<solutions>);   direkt nach SchreibeRanking(...).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    public partial class MainWindow
    {
        // Erzeugt fuer die uebergebenen Loesungen die Extratabelle "NuHo" und je
        // Loesung einen NuHo-Klassenplan "NuHoKP_<label>". Robust gegen einzelne
        // fehlerhafte Loesungen; bricht die Planerstellung nie ab.
        private void ErzeugeNuHoAusgaben(
            List<(int quality, int badUnits, int[,] belegung, string label, List<UnterrichtsBlock> blocks)> solutions)
        {
            if (input == null || solutions == null || solutions.Count == 0) return;

            try
            {
                var ergebnisse = new List<NuHoPlanErgebnis>();

                foreach (var sol in solutions)
                {
                    NuHoPlanErgebnis erg;
                    try
                    {
                        erg = NuHoAnalyse.Berechne(
                            sol.belegung, sol.blocks, input.Slots,
                            input.LehrerStammdaten,
                            input.NuHoSollwertProZeitslot,
                            input.StrafeZuWenigNuHo,
                            sol.label);
                    }
                    catch (Exception exSol)
                    {
                        Log($"NuHo: Loesung '{sol.label}' konnte nicht ausgewertet werden ({exSol.Message}).");
                        continue;
                    }

                    ergebnisse.Add(erg);

                    // Klassenplan je Loesung.
                    try { NuHoAnalyse.ErzeugeNuHoKlassenplan(excelPfad, erg, input.Slots, sol.label); }
                    catch (Exception exKp) { Log($"NuHo-Klassenplan '{sol.label}' fehlgeschlagen: {exKp.Message}"); }
                }

                if (ergebnisse.Count > 0)
                {
                    NuHoAnalyse.ErzeugeNuHoTabelle(excelPfad, ergebnisse);

                    // Bewusst KEINE Quersumme ueber alle Loesungen mehr:
                    // FehlendeGesamt ist bereits die Summe EINER Loesung (ueber
                    // alle Zeitslots). Die frueher hier ausgegebene Summe ueber
                    // alle Loesungen wuchs mit der Anzahl der Loesungen statt mit
                    // deren Qualitaet und wurde im Ausgabefenster als Planfehler
                    // fehlgedeutet. Stattdessen eine Zeile je Loesung — im selben
                    // Format wie die Lösungsausgabe "  [{label}] Qualität: ...".
                    Log($"NuHo-Auswertung: Tabelle 'NuHo' + {ergebnisse.Count} NuHo-Klassenplan/-plaene erzeugt.");
                    foreach (var e in ergebnisse)
                    {
                        // Loesungen ohne Unterschreitung werden bewusst mit
                        // ausgegeben, damit die Liste vollstaendig zur
                        // Lösungsliste passt.
                        string label = string.IsNullOrWhiteSpace(e.Label) ? "?" : e.Label;
                        Log($"  [{label}] fehlende NuHos: {e.FehlendeGesamt}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"NuHo-Ausgabe fehlgeschlagen: {ex.Message}");
            }
        }

        // Fehlende NuHos (= Anzahl der Unterschreitungen) einer einzelnen
        // Loesung — fuer die Rank-Spalte "zu wenig NuHos".
        private int NuHoFehlendeFuerLoesung(int[,] belegung, List<UnterrichtsBlock> blocks)
        {
            if (input == null) return 0;
            try
            {
                var erg = NuHoAnalyse.Berechne(
                    belegung, blocks, input.Slots, input.LehrerStammdaten,
                    input.NuHoSollwertProZeitslot, input.StrafeZuWenigNuHo);
                return erg.FehlendeGesamt;
            }
            catch { return 0; }
        }
    }
}
