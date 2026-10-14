using UnityEngine;

// Будує тестову стіну і будинок, щоб було що руйнувати.
// Додатково будує ряд стін, по одній на кожен матеріал з VoxelWorld → Material Rules
// (дерево, цегла, бетон, метал...), з підписами міцності, щоб перевіряти стадії каменя.
// Може також завантажити модель з MagicaVoxel.
public class VoxelTestBuilder : MonoBehaviour
{
    public VoxelWorld world;

    [Header("Тест матеріалів (стіна на кожне правило з VoxelWorld)")]
    public bool buildMaterialTest = true;
    [Tooltip("Розмір однієї стіни у вокселях (20 x 24 x 4 = 5 x 6 x 1 м)")]
    public Vector3Int materialWallSize = new Vector3Int(20, 24, 4);
    [Tooltip("Проміжок між стінами у вокселях")]
    public int materialGap = 4;
    [Tooltip("Відстань ряду від центру по осі Z, у вокселях (за стіною з цегли)")]
    public int materialTestZ = 90;
    public bool materialLabels = true;

    [Header("Модель з MagicaVoxel (необов'язково)")]
    [Tooltip("Файл .vox, перейменований на .vox.bytes")]
    public TextAsset voxFile;
    public Vector3Int voxOrigin = new Vector3Int(0, 0, 80);

    void Start()
    {
        if (world == null) return;

        BuildWall();
        BuildHouse();
        if (buildMaterialTest) BuildMaterialTest();

        if (voxFile != null)
            world.LoadVox(voxFile.bytes, voxOrigin);
    }

    void BuildWall()
    {
        byte brickA = world.GetOrAddColor(new Color32(170, 70, 55, 255));
        byte brickB = world.GetOrAddColor(new Color32(150, 60, 48, 255));

        // 60 x 24 x 4 вокселів = 15 x 6 x 1 метр, за 10 метрів попереду від центру
        for (int z = 40; z < 44; z++)
        for (int y = 0; y < 24; y++)
        for (int x = -30; x < 30; x++)
        {
            byte id = ((x / 3) + (y / 2)) % 2 == 0 ? brickA : brickB;
            world.SetVoxel(new Vector3Int(x, y, z), id);
        }
    }

    void BuildHouse()
    {
        byte wallA = world.GetOrAddColor(new Color32(200, 195, 180, 255));
        byte wallB = world.GetOrAddColor(new Color32(185, 180, 165, 255));
        byte roof = world.GetOrAddColor(new Color32(90, 55, 45, 255));
        byte floor = world.GetOrAddColor(new Color32(110, 110, 110, 255));

        Vector3Int origin = new Vector3Int(60, 0, 20);
        Vector3Int size = new Vector3Int(40, 26, 32);

        for (int z = 0; z < size.z; z++)
        for (int y = 0; y < size.y; y++)
        for (int x = 0; x < size.x; x++)
        {
            bool isRoof = y >= size.y - 2;
            bool isFloor = y < 1;
            bool isWall = x < 2 || x >= size.x - 2 || z < 2 || z >= size.z - 2;
            if (!isRoof && !isFloor && !isWall) continue;   // порожнє всередині

            bool isDoor = z < 2 && x >= 16 && x < 24 && y < 14;
            if (isDoor) continue;

            byte id = isRoof ? roof : isFloor ? floor : ((x / 4 + y / 4) % 2 == 0 ? wallA : wallB);
            world.SetVoxel(origin + new Vector3Int(x, y, z), id);
        }
    }

    // Один ряд стін поруч: ліворуч найм'якший матеріал, праворуч найміцніший (за порядком у Material Rules).
    // Колір кожної стіни береться прямо з правила, тож міцність гарантовано збігається.
    void BuildMaterialTest()
    {
        var rules = world.materialRules;
        if (rules == null || rules.Count == 0) return;

        Vector3Int size = materialWallSize;
        int n = rules.Count;
        int total = n * size.x + (n - 1) * materialGap;
        int startX = -total / 2;

        for (int i = 0; i < n; i++)
        {
            VoxelWorld.MaterialRule rule = rules[i];
            int xs = startX + i * (size.x + materialGap);

            // два близьких відтінки, щоб стіна не була плоскою (обидва в межах tolerance правила)
            byte a = world.GetOrAddColor(Shift(rule.color, 6));
            byte b = world.GetOrAddColor(Shift(rule.color, -6));

            for (int z = 0; z < size.z; z++)
            for (int y = 0; y < size.y; y++)
            for (int x = 0; x < size.x; x++)
            {
                byte id = ((x / 3) + (y / 2)) % 2 == 0 ? a : b;
                world.SetVoxel(new Vector3Int(xs + x, y, materialTestZ + z), id);
            }

            // перевірка: чи справді воксели отримали очікувану міцність
            int got = world.GetHardness(a);
            if (got != rule.hardness)
                Debug.LogWarning($"[Test] «{rule.name}»: міцність {got}, а очікувалась {rule.hardness}. Правила з близькими кольорами перебивають одне одного, змінюй колір або tolerance у Material Rules.", this);

            if (materialLabels)
                CreateLabel($"{rule.name}\nміцність {rule.hardness}",
                            new Vector3(xs + size.x * 0.5f, size.y + 5f, materialTestZ));
        }
    }

    static Color32 Shift(Color c, int delta)
    {
        Color32 c32 = c;
        return new Color32(
            (byte)Mathf.Clamp(c32.r + delta, 0, 255),
            (byte)Mathf.Clamp(c32.g + delta, 0, 255),
            (byte)Mathf.Clamp(c32.b + delta, 0, 255), 255);
    }

    // Напис над стіною; читається з боку, звідки під'їжджає камінь (від -Z до +Z)
    void CreateLabel(string text, Vector3 voxelPos)
    {
        var go = new GameObject("Label " + text.Replace("\n", " "));
        go.transform.SetParent(transform, false);
        go.transform.position = world.transform.TransformPoint(voxelPos * world.voxelSize);

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var tm = go.AddComponent<TextMesh>();
        tm.text = text;
        tm.font = font;
        tm.fontSize = 100;
        tm.characterSize = 0.06f;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = Color.white;

        var mr = go.GetComponent<MeshRenderer>();
        if (font != null) mr.sharedMaterial = font.material;
    }
}