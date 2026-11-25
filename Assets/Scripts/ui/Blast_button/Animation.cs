using UnityEngine;
using UnityEngine.UI;

// Ефекти кнопки R: натиск при вибуху, тряска при відмові, спалах при готовності.
// Повісь на R_button (саму кнопку). Камінь шукається сам, бо переживає переходи між сценами.
[RequireComponent(typeof(RectTransform))]
public class RButtonFx : MonoBehaviour
{
    [Header("Натиск (при вибуху)")]
    public bool punch = true;
    [Range(0.5f, 1f)] public float punchScale = 0.8f;
    public float punchTime = 0.3f;

    [Header("Тряска (R недоступна)")]
    public bool shake = true;
    public float shakeStrength = 8f;
    public float shakeTime = 0.25f;

    [Header("Спалах готовності")]
    public bool readyFlash = true;
    [Tooltip("Білий Image поверх кнопки (можна залишити порожнім: тоді буде лише збільшення)")]
    public Image flash;
    public float flashTime = 0.35f;
    [Range(1f, 1.5f)] public float flashScale = 1.15f;

    RectTransform rt;
    RockBlast blast;
    Vector2 basePos;
    float findTimer;
    float punchT = 1f, shakeT = 1f, flashT = 1f;
    bool wasCooling;

    void Awake()
    {
        rt = (RectTransform)transform;
        basePos = rt.anchoredPosition;
        if (flash != null)
        {
            flash.raycastTarget = false;
            SetFlashAlpha(0f);
        }
    }

    void Update()
    {
        FindBlast();

        // кулдаун закінчився: спалах готовності
        if (blast != null)
        {
            bool cooling = blast.CooldownLeft01 > 0f;
            if (wasCooling && !cooling && readyFlash) flashT = 0f;
            wasCooling = cooling;
        }

        float dt = Time.unscaledDeltaTime;
        Vector2 pos = basePos;
        float scale = 1f;

        if (punchT < 1f)
        {
            punchT = Mathf.Min(1f, punchT + dt / Mathf.Max(0.01f, punchTime));
            // різко стиснулась, потім пружинить із затуханням
            scale *= 1f + (punchScale - 1f) * Mathf.Cos(punchT * Mathf.PI * 2f) * (1f - punchT);
        }

        if (shakeT < 1f)
        {
            shakeT = Mathf.Min(1f, shakeT + dt / Mathf.Max(0.01f, shakeTime));
            pos.x += Mathf.Sin(shakeT * Mathf.PI * 10f) * (1f - shakeT) * shakeStrength;
        }

        if (flashT < 1f)
        {
            flashT = Mathf.Min(1f, flashT + dt / Mathf.Max(0.01f, flashTime));
            float k = Mathf.Sin(flashT * Mathf.PI);   // 0 -> 1 -> 0
            scale *= 1f + (flashScale - 1f) * k;
            SetFlashAlpha(k * 0.7f);
        }

        rt.anchoredPosition = pos;
        rt.localScale = Vector3.one * scale;
    }

    void FindBlast()
    {
        if (blast != null) return;
        findTimer -= Time.unscaledDeltaTime;
        if (findTimer > 0f) return;
        findTimer = 0.5f;

        blast = FindFirstObjectByType<RockBlast>();
        if (blast != null)
        {
            blast.Blasted += OnBlasted;
            blast.Denied += OnDenied;
            wasCooling = blast.CooldownLeft01 > 0f;
        }
    }

    void OnBlasted() { if (punch) punchT = 0f; }
    void OnDenied()  { if (shake) shakeT = 0f; }

    void SetFlashAlpha(float a)
    {
        if (flash == null) return;
        Color c = flash.color;
        c.a = a;
        flash.color = c;
    }

    void OnDestroy()
    {
        if (blast != null)
        {
            blast.Blasted -= OnBlasted;
            blast.Denied -= OnDenied;
        }
    }
}