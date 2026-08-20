using System;
using System.Collections.Generic;
using System.Linq;

namespace Stundenplan_V2
{
    // =====================================================================
    // KLASSENGRUPPEN  (Untis-Konzept, Vollausbau / "Variante 2")
    //
    // Modelliert Klassen, die in Gruppen aufgeteilt sind — auch mit
    // TEILWEISE UEBERLAPPENDEN oder KLASSENUEBERGREIFENDEN Gruppen
    // (z.B. Fremdsprachen-Kurse, die Schueler aus 10a UND 10b ziehen).
    //
    // Grundidee: Jeder Klassen-Token (so wie er in der UV-Spalte
    // "Klasse(n)" steht) wird auf eine Menge ATOMARER "Bausteine"
    // abgebildet — kleinste, nicht weiter teilbare Schuelermengen.
    //   * Zwei Token KOLLIDIEREN genau dann, wenn ihre Bausteinmengen sich
    //     schneiden (= gemeinsame Schueler).
    //   * Disjunkte Gruppen (z.B. 10a_m / 10a_w) teilen keinen Baustein
    //     und duerfen daher parallel liegen.
    //   * Die GANZE Klasse (10a) enthaelt automatisch die Bausteine ALLER
    //     ihrer Gruppen und kollidiert daher mit jeder einzelnen Gruppe.
    //
    // Ein Token, der NICHT im Sheet "Klassengruppen" vorkommt, ist sein
    // eigener, einziger Baustein (= er selbst). Ohne dieses Sheet liefert
    // AtomeDesBlocks() also exakt die bisherigen Klassen-Token zurueck.
    //
    // ANZEIGE (Eltern/Untergruppen): Zusaetzlich zur Kollisionsrechnung
    // haelt diese Klasse fest, welche Gruppe unter welcher Elternklasse
    // angezeigt werden soll — gesteuert ueber Spalte "Klasse" (B), die auch
    // MEHRERE Eltern kommasepariert nennen darf (Frz10 -> "10a, 10b").
    //
    // Der KKK-Mechanismus bleibt voellig unberuehrt.
    // =====================================================================
    public sealed class KlassenGruppen
    {
        // Token -> Menge seiner Bausteine (atomare Schuelersegmente).
        private readonly Dictionary<string, HashSet<string>> _atome
            = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Untergruppe (Token) -> Menge ihrer Elternklassen (aus Spalte B).
        private readonly Dictionary<string, HashSet<string>> _elternVon
            = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Elternklasse -> Menge ihrer Untergruppen-Token.
        private readonly Dictionary<string, HashSet<string>> _untergruppenVon
            = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Alle in Spalte B genannten Elternklassen.
        private readonly HashSet<string> _elternklassen
            = new HashSet<string>(StringComparer.Ordinal);

        // Einlese-Protokoll.
        public IReadOnlyList<string> Diagnose { get; }

        public static KlassenGruppen Leer { get; } =
            new KlassenGruppen(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
                               new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
                               new Dictionary<string, HashSet<string>>(StringComparer.Ordinal),
                               new HashSet<string>(StringComparer.Ordinal),
                               new List<string>());

        public bool IstLeer => _atome.Count == 0;

        private KlassenGruppen(
            Dictionary<string, HashSet<string>> atome,
            Dictionary<string, HashSet<string>> elternVon,
            Dictionary<string, HashSet<string>> untergruppenVon,
            HashSet<string> elternklassen,
            List<string> diagnose)
        {
            _atome = atome;
            _elternVon = elternVon;
            _untergruppenVon = untergruppenVon;
            _elternklassen = elternklassen;
            Diagnose = diagnose;
        }

        // -----------------------------------------------------------------
        // Baut das Modell aus Roh-Zeilen des Sheets "Klassengruppen".
        // Jede Zeile: (gruppe, klasse, bausteine)
        //   gruppe    = Token, wie er in der UV steht (Pflicht).
        //   klasse    = uebergeordnete Klasse(n), KOMMASEPARIERT erlaubt.
        //               Steuert die Anzeige (Untergruppe unter Elternklasse)
        //               und — NUR wenn die Gruppe KEINE eigenen Bausteine hat
        //               — auch die Vererbung der Bausteine an die Elternklasse.
        //   bausteine = explizite atomare Segmente (optional). Leer => es
        //               wird EIN automatischer Baustein erzeugt.
        //
        // Wichtig: Hat eine Gruppe EIGENE Bausteine (klassenuebergreifender
        // Kurs), wird ueber Spalte B NICHT vererbt — sonst wuerden die
        // genannten Elternklassen faelschlich gemeinsame Schueler bekommen.
        // Spalte B dient dann rein der Anzeige-Zuordnung.
        // -----------------------------------------------------------------
        public static KlassenGruppen Baue(
            IEnumerable<(string gruppe, string klasse, List<string> bausteine)> zeilen,
            List<string> diagnose = null)
        {
            diagnose ??= new List<string>();
            var atome = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var elternVon = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var untergruppenVon = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var elternklassen = new HashSet<string>(StringComparer.Ordinal);
            // Elternklasse -> vererbte Bausteine (nur aus Gruppen OHNE eigene).
            var elternAtome = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            if (zeilen != null)
            {
                foreach (var (gruppeRaw, klasseRaw, bausteineRaw) in zeilen)
                {
                    string gruppe = (gruppeRaw ?? "").Trim();
                    if (string.IsNullOrEmpty(gruppe))
                    {
                        diagnose.Add("Klassengruppen: Zeile ohne Gruppen-Token uebersprungen.");
                        continue;
                    }

                    // Eltern (Spalte B) — kommasepariert, ohne Selbstbezug.
                    var eltern = (klasseRaw ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(e => e.Trim())
                        .Where(e => e.Length > 0 && !string.Equals(e, gruppe, StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    var eigeneBausteine = (bausteineRaw ?? new List<string>())
                        .Select(b => (b ?? "").Trim())
                        .Where(b => b.Length > 0)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    bool hatExplizit = eigeneBausteine.Count > 0;

                    // Ohne explizite Bausteine: automatischer, eindeutiger Baustein.
                    var bausteine = hatExplizit
                        ? eigeneBausteine
                        : new List<string> { "@" + gruppe };

                    if (!atome.TryGetValue(gruppe, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        atome[gruppe] = set;
                    }
                    foreach (var b in bausteine) set.Add(b);

                    foreach (var p in eltern)
                    {
                        elternklassen.Add(p);

                        if (!elternVon.TryGetValue(gruppe, out var pset))
                        {
                            pset = new HashSet<string>(StringComparer.Ordinal);
                            elternVon[gruppe] = pset;
                        }
                        pset.Add(p);

                        if (!untergruppenVon.TryGetValue(p, out var cset))
                        {
                            cset = new HashSet<string>(StringComparer.Ordinal);
                            untergruppenVon[p] = cset;
                        }
                        cset.Add(gruppe);

                        // Vererbung nur, wenn die Gruppe KEINE eigenen Bausteine
                        // hat (sauberer Split innerhalb EINER Klasse). Bei
                        // eigenen Bausteinen ist Spalte B reine Anzeige.
                        if (!hatExplizit)
                        {
                            if (!elternAtome.TryGetValue(p, out var eset))
                            {
                                eset = new HashSet<string>(StringComparer.Ordinal);
                                elternAtome[p] = eset;
                            }
                            foreach (var b in bausteine) eset.Add(b);
                        }
                    }

                    diagnose.Add(
                        $"Klassengruppe '{gruppe}'" +
                        (eltern.Count == 0 ? "" : $" (Klasse '{string.Join(", ", eltern)}')") +
                        " -> Bausteine {" + string.Join(", ", bausteine) + "}" +
                        (hatExplizit && eltern.Count > 0 ? "  [Spalte B nur Anzeige]" : ""));
                }
            }

            foreach (var kv in elternAtome)
            {
                if (!atome.TryGetValue(kv.Key, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    atome[kv.Key] = set;
                }
                foreach (var b in kv.Value) set.Add(b);
            }

            return new KlassenGruppen(atome, elternVon, untergruppenVon, elternklassen, diagnose);
        }

        // -----------------------------------------------------------------
        // Bausteine eines einzelnen Token. Unbekannter Token => { token }.
        // -----------------------------------------------------------------
        public IReadOnlyCollection<string> Atome(string token)
        {
            token = (token ?? "").Trim();
            if (token.Length == 0) return Array.Empty<string>();
            if (_atome.TryGetValue(token, out var set)) return set;
            return new[] { token };
        }

        // -----------------------------------------------------------------
        // Vereinigte Bausteine eines ganzen Blocks (ueber alle Klassen-Token).
        // Schluessel fuer die Kollisionsschleifen. Leeres Modell => bisherige
        // distinct Klassen-Token.
        // -----------------------------------------------------------------
        public IReadOnlyCollection<string> AtomeDesBlocks(UnterrichtsBlock block)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (block == null) return result;

            foreach (var token in block.Teile
                        .SelectMany(t => t.Klassen)
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .Distinct(StringComparer.Ordinal))
            {
                foreach (var atom in Atome(token))
                    result.Add(atom);
            }
            return result;
        }

        // -----------------------------------------------------------------
        // Ueberschneiden sich zwei Klassen-Token (teilen sie Schueler)?
        // Gleicher Token => immer true. Sonst: Schnitt der Bausteinmengen.
        // -----------------------------------------------------------------
        public bool Überschneiden(string token1, string token2)
        {
            string a = (token1 ?? "").Trim();
            string b = (token2 ?? "").Trim();
            if (a.Length == 0 || b.Length == 0) return false;
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;

            var setA = Atome(a);
            foreach (var atom in Atome(b))
                if (setA.Contains(atom)) return true;
            return false;
        }

        // -----------------------------------------------------------------
        // Gehoert ein Block (irgendeiner seiner Klassen-Token) zur Klasse
        // 'klasse'? "Gehoert zu" = teilt Schueler mit ihr -> erscheint im
        // Plan dieser Klasse (fuer die Anzeige).
        // -----------------------------------------------------------------
        public bool GehörtZuKlasse(UnterrichtsBlock block, string klasse)
        {
            if (block == null) return false;
            foreach (var token in block.Teile.SelectMany(t => t.Klassen).Distinct(StringComparer.Ordinal))
                if (Überschneiden(token, klasse)) return true;
            return false;
        }

        // -----------------------------------------------------------------
        // Teilen zwei Bloecke Schueler (gemeinsamer Baustein)? Fuer die
        // Doppelbelegungs-Heuristik der Zellenanzeige.
        // -----------------------------------------------------------------
        public bool BloeckeUeberschneiden(UnterrichtsBlock a, UnterrichtsBlock b)
        {
            if (a == null || b == null) return false;
            var setA = AtomeDesBlocks(a);
            foreach (var atom in AtomeDesBlocks(b))
                if (setA.Contains(atom)) return true;
            return false;
        }

        // -------- Anzeige-Beziehungen (Eltern / Untergruppen) -------------

        public bool IstElternklasse(string token)
            => _elternklassen.Contains((token ?? "").Trim());

        public bool IstUntergruppe(string token)
            => _elternVon.ContainsKey((token ?? "").Trim());

        public IReadOnlyCollection<string> ElternVon(string token)
            => _elternVon.TryGetValue((token ?? "").Trim(), out var s)
               ? (IReadOnlyCollection<string>)s
               : Array.Empty<string>();

        public IReadOnlyCollection<string> UntergruppenVon(string elternklasse)
            => _untergruppenVon.TryGetValue((elternklasse ?? "").Trim(), out var s)
               ? (IReadOnlyCollection<string>)s
               : Array.Empty<string>();
    }
}
