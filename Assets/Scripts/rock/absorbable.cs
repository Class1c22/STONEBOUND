using UnityEngine;

// Позначає фігуру (уламок або шматок), яку камінь може всмоктати. Створюється автоматично.
public class Absorbable : MonoBehaviour
{
    [Tooltip("Об'єм у кубічних метрах")]
    public float volume = 0.01f;

    [HideInInspector] public bool attached;
}