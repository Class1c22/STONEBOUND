using UnityEngine;

// Будує тестову стіну і будинок, щоб було що руйнувати.
// Може також завантажити модель з MagicaVoxel.
public class VoxelTestBuilder : MonoBehaviour
{
    public VoxelWorld world;

    [Header("Модель з MagicaVoxel (необов'язково)")]
    [Tooltip("Файл .vox, перейменований на .vox.bytes")]
    public TextAsset voxFile;
    public Vector3Int voxOrigin = new Vector3Int(0, 0, 80);

    void Start()
    {
        if (world == null) return;

        BuildWall();
        BuildHouse();

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
}