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
    [Tooltip("Скільки шматків треба з'їсти, щоб рости на цій стадії (1 = як зараз, 2 = удвічі більше шматків). Береться за поточною стадією каменя")]
    [Min(0.1f)] public float absorbCostMultiplier = 1f;
}