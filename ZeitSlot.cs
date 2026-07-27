using System.Collections.Generic;

namespace Stundenplan_V2
{
    public class ZeitSlot
    {
        public string WTag { get; set; }
        public int Stunde { get; set; }

        // belegte Unterrichtsnummern
        public List<int> BelegteUNrn { get; set; }
        public List<int> FixUNrn { get; set; } = new();
        // 🔥 Zeitwünsche
        public Dictionary<string, int> LehrerWunsch { get; set; }
        public Dictionary<string, int> KlassenWunsch { get; set; }

        public ZeitSlot()
        {
            BelegteUNrn = new List<int>();
            LehrerWunsch = new Dictionary<string, int>();
            KlassenWunsch = new Dictionary<string, int>();
        }
    }
}