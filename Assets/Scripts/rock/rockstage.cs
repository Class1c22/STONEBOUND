using UnityEngine;

// Стадія розміру каменя: від неї залежать швидкість, сила, радіус руйнування і міцність,
// яку камінь уже здатен зламати.
[System.Serializable]
public class RockStage
{
    public string name = "Стадія";
    [Tooltip("З якого розміру починається стадія (радіус відносно стартового, 1 = початковий)")]
    public float radiusRatio = 1f;
    [Tooltip("Яку міцність матеріалу ламає: 1 = дерево, 2 = цегла, 3 = бетон... (див. VoxelWorld)")]
    public int power = 1;
    [Tooltip("Множник максимальної швидкості (RockController)")]
    public float speedMultiplier = 1f;
    [Tooltip("Множник сили розгону (RockController)")]
    public float forceMultiplier = 1f;
    [Tooltip("Множник радіуса руйнування (VoxelDestroyer і шматки)")]
    public float destroyRadiusMultiplier = 1f;
}