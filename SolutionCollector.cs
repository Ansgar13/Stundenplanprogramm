using Google.OrTools.Sat;
using System.Collections.Generic;

namespace Stundenplan_V2
{
    public class PlanSolution
    {
        public int EarlyDouble;
        public int LateDouble;
        public List<List<int>> SlotBelegung;
    }

    public class SolutionCollector : CpSolverSolutionCallback
    {
        private readonly BoolVar[,] x;
        private readonly BoolVar[,] d;
        private readonly List<ZeitSlot> slots;
        private readonly int B;
        private readonly int S;
        private readonly int maxSolutions;

        public List<PlanSolution> Solutions = new();

        public SolutionCollector(
            BoolVar[,] xVars,
            BoolVar[,] dVars,
            List<ZeitSlot> slotList,
            int blockCount,
            int slotCount,
            int limit)
        {
            x = xVars;
            d = dVars;
            slots = slotList;
            B = blockCount;
            S = slotCount;
            maxSolutions = limit;
        }

        public override void OnSolutionCallback()
        {
            if (Solutions.Count >= maxSolutions)
                return;

            int early = 0;
            int late = 0;

            for (int b = 0; b < B; b++)
            {
                for (int s = 0; s < S - 1; s++)
                {
                    if (d[b, s] == null)
                        continue;

                    if (Value(d[b, s]) == 1)
                    {
                        if (slots[s].Stunde <= 5)
                            early++;
                        else
                            late++;
                    }
                }
            }

            var belegung = new List<List<int>>();

            for (int s = 0; s < S; s++)
            {
                var slotList = new List<int>();

                for (int b = 0; b < B; b++)
                    if (Value(x[b, s]) == 1)
                        slotList.Add(b);

                belegung.Add(slotList);
            }

            Solutions.Add(new PlanSolution
            {
                EarlyDouble = early,
                LateDouble = late,
                SlotBelegung = belegung
            });
        }
    }
}