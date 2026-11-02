using System.Collections.Generic;
using UnityEngine;

// Малі острівці й об'єкти в морі для CountryGenerator.
// Усе живе в "смузі" навколо великого острова (isletBand), тож плитки будуються тією самою системою.
public partial class CountryGenerator
{
    [Header("Малі острівці")]
    [Tooltip("Максимум острівців навколо кожного великого острова")]
    public int isletsPerIsland = 7;
    public int isletRadiusMin = 5;
    [Tooltip("Маяк ставиться на острівцях із радіусом від 10")]
    public int isletRadiusMax = 13;
    [Tooltip("Смуга моря навколо острова (вокселі), де з'являються острівці й об'єкти. Трохи зменшує максимальний радіус великого острова")]
    public int isletBand = 90;
    [Tooltip("Мінімальний просвіт моря між великим островом і острівцем")]
    public int isletGap = 14;

    [Header("Об'єкти в морі")]
    [Tooltip("Буї, скелі, човни й уламки кораблів на кожен острів")]
    public int seaObjectsPerIsland = 10;

    // ---------- планування ----------

    // Додає в суші острівця малі острови (викликати ДО ComputeLandDist)
    List<Vector3Int> AddIslets(Island s)
    {
        var list = new List<Vector3Int>();   // x, z, радіус
        if (isletsPerIsland <= 0) return list;

        int S = s.S;
        var rng = new System.Random(Hash(seed ^ 0x7E1, s.region.x * 6151 + 5, s.region.y * 3571 + 9));
        float mainOut = s.R * (1f + coastJitterFrac) + isletGap;
        int target = rng.Next(isletsPerIsland / 2, isletsPerIsland + 1);
        int rMin = Mathf.Max(4, isletRadiusMin);
        int rMax = Mathf.Max(rMin, isletRadiusMax);

        for (int tries = 0; tries < 300 && list.Count < target; tries++)
        {
            int r = RI(rng, rMin, rMax);
            int pad = Mathf.CeilToInt(r * 1.3f) + 3;
            if (S <= pad * 2 + 2) continue;
            int lx = rng.Next(pad, S - pad), lz = rng.Next(pad, S - pad);
            int x = s.ox + lx, z = s.oz + lz;

            float dx = x - s.cx, dz = z - s.cz;
            if (Mathf.Sqrt(dx * dx + dz * dz) < mainOut + r * 1.3f) continue;

            bool ok = true;
            for (int k = 0; k < list.Count; k++)
            {
                Vector3Int e = list[k];
                float ex = x - e.x, ez = z - e.y;
                float need = r + e.z + isletGap * 0.5f + 4f;
                if (ex * ex + ez * ez < need * need) { ok = false; break; }
            }
            if (!ok) continue;

            float nx = 100f + (float)rng.NextDouble() * 1000f;
            float nz = 100f + (float)rng.NextDouble() * 1000f;
            int ext = pad - 2;
            for (int zz = -ext; zz <= ext; zz++)
            for (int xx = -ext; xx <= ext; xx++)
            {
                float d = Mathf.Sqrt(xx * xx + zz * zz);
                float n = Mathf.PerlinNoise(xx * 0.15f + nx, zz * 0.15f + nz) * 2f - 1f;
                if (d + n * r * 0.3f >= r) continue;
                int i = (lx + xx) + S * (lz + zz);
                s.land[i] = true;
                s.occ[i] = true;     // великі будівлі й поля сюди не лізуть
            }
            list.Add(new Vector3Int(x, z, r));
        }
        return list;
    }

    // Роботи для острівців і морських об'єктів (викликати ПІСЛЯ ComputeLandDist, коли cur == s)
    void PlanIsletsAndSea(Island s, List<Vector3Int> islets)
    {
        var rng = new System.Random(Hash(seed ^ 0x5EA, s.region.x * 2711 + 3, s.region.y * 1999 + 11));

        for (int k = 0; k < islets.Count; k++)
        {
            int ix = islets[k].x, iz = islets[k].y, ir = islets[k].z, sd = rng.Next();
            AddJob(ix, iz, () => BuildIslet(ix, iz, ir, sd));
        }

        // об'єкти у відкритій воді
        var placed = new List<Vector3Int>();
        int count = Mathf.Max(0, seaObjectsPerIsland);
        for (int tries = 0; tries < count * 20 && placed.Count < count; tries++)
        {
            if (s.S < 40) break;
            int lx = rng.Next(8, s.S - 8), lz = rng.Next(8, s.S - 8);
            int x = s.ox + lx, z = s.oz + lz;

            double roll = rng.NextDouble();
            int kind = roll < 0.35 ? 0 : roll < 0.6 ? 1 : roll < 0.85 ? 2 : 3;
            int rad = kind == 0 ? 1 : kind == 1 ? 3 : 5;
            if (!OpenSea(s, x, z, rad + 4)) continue;

            bool ok = true;
            for (int k = 0; k < placed.Count; k++)
            {
                float ex = x - placed[k].x, ez = z - placed[k].y;
                float need = rad + placed[k].z + 6f;
                if (ex * ex + ez * ez < need * need) { ok = false; break; }
            }
            if (!ok) continue;
            placed.Add(new Vector3Int(x, z, rad));

            int px = x, pz = z, sd = rng.Next(), pf = rng.Next(0, 4);
            switch (kind)
            {
                case 0: AddJob(px, pz, () => BuildBuoy(px, pz)); break;
                case 1: AddJob(px, pz, () => BuildSeaStack(px, pz, sd)); break;
                case 2: AddJob(px, pz, () => BuildSailboat(px, pz, pf)); break;
                default: AddJob(px, pz, () => BuildWreck(px, pz, pf)); break;
            }
        }
    }

    // Чи немає суші поблизу (перевіряється по масиву острова)
    bool OpenSea(Island s, int x, int z, int r)
    {
        int S = s.S;
        for (int k = -1; k < 12; k++)
        for (int ring = 0; ring < 2; ring++)
        {
            float a = k * (Mathf.PI * 2f / 12f);
            float rad = k < 0 ? 0f : (ring == 0 ? r : r * 0.5f);
            int px = x + Mathf.RoundToInt(Mathf.Cos(a) * rad) - s.ox;
            int pz = z + Mathf.RoundToInt(Mathf.Sin(a) * rad) - s.oz;
            if (px < 0 || pz < 0 || px >= S || pz >= S) continue;
            if (s.land[px + S * pz]) return false;
        }
        return true;
    }

    // Суша на воксельній позиції з відстанню до води не менше minDist
    bool LandAt(int x, int z, int minDist)
    {
        Island s = IslandAt(x, z);
        if (s == null || s.land == null) return false;
        int i = (x - s.ox) + s.S * (z - s.oz);
        return s.land[i] && s.dist[i] >= minDist;
    }

    // ---------- острівець ----------

    void BuildIslet(int cx, int cz, int r, int sd)
    {
        var rng = new System.Random(sd);
        bool lighthouse = false;

        if (r >= 10 && LandAt(cx, cz, 6))
        {
            BuildLighthouse(cx, cz);
            lighthouse = true;
        }
        else if (r >= 6 && rng.NextDouble() < 0.45 && LandAt(cx, cz, 3) && LandAt(cx + 1, cz, 3))
        {
            // скриня зі скарбом
            Add(cx, 1, cz, cSun); Add(cx + 1, 1, cz, cSun);
            Add(cx, 0, cz, cDoor); Add(cx + 1, 0, cz, cDoor);
        }

        // пальми
        int palms = Mathf.Max(1, r / 3) + rng.Next(0, 2);
        for (int n = 0; n < palms; n++)
        {
            float ang = (float)rng.NextDouble() * 6.2832f;
            float dist = Mathf.Lerp(0.15f, 0.7f, (float)rng.NextDouble()) * r;
            int px = cx + Mathf.RoundToInt(Mathf.Cos(ang) * dist);
            int pz = cz + Mathf.RoundToInt(Mathf.Sin(ang) * dist);
            if (lighthouse && Mathf.Abs(px - cx) < 5 && Mathf.Abs(pz - cz) < 5) continue;
            if (!LandAt(px, pz, 3)) continue;
            BuildPalm(px, pz, rng);
        }

        // камінці
        for (int n = rng.Next(1, 3); n > 0; n--)
        {
            int px = cx + rng.Next(-r / 2, r / 2 + 1), pz = cz + rng.Next(-r / 2, r / 2 + 1);
            if (!LandAt(px, pz, 2)) continue;
            Add(px, 0, pz, cStone);
            if (rng.NextDouble() < 0.5) Add(px, 1, pz, cStone2);
            if (LandAt(px + 1, pz, 2)) Add(px + 1, 0, pz, cStone2);
        }

        // трава й квіти
        int e = r + 2;
        for (int z = cz - e; z <= cz + e; z++)
        for (int x = cx - e; x <= cx + e; x++)
        {
            float h = Hash01(x, 61, z);
            if (h > 0.15f) continue;
            if (!LandAt(x, z, 4)) continue;
            Add(x, 0, z, h < 0.04f ? Pick(flowerColors, Hash(x, 62, z)) : Pick(grassSet, Hash(x, 63, z)));
        }
    }

    void BuildPalm(int x, int z, System.Random rng)
    {
        int h = RI(rng, 4, 6);
        byte[] lv = leafSets[1];

        // крона першою
        Add(x, h, z, lv[0]);
        for (int d = 0; d < 4; d++)
        {
            int dx = d == 0 ? 1 : d == 1 ? -1 : 0;
            int dz = d == 2 ? 1 : d == 3 ? -1 : 0;
            Add(x + dx, h, z + dz, lv[1]);
            Add(x + 2 * dx, h, z + 2 * dz, lv[0]);
            Add(x + 3 * dx, h - 1, z + 3 * dz, lv[2]);
        }
        for (int sx = -1; sx <= 1; sx += 2)
        for (int sz = -1; sz <= 1; sz += 2)
        {
            Add(x + sx, h - 1, z + sz, lv[1]);
            Add(x + 2 * sx, h - 2, z + 2 * sz, lv[2]);
        }

        for (int y = 0; y < h; y++)
            Add(x, y, z, (y & 1) == 0 ? cBarkA : cBarkB);
    }

    void BuildLighthouse(int cx, int cz)
    {
        const int H = 11;
        byte white = wallSet[0], red = flagSet[0];

        // верх першим
        Add(cx, H + 5, cz, cPost);
        Disc(cx, cz, 0.9f, H + 4, H + 5, cCap);
        Disc(cx, cz, 1.9f, H + 3, H + 4, cCap);
        Add(cx, H + 1, cz, mLampHead);
        Add(cx, H + 2, cz, mLampHead);
        Disc(cx, cz, 1.6f, H + 1, H + 3, cGlass);
        Disc(cx, cz, 2.4f, H, H + 1, cStone2);

        // тіло зі смугами
        for (int y = 2; y < H; y++)
            Disc(cx, cz, 2.6f - y * 0.12f, y, y + 1, ((y / 2) & 1) == 0 ? white : red);

        Disc(cx, cz, 3.4f, 0, 2, cStone);
    }

    // ---------- об'єкти в морі ----------

    void BuildBuoy(int x, int z)
    {
        Add(x, 4, z, mLampHead);
        Add(x, 3, z, cPost);
        Add(x, 2, z, flagSet[0]);
        Add(x, 1, z, wallSet[0]);
        Add(x, 0, z, flagSet[0]);
    }

    void BuildSeaStack(int x, int z, int sd)
    {
        var rng = new System.Random(sd);
        int h = RI(rng, 4, 9);
        float r = RI(rng, 2, 3);

        // шапка з трави
        Disc(x, z, 0.9f, h, h + 1, grassSet[0]);
        if (rng.NextDouble() < 0.5) Add(x, h + 1, z, leafSets[0][0]);

        for (int y = 0; y < h; y++)
        {
            float rad = Mathf.Max(0.8f, r + 0.6f - y * (r / h) * 0.8f);
            Disc(x, z, rad, y, y + 1, Hash01(x, y, z) < 0.5f ? cStone : cStone2);
        }

        // піна біля основи
        Disc(x, z, r + 1.7f, 0, 1, wallSet[2]);
    }

    // Човен лежить уздовж lz: 3 вокселі завширшки, 7 завдовжки
    void SetBoatT(int x, int z, int f)
    {
        if ((f & 1) == 0) SetT(f, x - 1, z - 3, 3, 7);
        else SetT(f, x - 3, z - 1, 3, 7);
    }

    void BuildSailboat(int x, int z, int f)
    {
        SetBoatT(x, z, f);
        byte flag = flagSet[Mathf.Abs(Hash(x, 77, z)) % flagSet.Length];

        // прапорець, щогла, вітрило
        PH(1, 9, 3, flag);
        for (int y = 1; y <= 8; y++) PH(1, y, 3, cPost);
        for (int y = 2; y <= 7; y++)
        {
            int n = (8 - y) / 2;
            for (int lz = 4; lz <= 3 + n; lz++) PH(1, y, lz, wallSet[0]);
        }

        // корпус
        for (int lz = 0; lz < 7; lz++)
        for (int lx = 0; lx < 3; lx++)
        {
            if (lz == 6 && lx != 1) continue;
            PH(lx, 0, lz, cPost);
            bool edge = lx == 0 || lx == 2 || lz == 0 || lz == 6;
            if (edge) PH(lx, 1, lz, cDoor);
        }
    }

    void BuildWreck(int x, int z, int f)
    {
        SetBoatT(x, z, f);

        // зламана щогла
        for (int y = 1; y <= 3; y++) PH(1, y, 3, cBarkB);

        for (int lz = 0; lz < 7; lz++)
        for (int lx = 0; lx < 3; lx++)
        {
            if (lz == 6 && lx != 1) continue;
            if (Hash01(x + lx, 78, z + lz) > 0.15f) PH(lx, 0, lz, cBarkB);
            bool edge = lx == 0 || lx == 2 || lz == 0 || lz == 6;
            if (edge && Hash01(x + lx, 79, z + lz) > 0.5f) PH(lx, 1, lz, cBarkA);
        }
    }
}