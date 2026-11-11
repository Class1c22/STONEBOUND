using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Повісь на Image з картинкою трубки (твій спрайт). Усередині трубки з'являються піксельні квадратики:
// з прогресом стадії (RockAbsorber.StageProgress) нові пікселі падають зверху й складаються рядами.
// Колір кожного пікселя = колір фігури, яку камінь всмоктав (RockAbsorber.PieceAbsorbed).
// Після R верхні пікселі зникають. На переході на нову стадію трубка висипається і наповнюється заново.
//
// Налаштування:
//  1) Canvas (Screen Space - Overlay). На ньому Image зі спрайтом трубки (Filter Mode: Point, Compression: None).
//  2) Повісь цей скрипт на цей Image. Підбери Pad Left/Right/Top/Bottom, щоб пікселі лягали рівно всередину контуру.
//  3) Щоб бар пережив завантаження сцен, постав Canvas у TierTransition -> Persistent UI.
// Зміни Columns/Pad у грі застосовуються самі (або ПКМ по компоненту -> Rebuild).
[RequireComponent(typeof(RectTransform))]
public class PixelTubeBar : MonoBehaviour
{
    [Header("Джерело")]
    [Tooltip("Камінь. Порожньо = знайти самому (шукає, поки не знайде)")]
    public RockAbsorber rock;

    [Header("Пікселі")]
    [Tooltip("Скільки пікселів у ширину. Розмір піксельного квадратика = внутрішня ширина / це число")]
    [Min(1)] public int columns = 4;

    [Header("Відступи від країв картинки до внутрішньої частини трубки (в одиницях Canvas). Підбери під свій спрайт")]
    public int padLeft = 4;
    public int padRight = 4;
    public int padTop = 10;
    public int padBottom = 10;

    [Header("Кольори")]
    [Tooltip("Пікселі беруть колір всмоктаних фігур. Вимкнено: випадкові кольори з палітри")]
    public bool useAbsorbedColors = true;
    [Tooltip("Скільки кольорів максимум чекає в черзі")]
    public int maxQueuedColors = 64;
    [Tooltip("Кольори пікселів (вибираються випадково, коли немає кольору всмоктаної фігури). Порожньо = стандартні кольори уламків")]
    public Color[] palette;
    [Tooltip("Випадкова зміна яскравості пікселя")]
    [Range(0f, 0.3f)] public float brightnessJitter = 0.08f;

    [Header("Падіння")]
    [Tooltip("Гравітація в висотах трубки за секунду². 3 = піксель пролітає всю трубку приблизно за 0.8 с")]
    public float gravity = 3f;
    [Tooltip("Мінімальна пауза між появою пікселів, секунд (щоб падали по одному, а не купою)")]
    public float spawnInterval = 0.04f;
    [Tooltip("Звідки падають: над верхом трубки на стільки пікселів (випадково від 0.5 до цього)")]
    public float spawnHeightPixels = 3f;
    [Tooltip("Коли стадія змінилась, пікселі висипаються вниз. Вимкнено: зникають одразу")]
    public bool drainOnStageChange = true;
    [Tooltip("Затримка висипання між рядами, секунд")]
    public float drainRowDelay = 0.012f;

    // ---------- внутрішнє ----------

    enum State { Falling, Landed, Draining }

    class Px
    {
        public RectTransform rt;
        public Image img;
        public float x, y, vy, targetY, delay;
        public State state;
    }

    RectTransform root, container;
    float innerW, innerH, size;
    int rows, cells, placed;
    int[] heights;
    Px[][] grid;
    readonly List<Px> pixels = new List<Px>();
    readonly List<int> candidates = new List<int>();
    readonly Queue<Color> colorQueue = new Queue<Color>();
    RockAbsorber subscribedRock;

    Vector2 lastRect;
    int lastColumns;
    int lastPadL, lastPadR, lastPadT, lastPadB;
    float spawnTimer, findTimer;
    int lastStage = -1;
    float baseline;   // прогрес каменя на початку рівня: від нього бар рахується з нуля
    int lastScene = -1;
    bool synced;   // чи вже виставлено початковий стан без анімації

    void Awake()
    {
        root = (RectTransform)transform;

        var go = new GameObject("Pixels", typeof(RectTransform));
        container = (RectTransform)go.transform;
        container.SetParent(root, false);
        container.anchorMin = Vector2.zero;
        container.anchorMax = Vector2.one;
        container.pivot = new Vector2(0.5f, 0.5f);
    }

    void OnEnable()
    {
        lastRect = Vector2.zero;   // примусово перебудувати при вмиканні
    }

    void OnDisable()
    {
        Unsubscribe();
    }

    void Unsubscribe()
    {
        if (subscribedRock != null) subscribedRock.PieceAbsorbed -= OnPieceAbsorbed;
        subscribedRock = null;
    }

    void OnPieceAbsorbed(Color c)
    {
        if (!useAbsorbedColors) return;
        colorQueue.Enqueue(new Color(c.r, c.g, c.b, 1f));
        while (colorQueue.Count > maxQueuedColors) colorQueue.Dequeue();
    }

    [ContextMenu("Rebuild")]
    void Rebuild()
    {
        // прибираємо старі пікселі
        foreach (var p in pixels)
            if (p != null && p.rt != null) Destroy(p.rt.gameObject);
        pixels.Clear();
        placed = 0;
        synced = false;

        container.offsetMin = new Vector2(padLeft, padBottom);
        container.offsetMax = new Vector2(-padRight, -padTop);

        innerW = container.rect.width;
        innerH = container.rect.height;
        columns = Mathf.Max(1, columns);

        size = Mathf.Max(1f, Mathf.Floor(innerW / columns));
        rows = Mathf.FloorToInt(innerH / size);
        cells = rows > 0 ? columns * rows : 0;

        heights = new int[columns];
        grid = new Px[columns][];
        for (int c = 0; c < columns; c++) grid[c] = new Px[Mathf.Max(0, rows)];

        lastRect = root.rect.size;
        lastColumns = columns;
        lastPadL = padLeft; lastPadR = padRight; lastPadT = padTop; lastPadB = padBottom;
    }

    bool LayoutChanged()
    {
        return root.rect.size != lastRect || columns != lastColumns
            || padLeft != lastPadL || padRight != lastPadR
            || padTop != lastPadT || padBottom != lastPadB;
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;

        if (LayoutChanged()) Rebuild();

        if (rock == null)
        {
            findTimer -= dt;
            if (findTimer <= 0f)
            {
                findTimer = 0.5f;
                rock = FindFirstObjectByType<RockAbsorber>();
            }
        }

        // підписка на події каменя (камінь може змінитись після завантаження сцени)
        if (rock != subscribedRock)
        {
            Unsubscribe();
            colorQueue.Clear();
            if (rock != null)
            {
                rock.PieceAbsorbed += OnPieceAbsorbed;
                subscribedRock = rock;
            }
        }

        if (rock != null && cells > 0)
        {
            // новий рівень (сцена): бар починається порожнім
            int sceneId = SceneManager.GetActiveScene().handle;
            if (sceneId != lastScene)
            {
                lastScene = sceneId;
                lastStage = rock.Stage;
                baseline = rock.StageProgress;
                colorQueue.Clear();
                if (drainOnStageChange) DrainAll(); else ClearAll();
            }

            // нова стадія: трубку скидаємо
            if (lastStage < 0) lastStage = rock.Stage;
            if (rock.Stage != lastStage)
            {
                lastStage = rock.Stage;
                baseline = 0f;
                colorQueue.Clear();
                if (drainOnStageChange) DrainAll(); else ClearAll();
            }

            // прогрес упав (R): точка відліку не може бути вище за поточний прогрес
            float prog = rock.StageProgress;
            if (prog < baseline) baseline = prog;
            float shown = baseline >= 1f ? 0f : (prog - baseline) / (1f - baseline);

            int wanted = Mathf.Clamp(Mathf.FloorToInt(shown * cells + 0.0001f), 0, cells);

            // перший запуск (або після Rebuild): початковий стан ставимо одразу, без падіння
            if (!synced)
            {
                synced = true;
                while (placed < wanted) Spawn(true);
            }

            // прогрес впав (R): знімаємо верхні пікселі
            while (placed > wanted) RemoveTop();

            // прогрес виріс: по одному пікселю з паузою
            spawnTimer = Mathf.Max(0f, spawnTimer - dt);
            if (placed < wanted && spawnTimer <= 0f)
            {
                Spawn();
                spawnTimer = spawnInterval;
            }
        }

        Step(dt);
    }

    // ---------- пікселі ----------

    Color PickColor()
    {
        Color c;
        if (palette != null && palette.Length > 0)
            c = palette[Random.Range(0, palette.Length)];
        else
        {
            Color[] def =
            {
                new Color32(150, 150, 146, 255), new Color32(124, 124, 120, 255), new Color32(128, 100, 70, 255),
                new Color32(110, 84, 58, 255), new Color32(95, 160, 70, 255), new Color32(190, 70, 56, 255),
                new Color32(222, 196, 130, 255), new Color32(70, 110, 170, 255)
            };
            c = def[Random.Range(0, def.Length)];
        }
        float k = 1f + Random.Range(-brightnessJitter, brightnessJitter);
        return new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), 1f);
    }

    void Spawn(bool instant = false)
    {
        // найнижча колонка (рівномірне наповнення), серед рівних вибір випадковий
        int min = int.MaxValue;
        for (int c = 0; c < columns; c++) min = Mathf.Min(min, heights[c]);
        if (min >= rows) return;

        candidates.Clear();
        for (int c = 0; c < columns; c++)
            if (heights[c] == min) candidates.Add(c);
        int col = candidates[Random.Range(0, candidates.Count)];
        int row = heights[col]++;
        placed++;

        var go = new GameObject("px", typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(container, false);
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        rt.sizeDelta = new Vector2(size, size);

        var img = go.GetComponent<Image>();
        // колір всмоктаної фігури з черги; якщо черга порожня (або стартовий стан) - випадковий з палітри
        img.color = (!instant && colorQueue.Count > 0) ? colorQueue.Dequeue() : PickColor();
        img.raycastTarget = false;
        img.enabled = false;   // покажемо, коли піксель буде всередині трубки

        float offsetX = Mathf.Round((innerW - columns * size) * 0.5f);
        var p = new Px
        {
            rt = rt,
            img = img,
            x = offsetX + col * size,
            y = instant ? row * size : innerH + size * Random.Range(0.5f, Mathf.Max(0.5f, spawnHeightPixels)),
            targetY = row * size,
            state = instant ? State.Landed : State.Falling
        };
        img.enabled = instant;
        rt.anchoredPosition = new Vector2(p.x, p.y);

        grid[col][row] = p;
        pixels.Add(p);
    }

    void RemoveTop()
    {
        int best = -1, bestH = 0;
        for (int c = 0; c < columns; c++)
            if (heights[c] > bestH) { bestH = heights[c]; best = c; }
        if (best < 0) { placed = 0; return; }

        int row = heights[best] - 1;
        Px p = grid[best][row];
        grid[best][row] = null;
        heights[best]--;
        placed--;

        if (p != null)
        {
            pixels.Remove(p);
            if (p.rt != null) Destroy(p.rt.gameObject);
        }
    }

    // Пікселі зникають одразу
    void ClearAll()
    {
        foreach (var p in pixels)
            if (p != null && p.rt != null) Destroy(p.rt.gameObject);
        pixels.Clear();
        ResetGrid();
    }

    // Пікселі висипаються вниз з трубки, нижні ряди першими; трубка одразу вільна для нових
    void DrainAll()
    {
        for (int c = 0; c < columns; c++)
        {
            for (int r = 0; r < grid[c].Length; r++)
            {
                Px p = grid[c][r];
                if (p == null) continue;
                p.state = State.Draining;
                p.vy = 0f;
                p.delay = r * drainRowDelay + Random.Range(0f, 0.05f);
            }
        }
        ResetGrid();
    }

    void ResetGrid()
    {
        placed = 0;
        for (int c = 0; c < columns; c++)
        {
            heights[c] = 0;
            for (int r = 0; r < grid[c].Length; r++) grid[c][r] = null;
        }
    }

    void Step(float dt)
    {
        float g = gravity * innerH;

        for (int i = pixels.Count - 1; i >= 0; i--)
        {
            Px p = pixels[i];
            if (p == null || p.rt == null) { pixels.RemoveAt(i); continue; }
            if (p.state == State.Landed) { p.img.enabled = true; continue; }

            if (p.delay > 0f) { p.delay -= dt; continue; }

            p.vy += g * dt;
            p.y -= p.vy * dt;

            if (p.state == State.Falling && p.y <= p.targetY)
            {
                p.y = p.targetY;
                p.vy = 0f;
                p.state = State.Landed;
            }
            else if (p.state == State.Draining && p.y < -size * 2f)
            {
                Destroy(p.rt.gameObject);
                pixels.RemoveAt(i);
                continue;
            }

            p.rt.anchoredPosition = new Vector2(p.x, p.state == State.Landed ? Mathf.Round(p.y) : p.y);
            p.img.enabled = p.y < innerH && p.y + size > 0f;
        }
    }
}