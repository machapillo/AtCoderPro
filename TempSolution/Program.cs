using System;
using System.Collections.Generic;
using System.Linq;

class Program
{
    static void Main()
    {
        // Require fast I/O
        var inputs = Console.ReadLine().Split();
        int N = int.Parse(inputs[0]);
        int L = int.Parse(inputs[1]);
        int T = int.Parse(inputs[2]);
        long K = long.Parse(inputs[3]);

        var A = Console.ReadLine().Split().Select(int.Parse).ToArray();
        var C = new long[L][];
        for (int i = 0; i < L; i++)
        {
            C[i] = Console.ReadLine().Split().Select(long.Parse).ToArray();
        }

        // State
        long currentApples = K;
        long[,] B = new long[L, N];
        int[,] P = new int[L, N];

        // Init B to 1
        for (int i = 0; i < L; i++)
        for (int j = 0; j < N; j++)
            B[i, j] = 1;

        Random rand = new Random();

        // Game Loop
        for (int t = 0; t < T; t++)
        {
            // Decision: Try to upgrade something affordable
            // Heuristic: Prefer upgrading Level 0 (direct apples) or Level 1 (machine production) 
            // depending on the turn. Early game -> Higher levels, Late game -> Level 0.
            
            var candidates = new List<(int i, int j, long cost)>();

            for (int i = 0; i < L; i++)
            {
                for (int j = 0; j < N; j++)
                {
                    long cost = C[i][j] * (P[i, j] + 1);
                    if (cost <= currentApples)
                    {
                        candidates.Add((i, j, cost));
                    }
                }
            }

            int targetI = -1;
            int targetJ = -1;

            if (candidates.Count > 0)
            {
                // Simple strategy: Randomly pick an affordable one
                var pick = candidates[rand.Next(candidates.Count)];
                targetI = pick.i;
                targetJ = pick.j;
            }

            // Output action
            if (targetI != -1)
            {
                Console.WriteLine($"{targetI} {targetJ}");
                // Update state (Simplified simulation)
                long cost = C[targetI][targetJ] * (P[targetI, targetJ] + 1);
                currentApples -= cost;
                P[targetI, targetJ]++;
            }
            else
            {
                Console.WriteLine("-1");
            }

            // Simulate Turn Production (Crucial to track Apples)
            // Level 0 -> Apples
            // Level >= 1 -> Lower Level Machines
            
            // Level 0
            for (int j = 0; j < N; j++)
            {
                // Note: A[j] is for Level 0 machines
                currentApples += A[j] * B[0, j] * P[0, j]; // Wait, P is power? Problem says A * B * P?
                // Problem says: "Level 0: Increase apples by A_j * B_{0,j} * P_{0,j}"
                // Wait, P starts at 0. If P is 0, produce 0? 
                // "Level 0... Apples increase by A_j * B_{0,j} * P_{0,j}"
                // If initial P is 0, we produce nothing.
                // We MUST upgrade P to at least 1 to produce anything.
                // Ah, the problem says "Enhance: Pay ... to increase P by 1".
                // So P=0 means 0 multiplier. We need to upgrade immediately.
            }
            
            // Level 1..L-1
            for (int i = 1; i < L; i++)
            {
                for (int j = 0; j < N; j++)
                {
                     B[i - 1, j] += B[i, j] * P[i, j];
                }
            }
        }
    }
}