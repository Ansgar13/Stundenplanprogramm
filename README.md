# Stundenplan V4

Ein Windows-Desktop-Programm (WPF, .NET 10) zur automatischen Erstellung von
Schul-Stundenplänen. Die Unterrichtsverteilung wird aus einer Excel-Arbeitsmappe
gelesen, mit dem CP-SAT-Solver von **Google OR-Tools** verplant und wahlweise
wieder nach **Untis** zurückgespielt.

- **Framework:** .NET 10 (`net10.0-windows`), WPF
- **Namespace:** `Stundenplan_V2`
- **Abhängigkeiten:** `Google.OrTools` 9.15.6755, `ClosedXML` 0.105.0
- **Datenbasis:** eine Excel-Datei mit u. a. den Blättern `UV` (Unterrichtsverteilung),
  `StD` (Lehrer-Stammdaten), `PM` (Parameter), `FT` (freie Tage/Stunden),
  `ZWL`/`ZWK` (Zeitwünsche Lehrer/Klassen).

Der Ablauf wird über das Hauptfenster in nummerierten Schritten geführt:
Excel einlesen → Zeitwünsche → (optional) UV aus Untis importieren → fixieren/
ignorieren → PM-Parameter → **Stundenplanerstellung** → Plan-Editor → Lösung
sichern → verbessern → nach Untis exportieren → Klassen-/Lehrerpläne erzeugen.

---

## Schneller Algorithmus: Google OR-Tools (CP-SAT)

Kern der Planung ist der **CP-SAT-Solver aus Google OR-Tools**. Die Belegung wird
als Constraint-Programming-Modell formuliert: Für jeden Unterrichtsblock `b` und
jeden Zeitslot `s` gibt es eine Boolean-Variable `x[b,s]` (Block liegt in diesem
Slot: ja/nein). Der Solver sucht daraus die zulässige Belegung mit dem besten
Zielfunktionswert.

- **Aufruf über eine austauschbare Solver-Schnittstelle** (`ISolver` /
  `OrToolsSolver`), die intern `StundenplanEngine.Planen(...)` startet. Alle
  Parameter (Zeitlimit, Gewichte, Strafen, Verbote, Lösungsanzahl) kommen aus dem
  `StundenplanInput`-Objekt, das aus den Excel-Blättern befüllt wird.
- **Konfigurierbares Zeitlimit** pro Lauf (`ZeitlimitSekunden`, Standard 30 s) –
  der Solver liefert innerhalb dieser Zeit die beste gefundene Lösung.
- **Mehrere, echte verschiedene Lösungen:** Über einen `SolutionCollector` und
  einen Mindest-Hamming-Abstand (`MindestAbstandLösungenBloecke`) werden nahezu
  identische Lösungen unterdrückt, sodass wirklich unterscheidbare Varianten
  ausgegeben werden.
- **Fortschritt & Abbruch:** Der Lauf meldet laufend seinen Fortschritt
  (`SolverFortschritt`) und lässt sich jederzeit über ein `CancellationToken`
  abbrechen. Ein **Live-Export** schreibt den jeweils besten Zwischenstand
  periodisch in nummerierte Excel-Dateien.
- **Diagnose bei Unlösbarkeit:** Meldet der Solver `INFEASIBLE`, läuft eine
  stufenweise Diagnose, die die harten Regeln einzeln prüft und den auslösenden
  Lehrer bzw. das auslösende Kriterium benennt.

---

## Lehrertausch

Das Programm kann Lehrer innerhalb einer Gruppe gegeneinander **tauschen**, um
bessere Pläne zu finden, als es die feste Zuordnung erlauben würde.

- **Steuerung über das LTKZ (Lehrer-Tausch-Kennzeichen)** in der UV: Ein LTKZ
  besteht aus **Zahl + Buchstabe** (z. B. `5a`, `5b`, `5c`). Alle Einträge mit
  **gleicher Zahl** bilden eine **Tauschgruppe**; jeder **Buchstabe** ist eine
  **Rolle** (ein Lehrer mit seinen Blöcken). Innerhalb einer Gruppe darf jede
  Rolle gegen jede andere getauscht werden (z. B. `5a↔5b`).
- **Zwei Phasen pro Lauf:**
  - **Phase 1 – ohne Tausch:** die besten Lösungen mit der Original-Zuordnung
    (`AnzahlLösungenOhneTausch`).
  - **Phase 2 – mit Tausch:** die aussichtsreichsten Tausch-Kombinationen werden
    bewertet und durchgerechnet (`AnzahlLösungenMitTausch`). Eine Kombination kann
    auch **mehrere gleichzeitige Tausche** enthalten (z. B. `5a↔5b` **und**
    `1a↔1b`).
- **Ergebnis:** Am Ende stehen die besten Pläne *ohne* und *mit* Tausch
  nebeneinander zur Auswahl; jede Tauschvariante ist im Log und in der Diagnose
  nachvollziehbar beschriftet. Sind Klassen bereits ohne Tausch unlösbar, wird
  Phase 2 übersprungen.

---

## Harte Constraints je Kriterium

Viele Kriterien wirken standardmäßig nur als **Strafe in der Zielfunktion**
(weiche Regel). Sie lassen sich aber gezielt zu **echten harten Constraints**
hochstufen – teils global über das `PM`-Blatt, teils pro Lehrer über eigene
„hart"-Spalten im `StD`-Blatt. Jedes gesetzte Flag ist eine zusätzliche
Bedingung, die der Solver zwingend einhalten muss.

**Immer harte (strukturelle) Regeln:**

- **Klassenregel** – pro Slot höchstens eine Belegung je Klasse. Ausnahmen:
  gleiches, nicht-leeres **KKK** (Klassen-Kollisions-Kennzeichen) erlaubt
  Koexistenz; **A-/B-Wochengruppen** kollidieren nie.
- **Fachraum-Limit** – pro Slot höchstens *N* Blöcke je Fachgruppe (raumbezogen),
  ebenfalls A-/B-Wochen-bewusst.
- **Zeitwünsche −3** – mit `−3` gesperrte Slots für Lehrer bzw. Klassen sind hart
  blockiert.

**Wahlweise hart schaltbare Kriterien:**

- **Freie Tage** je Lehrer: `−3` = hart (mind. *N* freie Tage garantiert),
  `−2` = Wunsch (nur Strafe, hart nur bei aktivem „Verbot −2").
- **Freie Stunden / freies Band** je Lehrer (Teilband, z. B. 5.–11. Stunde),
  analog mit `−3`/`−2`.
- **Zeitwünsche −2** – global per „Verbot −2-Verletzungen" hart statt nur bestraft.
- **Späte Doppelstunden** – per „Verbot späte Doppelstunden" ganz untersagen.
- **Später Tag → späterer Beginn am Folgetag** je Lehrer (StD-Spalte „Gewicht
  Spät-Früh"): Hat ein Lehrer an einem Tag späte Stunden, soll er am Folgetag
  nicht zu früh beginnen. `−3` = hart, `−2` = Strafe. Die Schwellen sind global
  im `PM`-Blatt einstellbar („Spätgrenze Vortag", „Frühgrenze Folgetag") und die
  Regel greift nur oberhalb einer Stundenzahl am Vortag („Schwelle Std./Tag
  Vortag"). Verstöße werden in der Infeasibility-Diagnose (lehrerspezifisch), in
  „Plan prüfen", in der Diag-Tabelle und im Plan-Editor (Warnung beim Ziehen)
  dokumentiert.
- **Lehrer-Stammdatenregeln (StD, „…hart"-Spalten), pro Lehrer einzeln:**
  - **HohlWoche hart** – Wochensumme der Hohlstunden ≤ Sollwert.
  - **Folge hart** – nie mehr als *N* Stunden am Stück.
  - **Std./Tag hart** – kein Tag mit mit mehr oder weniger Unterrichtsstunden als im angegebenen Bereich.
  - **DoppelHohl hart** – keine zwei Hohlstunden in Folge.
  - **DreifachHohl hart** – keine drei oder mehr Hohlstunden in Folge.
  - **Verbot Bad units** – späte pädagogische Einheiten dieses Lehrers hart
    verboten.

**Nur als Strafe wirkende Kriterien (Zielfunktion, über `PM` gewichtet):**
frühe/späte Doppelstunden, späte pädagogische Einheiten („bad units"), Anzahl
freier Tage, Hohlstunden / Doppel- / Dreifach-Hohlstunden, Stundenfolge,
Einzelstunden, späte LK-Stunden, Hauptfächer zu spät, Fächer-Doppelstunden am
selben Tag, zu früher Beginn nach einem späten Vortag (Spät-Früh, `−2`) sowie
zu wenige nutzbare Hohlstunden (NuHo).

---

## Untis-Anbindung: UV-Export und Stundenplan-Reimport

Das Programm arbeitet nahtlos mit dem Untis-DIF-Format (`GPU002.TXT`, 46 Felder,
Trennzeichen `;`) zusammen – in beide Richtungen.

### UV aus Untis importieren (Schritt 6)

Eine aus Untis exportierte **`GPU002.TXT`** wird in das `UV`-Blatt eingelesen
(`ImportiereInUv`). Mehrere Untis-Zeilen derselben U-Nr und desselben Lehrers
(eine Zeile je Klasse) werden dabei zu **einer** UV-Zeile mit kommagetrennten
Klassen zusammengeführt. Zeichensatz (UTF-8/ANSI) wird automatisch erkannt.

### UV nach Untis exportieren (Schritt 7)

Die UV lässt sich als Untis-kompatible **`GPU002.TXT`** zurückschreiben
(`GpuExporter`). Kurznamen und Räume werden dabei automatisch Untis-konform
bereinigt (verbotene Zeichen entfernt, Raum-Alternativen von Komma auf `~`
umgestellt); jede Bereinigung wird protokolliert.

### Fertigen Stundenplan zurück nach Untis („ZZ-Trick")

Damit der **erzeugte Plan** in Untis landet, ohne dessen echte UV zu verändern,
nutzt das Programm einen Dummy-Lehrer-Trick:

- Zu jeder verplanten U-Nr wird eine zusätzliche GPU002-Zeile mit dem
  **Dummy-Lehrer `ZZ<UNr>`** erzeugt (Wochenstunden auf 0), begleitet von einer
  **Zeitwunsch-Datei `GPU016_ZZ.TXT`**, die den ZZ-Lehrern die belegten Slots als
  Zeitwünsche vorgibt. Beide Dateien werden zusammen in Untis importiert – der
  Plan „schnappt" dort auf die gewünschten Zeiten ein.
- Im **GPU-Quelle-Modus** wird eine bestehende `GPU002.TXT` sogar
  **byte-identisch** durchgeschrieben (inkl. BOM, Zeilenenden, Zeichensatz) und
  nur um die ZZ-Zeilen ergänzt. So entstehen beim Reimport **keine ungewollten
  Mini-Änderungen** an der Original-UV.

### Weitere Untis-nahe Exporte

Zeitwünsche-Import (`ZeitwunschExporter` → Blätter `ZWL`/`ZWK`), ein kompakter
`Unr-Plan` (Slot → U-Nrn) sowie Abweichungs- und Fixierungs-Exporte runden die
Anbindung ab.

---

## Kurzüberblick der Bedienschritte (Hauptfenster)

1. Excel-Datei einlesen · 2. Zeitwünsche einlesen/exportieren · 6. UV als GPU
importieren · 7. UV als GPU exportieren · 8. Gezielt fixieren/ignorieren ·
9. PM-Parameter bearbeiten · 10. **Stundenplanerstellung** · 11. Plan-Editor ·
12. Lösung sichern · 14. Lösung verbessern · 15. Minimale Änderungen (Solver) ·
18.–20. Klassen- und Lehrerpläne erzeugen · 21. Stammdaten (StD) bearbeiten.
