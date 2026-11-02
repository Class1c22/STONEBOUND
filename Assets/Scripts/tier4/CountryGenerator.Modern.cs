using System.Collections.Generic;
using UnityEngine;

// Сучасні королівства для CountryGenerator: мегаполіси, хмарочоси, асфальт, ліхтарі, передмістя.
// Поклади поруч із CountryGenerator.cs (клас став partial).
public partial class CountryGenerator
{
    [Header("Сучасні королівства")]
    [Tooltip("Імовірність, що острів сучасний (стартовий острів завжди середньовічний)")]
    [Range(0f, 1f)] public float modernChance = 0.5f;
    public int modernWidthMin = 5;
    public int modernWidthMax = 8;
    public int modernDepthMin = 5;
    public int modernDepthMax = 7;
    [Tooltip("Висота центрального хмарочоса столиці (вокселі)")]
    public int capitalTowerHeight = 36;
    [Tooltip("Висота центральної вежі звичайного міста")]
    public int townTowerHeight = 22;
    [Tooltip("Максимальна висота кварталів у центрі столиці. У місті ~60%, у селі низькі будинки")]
    public int downtownMaxHeight = 20;
    [Tooltip("Крок ліхтарів уздовж вулиць")]
    public int lampSpacing = 9;

    byte[] mWall, mBrick, mGlassSet, mAsphalt;
    byte mRoof, mLampPole, mLampHead;

    void InitModernColors()
    {
        mWall = new[] { C(200, 200, 205), C(182, 184, 192), C(215, 210, 200) };
        mBrick = new[] { C(160, 110, 90), C(150, 100, 82), C(168, 120, 98) };
        mGlassSet = new[] { C(90, 140, 190), C(110, 160, 205), C(70, 120, 170) };
        mAsphalt = new[] { C(70, 72, 76), C(64, 66, 70), C(80, 82, 86) };
        mRoof = C(58, 62, 70);
        mLampPole = C(70, 72, 78);
        mLampHead = C(255, 235, 150);
    }

    byte ModernRoadColor(int kind, int x, int z)
    {
        float r = Hash01(x, 6, z);
        return mAsphalt[r < 0.15f ? 2 : (r < 0.55f ? 0 : 1)];
    }

    // ---------- планування сучасного міста ----------

    void PlanModernCity(City cy, System.Random rng)
    {
        int cid = cur.colorIdx;
        int rw = Mathf.Max(1, roadWidth);
        int rowSp = rw + 2 * (modernDepthMax + 2);
        int rr = cy.r;

        // центральна споруда
        if (cy.capital)
        {
            MarkOcc(new RectInt(cy.cx - 12, cy.cz - 12, 24, 24));
            int ccx = cy.cx, ccz = cy.cz;
            AddJob(ccx, ccz, () => BuildSkyscraper(ccx, ccz, cid, true));
        }
        else if (!cy.village)
        {
            MarkOcc(new RectInt(cy.cx - 5, cy.cz - 5, 10, 10));
            int tcx = cy.cx, tcz = cy.cz;
            AddJob(tcx, tcz, () => BuildSkyscraper(tcx, tcz, cid, false));
        }
        else
        {
            MarkOcc(new RectInt(cy.cx - 4, cy.cz - 4, 8, 8));
            int vcx = cy.cx, vcz = cy.cz;
            AddJob(vcx, vcz, () => BuildFountain(vcx, vcz));
        }

        int K = rr / rowSp;

        // горизонтальні вулиці + ліхтарі
        for (int k = -K; k <= K; k++)
        {
            int zs = cy.cz + k * rowSp - rw / 2;
            int dz = zs + rw / 2 - cy.cz;
            float hl2 = (float)rr * rr - (float)dz * dz;
            if (hl2 <= 0f) continue;
            int hl = Mathf.FloorToInt(Mathf.Sqrt(hl2));
            for (int x = cy.cx - hl; x <= cy.cx + hl; x++)
            for (int t = 0; t < rw; t++)
                MarkRoad(x, zs + t, 1);

            int ls = Mathf.Max(5, lampSpacing);
            for (int x = cy.cx - hl + 4; x <= cy.cx + hl - 4; x += ls)
            {
                int lx = x, lz = zs;
                AddJob(lx, lz, () => BuildLamp(lx, lz));
            }
        }

        // поперечні вулиці
        int cs = Mathf.Max(8, crossStreetSpacing);
        int J = rr / cs;
        for (int j = -J; j <= J; j++)
        {
            int xs = cy.cx + j * cs - rw / 2;
            int dx = xs + rw / 2 - cy.cx;
            float vl2 = (float)rr * rr - (float)dx * dx;
            if (vl2 <= 0f) continue;
            int vl = Mathf.FloorToInt(Mathf.Sqrt(vl2));
            for (int z = cy.cz - vl; z <= cy.cz + vl; z++)
            for (int t = 0; t < rw; t++)
                MarkRoad(xs + t, z, 1);
        }

        // будинки з обох боків кожної вулиці
        for (int k = -K; k <= K; k++)
        {
            int zs = cy.cz + k * rowSp - rw / 2;
            int dz = zs + rw / 2 - cy.cz;
            float hl2 = (float)rr * rr - (float)dz * dz;
            if (hl2 <= 0f) continue;
            int hl = Mathf.FloorToInt(Mathf.Sqrt(hl2));

            for (int side = -1; side <= 1; side += 2)
            {
                int x = cy.cx - hl + rng.Next(0, 3);
                while (x < cy.cx + hl - modernWidthMin)
                {
                    int w = RI(rng, modernWidthMin, modernWidthMax);
                    int d = RI(rng, modernDepthMin, modernDepthMax);
                    int ox = x, oz, f;
                    if (side > 0) { oz = zs + rw + 1; f = 0; }
                    else { oz = zs - 1 - d; f = 2; }

                    RectInt foot = new RectInt(ox - 1, oz - 1, w + 2, d + 2);
                    if (rng.NextDouble() < houseChance && RectClear(foot))
                    {
                        MarkOcc(foot);

                        // висота: вища ближче до центру
                        float ddx = ox + w * 0.5f - cy.cx, ddz = oz + d * 0.5f - cy.cz;
                        float t01 = Mathf.Clamp01(Mathf.Sqrt(ddx * ddx + ddz * ddz) / Mathf.Max(1, rr));
                        int h;
                        if (cy.village) h = RI(rng, 3, 4);
                        else
                        {
                            float maxH = cy.capital ? downtownMaxHeight : downtownMaxHeight * 0.6f;
                            h = Mathf.Max(4, Mathf.RoundToInt(maxH * Mathf.Pow(1f - t01, 1.2f)) + rng.Next(0, 4));
                        }

                        int hox = ox, hoz = oz, hf = f, hw = w, hd = d, hh = h;
                        AddJob(ox + w / 2, oz + d / 2, () => BuildModernBuilding(hf, hox, hoz, hw, hd, hh, cid));
                    }
                    x += w + Mathf.Max(1, houseGap) + rng.Next(0, 2);
                }
            }
        }

        // зелень: дерева, кущі, квіти між будинками
        if (gardenDensity > 0f)
        {
            const int B = 6;
            for (int gz = -rr; gz <= rr; gz += B)
            for (int gx = -rr; gx <= rr; gx += B)
            {
                float cdx = gx + B * 0.5f, cdz = gz + B * 0.5f;
                if (cdx * cdx + cdz * cdz > (rr + 4f) * (rr + 4f)) continue;
                int bx0 = cy.cx + gx, bz0 = cy.cz + gz;
                AddJob(bx0 + B / 2, bz0 + B / 2, () => BuildGarden(bx0, bz0, B));
            }
        }
    }

    // ---------- будівлі ----------

    // Звичайна сучасна будівля: стіни з вікнами, плаский дах із парапетом, навіс над входом
    void BuildModernBuilding(int f, int ox, int oz, int w, int d, int h, int cIdx)
    {
        var rng = new System.Random(Hash(seed ^ 0x6D6, ox, oz));
        SetT(f, ox, oz, w, d);
        int style = rng.Next(3);   // 0 бетон, 1 цегла, 2 скляний офіс
        byte[] wallSrc = style == 1 ? mBrick : mWall;
        byte accent = flagSet[(cIdx + rng.Next(0, 3)) % flagSet.Length];

        // деталі першими: двері й навіс
        PH(w / 2, 1, 0, cDoor);
        PH(w / 2, 2, 0, cDoor);
        if (h >= 4 && w >= 5)
            for (int lx = 1; lx < w - 1; lx++) PH(lx, 3, -1, accent);

        // стіни
        for (int y = 0; y < h; y++)
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
        {
            if (lx != 0 && lx != w - 1 && lz != 0 && lz != d - 1) continue;
            bool corner = (lx == 0 || lx == w - 1) && (lz == 0 || lz == d - 1);
            byte id;
            if (y == 0) id = cStone2;
            else if (corner) id = Pick(wallSrc, Hash(lx, y, lz));
            else
            {
                bool win;
                if (style == 2) win = y % 3 != 0;
                else
                {
                    int along = (lx == 0 || lx == w - 1) ? lz : lx;
                    win = y >= 2 && (y & 1) == 0 && (along & 1) == 1;
                }
                id = win ? Pick(mGlassSet, Hash(lx, y, lz)) : Pick(wallSrc, Hash(lx, y, lz));
            }
            PH(lx, y, lz, id);
        }

        // парапет, дах, надбудова
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
            if (lx == 0 || lx == w - 1 || lz == 0 || lz == d - 1)
                PH(lx, h + 1, lz, Pick(wallSrc, Hash(lx, h, lz)));
        if (rng.NextDouble() < 0.5 && w >= 5 && d >= 5)
            PBox(1, h + 1, 1, 2, 1, 2, mWall[1]);
        PBox(0, h, 0, w, 1, d, mRoof);
        PBox(0, 0, 0, w, 1, d, cFloor);
    }

    // Порожній ярус башти (стіни зі скла й бетонних смуг)
    void TowerShell(int S, int ins, int y0, int y1)
    {
        int lo = ins, hi = S - 1 - ins;
        for (int y = y0; y < y1; y++)
        for (int lz = lo; lz <= hi; lz++)
        for (int lx = lo; lx <= hi; lx++)
        {
            if (lx != lo && lx != hi && lz != lo && lz != hi) continue;
            bool corner = (lx == lo || lx == hi) && (lz == lo || lz == hi);
            byte id;
            if (y == 0) id = cStone2;
            else if (corner || y % 3 == 0) id = Pick(mWall, Hash(lx, y, lz));
            else id = Pick(mGlassSet, Hash(lx, y, lz));
            PH(lx, y, lz, id);
        }
        PBox(lo, y1, lo, hi - lo + 1, 1, hi - lo + 1, mRoof);
    }

    // Хмарочос: подіум, основна вежа, верхній ярус, антена з прапором королівства
    void BuildSkyscraper(int cx, int cz, int cIdx, bool capital)
    {
        int S = capital ? 15 : 9;
        SetT(0, cx - S / 2, cz - S / 2, S, S);
        int H = capital ? capitalTowerHeight : townTowerHeight;
        int podH = capital ? 6 : 4;
        int topIns = capital ? 4 : 3;
        int topH = capital ? 10 : 6;
        int mid = S / 2;
        byte flag = flagSet[cIdx % flagSet.Length];

        // двері
        for (int lx = mid - (capital ? 1 : 0); lx <= mid + (capital ? 1 : 0); lx++)
        {
            PH(lx, 1, 0, cDoor);
            PH(lx, 2, 0, cDoor);
        }

        // антена й прапор
        int topY = H + topH;
        for (int y = topY + 1; y <= topY + 9; y++) PH(mid, y, mid, mLampPole);
        for (int y = topY + 6; y <= topY + 8; y++)
        for (int lx = mid + 1; lx <= mid + 4; lx++)
            PH(lx, y, mid, flag);

        // яруси зверху вниз (раніше поставлене не перезаписується)
        TowerShell(S, topIns, H, topY);
        TowerShell(S, 2, podH, H);
        TowerShell(S, 0, 0, podH);
        PBox(0, 0, 0, S, 1, S, cFloor);
    }

    // Фонтан у центрі сучасного села
    void BuildFountain(int cx, int cz)
    {
        Disc(cx, cz, 1.6f, 1, 2, cWater);
        Add(cx, 2, cz, cStone);
        Add(cx, 3, cz, cWater);
        Disc(cx, cz, 2.8f, 0, 2, cStone);
        Disc(cx, cz, 3.6f, 0, 1, cStone2);
    }

    // Вуличний ліхтар
    void BuildLamp(int x, int z)
    {
        Add(x, 4, z, mLampHead);
        for (int y = 0; y < 4; y++) Add(x, y, z, mLampPole);
    }
}