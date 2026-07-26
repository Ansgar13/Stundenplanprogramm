# Stundenplan V4

Windows-Programm zur automatischen Erstellung von Schulstundenplänen mit dem Google-OR-Tools-Solver (CP-SAT). 

## Überblick

Stundenplan liest alle Unterrichts- und Lehrerstammdaten aus einer einzigen Excel-Datei (`.xlsx`), berechnet mit Hilfe des Google-OR-Tools-Solvers optimale Stundenpläne unter Berücksichtigung diverser Constraints (Lehrer-/Klassenkonflikte, Zeitwünsche, Doppelstunden, Fachraum-Limits, Tauschgruppen, A-/B-Wochen u. v. m.) und schreibt die Ergebnisse direkt in dieselbe Excel-Datei zurück.

Die Unterrichts- und Lehrerstammdaten können z. B. aus einer Untis-Datei per Copy & Paste übertragen werden. 

> **Besonderer Vorteil für Sek-II-Pläne:** Über ein Lehrertauschkennzeichen (LTKZ) lassen sich gekennzeichnete, gegenseitig austauschbare Lehrer definieren. Der Solver kann diese Lehrer bei der Planberechnung automatisch untereinander tauschen, um bessere Lösungen zu finden. Das ist gerade bei Kurssystemen der Sekundarstufe II (parallele Kurse mit mehreren gleichwertigen Fachlehrern) ein entscheidender Vorteil, da hier die Zuordnung Lehrer↔Kurs oft flexibel ist und so deutlich bessere bzw. überhaupt erst zulässige Pläne entstehen.

### Kernfunktionen

- Vollautomatische Planerstellung unter Berücksichtigung von Lehrer- und Klassenkonflikten
- frei definierbare Qualitätskriterien entweder mit Gewichtung oder als harte/strenge Vorgaben. So lässt sich schnell ermitteln, ob Lösungen überhaupt existieren
- Zeitwünsche und Sperrzeiten für Lehrer und Klassen
- Doppelstunden-Vorgaben, Fachraum-Limits, **Tauschgruppen (LTKZ) – besonders wertvoll für Sek-II-Pläne**, A-/B-Wochen
- frei definierbare Qualitaätsoptimierung bspw. zur Vermeidung von nur späten Unterrichten, Dreifach-, Doppel und Einfachhohlstunden -alles auch als zwingende Bedingungen (hard constraints) definierbar
- Iterative Verbesserung bestehender Pläne
- unmittelbares Vergleichen zweier Stundenpläne nach bestimmten Diagnosekriterien in der Lehrer und Klassenansicht möglich
- Export von Lehrer- und Klassenplänen als Excel-Sheets
- Import des erzeugten Stundenplans in eine Untisdatei ist mit ein paar Klicks möglich, auch ohne offiziell vorgesehene Importfunktion
- Constraint-Prüfung mit farbcodiertem Verletzungs-Report
- Automatische Sequenzdiagnose bei Infeasibility (identifiziert den schuldigen Constraint-Block)
- Manueller Plan-Editor mit Drag & Drop, Tauschvorschlägen auch über zwei Klassen hinweg

Eine ausführliche Bedienungsanleitung befindet sich in `Anleitung V120.docx`.

## Technologie-Stack

- **.NET 10 / WPF** (`net10.0-windows`, `UseWPF=true`)
- **Google.OrTools** – CP-SAT-Solver für die Optimierung
- **ClosedXML** – Lesen/Schreiben der `.xlsx`-Eingabedatei

## Projektstruktur

```
├── App.xaml / App_xaml.cs                     Anwendungseinstieg
├── MainWindow.xaml / MainWindow_xaml.cs        Hauptfenster (Buttons, Log, Workflow-Steuerung)
│
├── Eingabe & Datenmodell
│   ├── ExcelLoader.cs                          Einlesen der xlsx (UV, Lös, PM, StD, FT, FGR, Fix UNrn, Plan)
│   ├── StundenplanInput.cs                     Eingabedaten-Container (Blöcke, Slots, Parameter)
│   ├── StundenplanModel.cs                     Domänenmodell (UnterrichtsBlock, Teile, …)
│   ├── LehrerStammdaten.cs                     Individuelle Lehrer-Einstellungen (Sheet StD)
│   └── ZeitSlot.cs                             Zeitraster (Tag/Stunde)
│
├── Solver & Constraints
│   ├── StundenplanEngine.cs                    Kernlogik: Modellaufbau, Lösungssuche, Sequenzdiagnose
│   ├── OrToolsSolver.cs                        CP-SAT-Solver-Wrapper
│   ├── StundenplanService.cs                   Orchestrierung Engine ↔ UI
│   ├── ObjectiveBuilder.cs                     Aufbau der Zielfunktion (Solver-Ziel)
│   ├── PlanBewertung.cs                        Berechnung der angezeigten Qualität (Rank-Kennzahl)
│   ├── TimeConstraint.cs                       Zeitwunsch-/Sperrzeiten-Constraints
│   ├── RoomConstraint.cs                       Fachraum-Constraints
│   ├── ClassConstraint.cs                      Klassenkonflikt-Constraints
│   └── FreeDayConstraint.cs                    Freie-Tage-Constraints
│
├── Verbesserung & Validierung
│   ├── PlanVerbesserung.cs                     Iterative Verbesserung bestehender Pläne
│   ├── PlanValidator.cs                        Constraint-Prüfung / Verletzungs-Report
│   ├── LehrerDiagnose.cs                       Lehrer-Diagnose-Übersicht
│   ├── SolutionCollector.cs                    Sammlung/Ranking berechneter Lösungen
│   └── SolverFortschritt.cs                    Fortschrittsanzeige während des Solver-Laufs
│
├── Export & Generatoren
│   ├── KlassenplanGenerator.cs                 Klassenpläne erzeugen
│   ├── LehrerplanGenerator.cs                  Lehrerpläne erzeugen
│   ├── UnrPlanExporter.cs                      Export des manuellen Unterrichtsplans
│   ├── ZeitwunschExporter.cs                   Erzeugt ZeitWL/ZeitWK aus Textdatei
│   └── AbweichungsExporter.cs                  Export von Plan-Abweichungen
│
├── Dialoge (XAML + Code-Behind)
│   ├── KlasseFixierenDialog(.xaml/.cs)         Gezieltes Fixieren nach Klasse/Fach
│   ├── FixierenDialog(.xaml/.cs)               Allgemeines Fixieren von Zeitslots
│   ├── PlanEditorDialog(.xaml/.cs)             Manueller Plan-Editor (Drag & Drop)
│   ├── MinimalAenderungDialog(.xaml/.cs)       Minimal-Änderungs-Suche
│   ├── VerbesserungsDialog(.xaml/.cs)          Automatische Plan-Verbesserung
│   ├── IgnoreDialog(.xaml/.cs)                 Ignorieren einzelner Unterrichtseinheiten
│   ├── DiagFilterDialog(.xaml/.cs)             Filter für die Diagnose-Ansicht
│   └── DiagAnzeigeWindow.cs                    Anzeige der Diagnose-Ergebnisse
│
├── SucheStatusFenster.cs                       Statusfenster während der Solver-Suche
│
├── Stundenplan_V4.csproj                       Projektdatei (.NET 10, WPF)
├── AssemblyInfo.cs                             Assembly-Metadaten
├── Stundenplan_V70_Anleitung.docx               Ausführliches Benutzerhandbuch
└── Teststdplan_ohne_Makros.xlsx                Beispiel-/Test-Eingabedatei
```

## Die Excel-Eingabedatei

Die gesamte Konfiguration erfolgt in einer einzigen `.xlsx`-Datei mit u. a. folgenden Tabellenblättern:

| Kurzname | Inhalt |
| --- | --- |
| UV | Zentrale Unterrichts- und Lehrerzuordnung (Pflicht) |
| Lös | Zeitraster und berechnete Lösungsspalten (Pflicht) |
| PM | Steuerungsparameter für den Solver (Pflicht) |
| StD | Individuelle Lehrer-Einstellungen (optional) |
| ZWL / ZWK | Zeitwünsche für Lehrer / Klassen |
| FT | Freie Tage der Lehrer |
| FGR | Fachraum-Limits nach Fachgruppe |
| Fix UNrn | Fixierte Zeitslots |
| Plan | Manueller Ausgangsplan |
| Rank | Ranking aller berechneten Lösungen (Ausgabe) |
| Verl | Constraint-Verletzungs-Report (Ausgabe) |
| Diag | Lehrer-Diagnose-Übersicht (Ausgabe) |

Details zu allen Spalten und Parametern: siehe `Stundenplan_V70_Anleitung.docx`.

## Empfohlener Arbeitsablauf

1. Excel-Datei vorbereiten (UV, Lös, PM, ggf. StD, FT).
2. Programm starten, Excel-Datei laden.
3. Optional: Zeitwünsche als `.txt` einlesen.
4. Solver starten (Zeitlimit/Lösungsanzahl vorher in PM einstellen).
5. Beste Lösung im Sheet `Rank` identifizieren.
6. Klassen- und Lehrerpläne erzeugen und prüfen.
7. Bei Bedarf gute Bereiche fixieren und Solver erneut starten.
8. Constraint-Prüfung durchführen.
9. Verbleibende Schwachstellen automatisch verbessern oder im Plan-Editor manuell bearbeiten.

## Build & Ausführung

Voraussetzungen: **.NET 10 SDK** (Windows, da WPF).

```bash
dotnet restore
dotnet build
dotnet run --project Stundenplan_V4.csproj
```

## Status

`erst auf Aufforderung zum coden warten` – es werden aktuell keine Code-Änderungen vorgenommen; diese README dient als Ausgangsbasis/Referenz für kommende Aufgaben.
