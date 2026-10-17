using UnityEngine;

// Стадія розміру каменя: від неї залежать швидкість, сила і радіус руйнування.
[System.Serializable]
public class RockStage
{
    public string name = "Стадія";
    [Tooltip("З якого розміру починається стадія (радіус відносно стартового, 1 = початковий)")]
    public float radiusRatio = 1f;
    [Tooltip("Множник максимальної швидкості (RockController)")]
    public float speedMultiplier = 1f;
    [Tooltip("Множник сили розгону (RockController)")]
    public float forceMultiplier = 1f;
    [Tooltip("Множник радіуса руйнування (VoxelDestroyer і шматки)")]
    public float destroyRadiusMultiplier = 1f;
}