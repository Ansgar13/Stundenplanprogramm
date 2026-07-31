using Stundenplan_V2;

public class StundenplanInput
{
    public List<UnterrichtsBlock> Blocks { get; set; } = new();
    public List<ZeitSlot> Slots { get; set; } = new();
    public Dictionary<string, int> Fachraeume { get; set; } = new();
    public Dictionary<string, int> ExtraFreieTage { get; set; } = new();
    public string ExcelPfad { get; set; }

    // Lehrer-Stammdaten (aus Sheet "Stammdaten")
    public Dictionary<string, LehrerStammdaten> LehrerStammdaten { get; set; } = new();

    // Parameter-Sheet
    public int ZeitlimitSekunden { get; set; } = 30;
    public int AnzahlLösungenOhneTausch { get; set; } = 2;
    public int AnzahlLösungenMitTausch { get; set; } = 2;
    // Mindestanzahl Blöcke, die sich zwischen zwei ausgegebenen Lösungen
    // mindestens unterscheiden müssen (verhindert nahezu identische Lösungen).
    public int MindestAbstandLösungenBloecke { get; set; } = 5;
    // Warnungen zu UV-Zeilen ohne Fach und/oder ohne Klasse (Pflichtfelder).
    public List<string> UvFachKlasseWarnungen { get; set; } = new();
    // Dieselben Zeilen wie oben, aber nur die reinen UNr-Werte (dedupliziert,
    // aufsteigend sortiert) — für kompakte Anzeige direkt in der Warn-MessageBox,
    // ohne den vollen Beschreibungstext parsen zu müssen.
    public List<int> UvFachKlasseWarnungUNrn { get; set; } = new();

    // Hinweise auf Werte im Sheet "PM", die sich nicht sauber als Zahl lesen
    // ließen (z.B. "200 s" statt 200) oder gerundet werden mussten. Wichtig,
    // weil in diesen Fällen still der Standardwert gilt und der Solver dann
    // mit anderen Vorgaben läuft als in der Tabelle stehen.
    public List<string> PmWarnungen { get; set; } = new();
    public HashSet<string> NichtFreieTage { get; set; } = new HashSet<string>();

    // Qualitätsfunktion-Gewichte
    public int GewichtFrüheDoppel { get; set; } = 1;
    public int GewichtSpäteDoppel { get; set; } = 5;
    public int GewichtSpätePädEinheiten { get; set; } = 5;
    public int GewichtFreieTage { get; set; } = 2;

    // Große Pausen: Liste von (StundeVor, StundeNach) z.B. (2,3), (4,5), (6,7)
    public List<(int stundeVor, int stundeNach)> GrossePausen { get; set; } = new();

    // Verbot Doppelstunden ab Stunde 6/7 aufwärts (5/6 bleibt erlaubt)
    public bool VerbotSpäteDoppel { get; set; } = false;

    // -2-Lehrer-Wünsche: entweder hart verboten oder mit Strafe belegt
    public bool VerbotMinus2Verletzungen { get; set; } = false;
    public int  StrafeMinus2Verletzungen { get; set; } = 0;

    // Lehrer, die neben ihrem Freie-Tage-Wunsch eine -2 eingetragen haben
    public HashSet<string> LehrerFreiTageMinus2 { get; set; } = new();
    public HashSet<string> LehrerFreiTageMinus3 { get; set; } = new();

    // ---- FREIE STUNDEN (Teilband) ------------------------------------
    // Neues Kriterium analog zu den freien Tagen, aber nur fuer ein
    // Stundenband (z.B. "5-11" = 5.-11. Stunde). Ein Tag zaehlt als
    // "freies Band", wenn der Lehrer an diesem Tag in KEINER Stunde des
    // Bandes Unterricht hat. Gefordert werden >= N solcher Tage pro Woche.
    //   ExtraFreieStunden        : Lehrer -> Anzahl Tage (N)
    //   FreieStundenBereich      : Lehrer -> (vonStunde, bisStunde) inklusive
    //   LehrerFreieStundenMinus3 : Gewicht -3 (zwingend/hart)
    //   LehrerFreieStundenMinus2 : Gewicht -2 (Wunsch; hart nur bei Verbot -2)
    // Die -2/-3-Mechanik nutzt bewusst dieselben Schalter/Strafen wie die
    // freien Tage (VerbotMinus2Verletzungen / StrafeMinus2Verletzungen);
    // ein eigener PM-Schalter ist nicht vorgesehen.
    public Dictionary<string, int> ExtraFreieStunden { get; set; } = new();
    public Dictionary<string, (int von, int bis)> FreieStundenBereich { get; set; } = new();
    public HashSet<string> LehrerFreieStundenMinus2 { get; set; } = new();
    public HashSet<string> LehrerFreieStundenMinus3 { get; set; } = new();
    // Diagnosezeilen zum Einlesen der FT-Tabelle (welche Einträge registriert/verworfen wurden)
    public List<string> FtDiagnose { get; set; } = new();
    // Diagnosezeilen zum Einlesen der StD-Tabelle: welche "hart"-Flags gesetzt
    // wurden und welche wegen eines fehlenden Werts ignoriert werden mussten.
    public List<string> StdDiagnose { get; set; } = new();
    // Hauptfach-Strafe: Hauptfächer (D,E,M,F) nicht zu oft nach Stunde 4
    public int HauptfachSpätAnteilProzent { get; set; } = 50; // max x% der Stunden nach Stunde 4
    public int StrafeHauptfachSpät { get; set; } = 0;         // Strafe pro Stunde über dem Limit

    // Nutzbare Hohlstunden (NuHo)
    // Sollwert je Zeitslot (gilt fuer jeden Wochentag und die Stunden 2..5).
    // 0 = keine NuHo-Vorgabe/-Strafe (Standard).
    public int NuHoSollwertProZeitslot { get; set; } = 0;
    // Strafhoehe pro fehlender NuHo (je Slot: max(0, Soll - Ist)).
    public int StrafeZuWenigNuHo { get; set; } = 0;

    // Hohlstunden-Strafen
    public int StrafeHohlstunde { get; set; } = 1;
    public int StrafeDoppelHohlstunde { get; set; } = 5;
    public int StrafeDreifachHohlstunde { get; set; } = 5;
    public int StrafeStdFolge { get; set; } = 5;
    public int StrafeEinzelstunde { get; set; } = 0;
    public int StrafeSpäteLkStunden { get; set; } = 0;
    public int GrenzeSpäteLk { get; set; } = 2;

    // Späte pädagogische Einheiten – konfigurierbare Zählung:
    // Fächer (exakter Fach-String, Groß/Klein egal) aus PM-Zeile
    // "Fächer ohne Spätzählung"; eine Einheit fällt nur raus, wenn ALLE ihre
    // Teile ein solches Fach tragen.
    public HashSet<string> AusgenommeneSpaetFaecher { get; set; }
        = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    // Wst -> Schwelle (Anzahl später Slots ab Stunde 6, ab der eine Einheit als
    // "spät/bad" gilt) aus Sheet "SpätSchwelle". Fehlt eine Wst, gilt 2.
    public Dictionary<int, int> SpaetSchwelleJeWst { get; set; }
        = new Dictionary<int, int>();

    // "Fächer-Doppelstd. nicht am selben Tag" (PM, Spalte B kommasepariert):
    // pro Klasse und Tag soll höchstens EINES dieser Fächer eine Doppelstunde
    // haben. Weiche Regel – jedes weitere gelistete Fach mit Doppelstunde am
    // selben Tag (in derselben Klasse) kostet StrafeDoppelSelberTag Punkte.
    public HashSet<string> DoppelSelberTagFaecher { get; set; }
        = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
    // Strafgewicht zur obigen Regel (PM-Zeile "Strafe Fächer-Doppelstd. selber
    // Tag"; Default 5, falls die Zeile fehlt).
    public int StrafeDoppelSelberTag { get; set; } = 5;
}

