# Stundenplan V43 – Automatischer Stundenplangenerator

Stundenplan V43 ist ein Windows-Programm zur automatischen Erstellung von Schulstundenplänen. Es liest alle Unterrichtsdaten aus einer Excel-Datei, berechnet mit dem Google-OR-Tools-Solver optimale Stundenpläne und schreibt die Ergebnisse direkt in dieselbe Excel-Datei zurück.

## Kernfunktionen

- Vollautomatische Planerstellung unter Berücksichtigung von Lehrer- und Klassenkonflikten
- Zeitwünsche und Sperrzeiten für Lehrer und Klassen (−3 bis +3)
- Doppelstunden, Fachraum-Limits, Tauschgruppen, A-/B-Wochen
- Separate Tabelle für zusätzliche freie Lehrertage (Sheet `FT`)
- Zweiter Beschriftungstext (`ZeilenText-2`) mit automatischer Kursfarbe (LK01/LK02)
- Iterative Verbesserung bestehender Pläne
- Export von Lehrer- und Klassenplänen als Excel-Sheets
- Constraint-Prüfung mit farbcodiertem Verletzungs-Report (Sheet `Verl`)
- Automatische Checkup-Prüfung fixierter Unterrichte vor jedem Solverlauf (Sheet `ChkFix`)
- Manueller Plan-Editor mit Drag & Drop, Tauschvorschlägen und Verschiebung-mit-Ausweich

## Voraussetzungen

- Windows 10 oder höher
- Microsoft Excel
- Eingabedatei ausschließlich als `.xlsx` (kein `.xlsm`)

## Die Excel-Eingabedatei

Die gesamte Konfiguration erfolgt in einer einzigen `.xlsx`-Datei. Ab V18 verwenden alle internen Sheets kurze Namen:

| Kurzname | Inhalt |
|---|---|
| `UV` | Zentrale Unterrichts- und Lehrerzuordnung (Pflicht) |
| `Lös` | Zeitraster und berechnete Lösungsspalten (Pflicht) |
| `PM` | Steuerungsparameter für den Solver (Pflicht) |
| `StD` | Individuelle Lehrer-Einstellungen (optional) |
| `ZWL` / `ZWK` | Zeitwünsche für Lehrer / Klassen (von Button 1 erzeugt) |
| `FT` | Freie Tage der Lehrer (optional) |
| `FGR` | Fachraum-Limits nach Fachgruppe (optional) |
| `Fix UNrn` | Fixierte Zeitslots |
| `Plan` | Manueller Ausgangsplan (optional) |
| `Rang` | Ranking aller berechneten Lösungen (Ausgabe) |
| `Verl` | Constraint-Verletzungs-Report (Ausgabe) |
| `Diag` | Lehrer-Diagnose-Übersicht (Ausgabe) |
| `ChkFix` | Checkup der fixierten Unterrichte vor dem Solverlauf (Ausgabe) |
| `Dstd-F` | Detailliste der Doppelstunden-Verletzungen (Ausgabe) |
| `Tausch` | Liste der Lehrertausch-Paare (Ausgabe) |
| `LKue` | Zuordnung Originalname ↔ Anonymkürzel |

Das wichtigste Sheet ist `UV`: Jede Zeile beschreibt einen Unterrichtseinsatz (U-Nr, Wochenstunden, Lehrer, Fach, Klasse, optional Doppelstunden-Vorgabe, Beschriftungstexte, Tauschkennzeichen, Ignore-Flag, Klassen-Konflikt-Kennzeichen, A-/B-Woche).

## Zeitwunsch-Textdatei

Zeitwünsche werden über eine einfache `.txt`-Datei eingelesen (Semikolon-getrennt):

```
Typ;Name;Tag;Stunde;Wert
L;WIN;3;1;-3    (Lehrer WIN: Mittwoch 1. Stunde gesperrt)
K;5a;5;7;-2     (Klasse 5a: Freitag 7. Stunde stark unerwünscht)
```

Werte reichen von −3 (absolute Sperre) bis +3 (starker Wunsch); aktuell sind nur 0, −2 und −3 implementiert.

## Programmbedienung

Das Hauptfenster zeigt zehn Schaltflächen in empfohlener Reihenfolge:

| Button | Funktion |
|---|---|
| 1 | Zeitwünsche aus Textdatei einlesen |
| 2 | Excel-Datei einlesen (Voraussetzung für alle weiteren Schritte) |
| 3 | Stundenplanerstellung (Solver starten, inkl. Infeasibility-Diagnose) |
| 4 | Lehrerpläne erzeugen |
| 5 / 5b | Klassenpläne erzeugen (5b nur für EF/Q1/Q2) |
| 6 | UNr-Plan bewerten |
| 7 | Klasse fixieren → Fix UNrn |
| 8 | UNr-Plan auf Verletzungen prüfen |
| 9 | Plan verbessern (automatische Tauschoperationen) |
| 10 | Fix UNrn selektiv löschen |

### Manueller Plan-Editor

Eigenständiges Fenster zur grafischen Bearbeitung einer berechneten Lösung per Drag & Drop: Verschieben auf freie Slots, Tauschen mit belegten Zellen, Entplanen in einen Parkbereich, automatische Tauschvorschläge sowie "Verschiebung mit Ausweich" bei blockierten Zielslots. Ein Diagnose-Panel zeigt für betroffene Lehrer die Veränderung von freien Tagen, Hohlstunden und Gesamtqualität vorher/nachher.

## Empfohlener Arbeitsablauf

1. Excel-Datei vorbereiten (`UV`, `Lös`, `PM`, ggf. `StD`, `FT`)
2. Programm starten, Button 2 (Excel laden)
3. Optional: Zeitwünsche per Button 1 einlesen
4. Button 3 (Solver starten)
5. Beste Lösung im Sheet `Rang` identifizieren
6. Button 4/5 für Lehrer- und Klassenpläne
7. Gute Klassen mit Button 7 fixieren, Solver erneut starten
8. Constraint-Prüfung mit Button 8
9. Bei Bedarf mit Button 9 oder dem Plan-Editor nachbessern
10. Fixierungen bei Bedarf mit Button 10 zurücksetzen und Prozess wiederholen

## Die Excel-Makrodatei (.xlsm)

Parallel zur `.xlsx`-Datei existiert eine `.xlsm`-Datei mit VBA-Makros für komfortables manuelles Arbeiten direkt in Excel. Das Stundenplan-Programm selbst liest ausschließlich `.xlsx`-Dateien ein.

| Makro | Funktion |
|---|---|
| `LehrerDurchKuerzelErsetzen` | Ersetzt Lehrernamen durch anonyme Kürzel |
| `KuerzelZurueckErsetzen` | Stellt Originalnamen wieder her |
| `TabellenImportieren` | Importiert Sheets aus einer beliebigen Excel-Datei |
| `TabellenExportieren` | Exportiert alle Sheets als makrofreie `.xlsx` |
| `LösungInFixUNrnÜbertragen` | Überträgt eine komplette Lösung in `Fix UNrn` |

## Fehlerbehebung

| Meldung | Lösung |
|---|---|
| „Bitte zuerst Excel-Datei laden (Button 2)“ | Button 2 ausführen |
| „Keine Lösungen verfügbar“ | Button 3 (Solver) ausführen |
| „Kein UNr-Plan gefunden“ | Sheet `Plan` befüllen oder Button 3 starten |
| „Tabelle 'Fix UNrn' nicht gefunden“ | Sheet manuell anlegen oder aus Vorlage kopieren |
| „Spalte 'X' nicht gefunden“ | Pflichtspalte in `UV` prüfen |

Weitere Hinweise: Die Excel-Datei darf während des Programmbetriebs nicht in Excel geöffnet sein. Lehrer- und Klassenkürzel müssen in allen Sheets exakt übereinstimmen.

## Glossar (Auswahl)

- **UNr** – Unterrichtsnummer, eindeutige ID für einen Unterrichtsblock
- **Block** – Gruppe von Zeilen mit gleicher UNr
- **Slot** – konkreter Zeitpunkt im Stundenplan (Wochentag + Stunde)
- **Hohlstunde** – Lücke im Lehrerplan
- **Fix UNrn** – als unveränderlich vorgegebene Zeitslots
- **Qualität** – Strafpunkte-Summe des Solvers (kleiner = besser)

## Vollständige Dokumentation

Eine ausführliche Beschreibung aller Sheets, Parameter und Funktionen befindet sich in `Stundenplan_V44_Anleitung.docx`.

## Lizenz

Noch keine Lizenz festgelegt.
