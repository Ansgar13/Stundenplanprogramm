using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Stundenplan_V2
{
    // =====================================================================
    // ÜBERLAUF-/ERWEITERUNGSANZEIGE FÜR PARALLELE BLÖCKE
    //
    // Problem: BaueZelle/BaueVergleichsZelle zeichnen pro Zelle maximal
    // MaxTeileProZelle Bloecke nebeneinander (Zelle ist 76 px breit, mehr
    // ist nicht mehr lesbar). Alles darueber wurde bisher STILLSCHWEIGEND
    // weggeworfen — eine Vierfachbelegung sah aus wie eine Dreifachbelegung.
    //
    // Loesung (Muster wie im Fachgruppenplan):
    //   (A) Zaehl-Badge unten links, sobald die Zelle ueberlaeuft.
    //       Zaehlung A/B-getrennt wie in ZaehleFachgruppe, damit eine
    //       A/B-Aufteilung nicht als Doppelbelegung erscheint.
    //   (B) Vollstaendige Belegung als Zell-Tooltip.
    //   (C) Klick auf das Badge oeffnet ein Popup mit ALLEN Bloecken des
    //       Slots. Jede Zeile springt wie ein Klick im Plan (Details +
    //       Synchronisation + Tauschvorschlaege); die nicht sichtbaren
    //       Zeilen sind hinterlegt. Pro Zeile ausserdem "-> Park":
    //       entplant den Block (Modus Einzelstunde/Block wie beim Ziehen)
    //       und macht ihn damit auch ohne Drag&Drop erreichbar.
    //
    // Gilt fuer Lehrerplan, Klassenplan, angeheftete Kacheln (alle ueber
    // BaueZelle) und die Vergleichsansicht (BaueVergleichsZelle, dort
    // reine Ansicht ohne Park-Knopf).
    // =====================================================================
    public partial class PlanEditorDialog
    {
        // Wie viele parallele Bloecke eine Zelle nebeneinander ausschreibt.
        // Alles darueber steckt im Badge / Popup.
        private const int MaxTeileProZelle = 3;

        // Popup mit der vollstaendigen Belegungsliste einer Lehrer-/Klassen-
        // Zelle. Bewusst getrennt von _fgBelegungPopup (Fachgruppenplan),
        // damit sich beide Ansichten nicht gegenseitig zuklappen.
        private System.Windows.Controls.Primitives.Popup _parallelPopup;

        // -----------------------------------------------------------------
        // Zaehlung A/B-getrennt — identisch zur Logik in ZaehleFachgruppe
        // bzw. RoomConstraint.cs, nur auf einer beliebigen Blockliste
        // (die Vergleichsansicht arbeitet mit _vglBlocks2, nicht mit _blocks).
        // A-Summe = A-Woche + ohne Wochengruppe, B-Summe analog.
        // hatWochenTrennung = false  ->  anzahlA == anzahlB, eine Zahl genuegt.
        // -----------------------------------------------------------------
        private (int anzahlA, int anzahlB, bool hatWochenTrennung) ZaehleAB(
            List<int> bloecke, List<UnterrichtsBlock> blocks)
        {
            int anzahlA = 0, anzahlB = 0;
            bool hatWochenTrennung = false;

            if (bloecke == null || blocks == null) return (0, 0, false);

            foreach (int b in bloecke)
            {
                string wg = (blocks[b].WochenGruppe ?? "").Trim();
                if (wg == "A" || wg == "B") hatWochenTrennung = true;
                if (wg != "B") anzahlA++;
                if (wg != "A") anzahlB++;
            }
            return (anzahlA, anzahlB, hatWochenTrennung);
        }

        // -----------------------------------------------------------------
        // Ist die Mehrfachbelegung eine ECHTE Doppelbelegung — oder nur
        // korrektes Material (A/B-Woche, gleiche UNr, gleiches KKK)?
        //
        // Die Ausnahmen sind exakt die der harten Konfliktpruefung in
        // FindeHartenKonflikt:
        //   Lehreransicht : nur A-vs-B-Woche kollidiert nie.
        //   Klassenansicht: zusaetzlich gleiche UNr (parallele Teilblocke)
        //                   und gleiches nicht-leeres KKK (z.B. Reli/Ethik).
        // Nur wenn ein Paar keiner Ausnahme unterliegt, wird rot gemeldet.
        // -----------------------------------------------------------------
        private bool HatEchteDoppelbelegung(
            List<int> bloecke, List<UnterrichtsBlock> blocks, bool lehrerAnsicht)
        {
            if (bloecke == null || blocks == null) return false;

            for (int i = 0; i < bloecke.Count; i++)
            {
                for (int j = i + 1; j < bloecke.Count; j++)
                {
                    var b1 = blocks[bloecke[i]];
                    var b2 = blocks[bloecke[j]];

                    string wg1 = (b1.WochenGruppe ?? "").Trim();
                    string wg2 = (b2.WochenGruppe ?? "").Trim();
                    if ((wg1 == "A" && wg2 == "B") || (wg1 == "B" && wg2 == "A")) continue;

                    if (!lehrerAnsicht)
                    {
                        if (b1.UNr == b2.UNr) continue;
                        string k1 = (b1.KKK ?? "").Trim();
                        string k2 = (b2.KKK ?? "").Trim();
                        if (k1.Length > 0 && k1 == k2) continue;
                    }

                    return true;
                }
            }
            return false;
        }

        // Einzeiliger Beschreibungstext eines Blocks fuer Tooltip/Popup.
        // Gleicher Aufbau wie FachgruppenBlockText, aber auf einer
        // beliebigen Blockliste (Vergleichsansicht!).
        private string ParallelBlockText(UnterrichtsBlock block)
        {
            var teile = block.Teile;
            string klassen = string.Join(",", teile.SelectMany(t => t.Klassen).Distinct());
            string faecher = string.Join(",", teile.Select(t => t.Fach).Distinct());
            string lehrer = string.Join(",", teile.Select(t => t.Lehrer)
                                                  .Where(l => !string.IsNullOrWhiteSpace(l)).Distinct());
            string wg = (block.WochenGruppe ?? "").Trim();
            string kkk = (block.KKK ?? "").Trim();

            return "UNr " + block.UNr + " \u00B7 " + faecher + " \u00B7 " + klassen + " \u00B7 " + lehrer +
                   (wg == "" ? "" : "  [" + wg + "-Woche]") +
                   (kkk == "" ? "" : "  [KKK " + kkk + "]");
        }

        // -----------------------------------------------------------------
        // (B) Tooltip mit der VOLLSTAENDIGEN Belegung der Zelle.
        // Liefert null, solange die Zelle nicht ueberlaeuft — dann bleibt
        // das bisherige Verhalten (Warn-Tooltip des Teilbereichs) unberuehrt.
        // -----------------------------------------------------------------
        private string ParallelSlotTooltip(
            List<int> bloecke, List<UnterrichtsBlock> blocks, bool lehrerAnsicht)
        {
            if (bloecke == null || bloecke.Count <= MaxTeileProZelle) return null;

            var (anzahlA, anzahlB, trennung) = ZaehleAB(bloecke, blocks);

            var sb = new StringBuilder();
            sb.Append("Parallel in diesem Slot: " + bloecke.Count);
            if (trennung)
                sb.Append("   (A-Woche " + anzahlA + " \u00B7 B-Woche " + anzahlB + ")");

            for (int i = 0; i < bloecke.Count; i++)
                sb.Append("\n" + (i < MaxTeileProZelle ? "\u2022 " : "\u25B8 ") +
                          ParallelBlockText(blocks[bloecke[i]]));

            sb.Append("\n\n\u25B8 = in der Zelle nicht sichtbar (" +
                      (bloecke.Count - MaxTeileProZelle) + ").");
            sb.Append("\nKlick auf das Badge unten links: vollstaendige Liste mit Sprung und \u201E-> Park\u201C.");

            if (HatEchteDoppelbelegung(bloecke, blocks, lehrerAnsicht))
                sb.Append("\n\nACHTUNG: echte Doppelbelegung — nicht durch A/B-Woche" +
                          (lehrerAnsicht ? "" : ", gleiche UNr oder gleiches KKK") + " gedeckt.");

            return sb.ToString();
        }

        // -----------------------------------------------------------------
        // (A) Zaehl-Badge fuer die untere linke Zellenecke. null, solange
        // die Zelle nicht ueberlaeuft (dann aendert sich am Bild nichts).
        // Unten rechts sitzt die Zeitwunsch-Zahl, oben rechts das Fix-"F" —
        // deshalb unten links.
        //
        // Text: ohne A/B-Trennung die reine Anzahl ("4 \u25BE"), mit
        // Trennung beide Summen ("3A\u00B72B \u25BE").
        // Farbe: neutral blau = korrektes Material (Kopplung/KKK/A-B),
        //        rot = echte Doppelbelegung.
        // -----------------------------------------------------------------
        private FrameworkElement BaueUeberlaufBadge(
            List<int> bloecke, int slotIdx, string auswahl, bool lehrerAnsicht,
            List<UnterrichtsBlock> blocks, bool interaktiv, bool vergleich)
        {
            if (bloecke == null || bloecke.Count <= MaxTeileProZelle) return null;

            var (anzahlA, anzahlB, trennung) = ZaehleAB(bloecke, blocks);
            bool konflikt = HatEchteDoppelbelegung(bloecke, blocks, lehrerAnsicht);

            string text = (trennung
                              ? anzahlA + "A\u00B7" + anzahlB + "B"
                              : bloecke.Count.ToString())
                          + " \u25BE";

            Color hg, vg;
            if (konflikt) { hg = Color.FromRgb(0xF8, 0xD7, 0xDA); vg = Color.FromRgb(0x8B, 0x1A, 0x1A); }
            else { hg = Color.FromRgb(0xE3, 0xEA, 0xF8); vg = Color.FromRgb(0x10, 0x4A, 0xE0); }

            var badge = new Border
            {
                Background = new SolidColorBrush(hg),
                BorderBrush = new SolidColorBrush(vg),
                BorderThickness = new Thickness(0.7),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(1, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.Hand,
                ToolTip = ParallelSlotTooltip(bloecke, blocks, lehrerAnsicht),
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(vg)
                }
            };

            var bloeckeKopie = new List<int>(bloecke);
            int slotKopie = slotIdx;
            string auswahlKopie = auswahl;
            bool lehrerKopie = lehrerAnsicht;
            var blocksKopie = blocks;
            bool interaktivKopie = interaktiv;
            bool vergleichKopie = vergleich;

            badge.MouseLeftButtonDown += (s, e) =>
            {
                OeffneParallelPopup(badge, bloeckeKopie, slotKopie, auswahlKopie,
                                    lehrerKopie, blocksKopie, interaktivKopie, vergleichKopie);
                e.Handled = true;
            };

            return badge;
        }

        // -----------------------------------------------------------------
        // (C) Popup mit der vollstaendigen, anklickbaren Belegungsliste.
        // Zeilen ab MaxTeileProZelle sind hinterlegt (= in der Zelle nicht
        // sichtbar). Klick auf eine Zeile verhaelt sich wie ein Klick auf
        // den Teilbereich im Plan; "-> Park" entplant den Block.
        // -----------------------------------------------------------------
        private void OeffneParallelPopup(
            UIElement anker, List<int> bloecke, int slotIdx, string auswahl,
            bool lehrerAnsicht, List<UnterrichtsBlock> blocks, bool interaktiv, bool vergleich)
        {
            if (slotIdx < 0 || bloecke == null || bloecke.Count == 0 || blocks == null) return;

            var (anzahlA, anzahlB, trennung) = ZaehleAB(bloecke, blocks);
            bool konflikt = HatEchteDoppelbelegung(bloecke, blocks, lehrerAnsicht);

            var panel = new StackPanel { Margin = new Thickness(8), MaxWidth = 560 };

            string slotName = _slots[slotIdx].WTag + " " + _slots[slotIdx].Stunde + ". Std";
            string zaehlTxt = trennung
                ? bloecke.Count + " parallel (A-Woche " + anzahlA + " \u00B7 B-Woche " + anzahlB + ")"
                : bloecke.Count + " parallel";

            var kopf = new TextBlock
            {
                Text = (auswahl ?? "") + " \u00B7 " + slotName + " \u00B7 " + zaehlTxt,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            if (konflikt) kopf.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20));
            panel.Children.Add(kopf);

            if (konflikt)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Echte Doppelbelegung: nicht durch A/B-Woche" +
                           (lehrerAnsicht ? "" : ", gleiche UNr oder gleiches KKK") + " gedeckt.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x20, 0x20)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6)
                });
            }

            for (int i = 0; i < bloecke.Count; i++)
            {
                int blockIdx = bloecke[i];
                bool unsichtbar = i >= MaxTeileProZelle;

                // Grundfarbe: hinterlegt = in der Zelle nicht sichtbar
                Brush grundFarbe = unsichtbar
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xD8))
                    : Brushes.Transparent;

                var zeilenGitter = new Grid();
                zeilenGitter.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                zeilenGitter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var txt = new TextBlock
                {
                    Text = (unsichtbar ? "\u25B8 " : "\u2022 ") + ParallelBlockText(blocks[blockIdx]),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap
                };
                Grid.SetColumn(txt, 0);
                zeilenGitter.Children.Add(txt);

                var zeile = new Border
                {
                    Background = grundFarbe,
                    Padding = new Thickness(3, 2, 3, 2),
                    Cursor = Cursors.Hand,
                    Child = zeilenGitter
                };
                zeile.MouseEnter += (s, e) => zeile.Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xF3, 0xFF));
                zeile.MouseLeave += (s, e) => zeile.Background = grundFarbe;

                int idxLokal = blockIdx;
                int slotLokal = slotIdx;
                bool lehrerLokal = lehrerAnsicht;
                var blocksLokal = blocks;

                if (vergleich)
                {
                    // Reine Ansicht: nur die Lehrer/Klassen-Synchronisation
                    // der Vergleichsspalten (kein Zugriff auf _blocks!).
                    zeile.MouseLeftButtonDown += (s, e) =>
                    {
                        SchliesseParallelPopup();
                        VergleichsKlickSync(blocksLokal[idxLokal], lehrerLokal);
                        e.Handled = true;
                    };
                }
                else
                {
                    // Exakt die Folgeaktionen von Teil_MouseLeftButtonDown,
                    // damit ein nicht sichtbarer Block genauso auswaehlbar ist
                    // wie ein sichtbarer.
                    zeile.MouseLeftButtonDown += (s, e) =>
                    {
                        SchliesseParallelPopup();
                        ZeigeDetails(idxLokal);
                        SynchronisiereAnderenPlan(idxLokal, lehrerLokal);
                        LeereVerschiebungen();
                        ZeigeTauschvorschlaege(idxLokal, slotLokal);
                        e.Handled = true;
                    };
                }

                // "-> Park": entplant den Block. Ersetzt das Ziehen in den
                // Parkbereich, das fuer nicht sichtbare Bloecke unmoeglich ist.
                if (interaktiv && !vergleich)
                {
                    var btnPark = new Button
                    {
                        Content = "\u2192 Park",
                        FontSize = 11,
                        Padding = new Thickness(6, 1, 6, 1),
                        Margin = new Thickness(10, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = Cursors.Hand,
                        ToolTip = "Entplant diesen Unterricht (Modus \u201EEinzelstunde\u201C: nur diese Stunde, " +
                                  "Modus \u201EBlock\u201C: alle Stunden des Tages) und legt ihn in den Parkbereich."
                    };
                    btnPark.Click += (s, e) =>
                    {
                        SchliesseParallelPopup();
                        EntplaneAusPopup(idxLokal, slotLokal);
                    };
                    Grid.SetColumn(btnPark, 1);
                    zeilenGitter.Children.Add(btnPark);
                }

                panel.Children.Add(zeile);
            }

            if (interaktiv && !vergleich)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "Hinterlegte Zeilen sind in der Zelle nicht sichtbar. " +
                           "Klick auf eine Zeile = wie Klick im Plan.",
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0)
                });
            }

            var rahmen = new Border
            {
                Background = Brushes.White,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Child = panel
            };

            if (_parallelPopup == null)
                _parallelPopup = new System.Windows.Controls.Primitives.Popup
                {
                    AllowsTransparency = true,
                    StaysOpen = false,
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
                };

            _parallelPopup.IsOpen = false;      // erzwingt Neupositionierung
            _parallelPopup.PlacementTarget = anker;
            _parallelPopup.Child = rahmen;
            _parallelPopup.IsOpen = true;
        }

        private void SchliesseParallelPopup()
        {
            if (_parallelPopup != null) _parallelPopup.IsOpen = false;
        }

        // -----------------------------------------------------------------
        // Entplanen aus dem Popup. Welche Stunden betroffen sind, richtet
        // sich wie beim Ziehen nach dem Modus-Radiobutton.
        // Zusaetzlich zum Drag-Weg wird bei fixierten Stunden nachgefragt —
        // ein Klick passiert leichter versehentlich als ein Drag.
        // -----------------------------------------------------------------
        private void EntplaneAusPopup(int blockIdx, int slotIdx)
        {
            if (_belegung == null || _blocks == null) return;
            if (blockIdx < 0 || blockIdx >= _blocks.Count) return;

            var slots = SlotsNachModus(blockIdx, slotIdx);
            if (slots.Count == 0) return;

            int unr = _blocks[blockIdx].UNr;
            int fixAnzahl = slots.Count(s => _slots[s].FixUNrn.Contains(unr));
            if (fixAnzahl > 0)
            {
                var antwort = MessageBox.Show(
                    $"UNr {unr} ist in {fixAnzahl} der {slots.Count} betroffenen Stunde(n) fixiert.\n\n" +
                    "Trotzdem entplanen?\n" +
                    "(Der Eintrag in \"Fix UNrn\" bleibt bestehen — wie beim Ziehen in den Parkbereich.)",
                    "Fixierten Block entplanen",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (antwort != MessageBoxResult.Yes)
                {
                    SetStatus("Entplanen abgebrochen — Block ist fixiert.", false);
                    return;
                }
            }

            EntplaneBlockSlots(blockIdx, slots);
        }

        // Betroffene Slots nach Modus: "Einzelstunde" = nur der angeklickte
        // Slot, "Block" = alle belegten Slots dieses Blocks am selben Tag.
        // Identisch zu Teil_MouseMove, damit Klick- und Drag-Weg dasselbe tun.
        private List<int> SlotsNachModus(int blockIdx, int slotIdx)
        {
            if (RbEinzel != null && RbEinzel.IsChecked == true)
                return new List<int> { slotIdx };

            string tag = _slots[slotIdx].WTag;
            var liste = new List<int>();
            for (int s = 0; s < _slots.Count; s++)
                if (_belegung[blockIdx, s] == 1 && _slots[s].WTag == tag)
                    liste.Add(s);

            if (liste.Count == 0) liste.Add(slotIdx);
            return liste;
        }

        // Gemeinsamer Kern des Entplanens — wird von Parkbereich_Drop
        // (Drag in den Parkbereich) und von EntplaneAusPopup benutzt.
        private void EntplaneBlockSlots(int blockIdx, List<int> slots)
        {
            if (slots == null || slots.Count == 0) return;

            foreach (int s in slots)
                _belegung[blockIdx, s] = 0;

            SetStatus("UNr " + _blocks[blockIdx].UNr + " entplant (" + slots.Count + " Stunde(n)).", false);

            ZeichneBeideGrids();
            ZeichneParkbereich();
            PruefeUndZeigeWarnungen();
        }
    }
}
