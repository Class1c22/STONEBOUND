using UnityEngine;
using UnityEngine.UI;

// Кулдаун кнопки R: прозора плашка опускається зверху вниз.
// Повісь на R_button (1) (плашка поверх R_button). Камінь шукається сам, бо він переживає переходи між сценами.
[RequireComponent(typeof(Image))]
public class CooldownOverlay : MonoBehaviour
{
    private Image overlay;
    private RockBlast blast;
    private float findTimer;

    private void Awake()
    {
        overlay = GetComponent<Image>();
        overlay.type = Image.Type.Filled;
        overlay.fillMethod = Image.FillMethod.Vertical;
        overlay.fillOrigin = (int)Image.OriginVertical.Bottom;  // верхній край опускається вниз
        overlay.fillAmount = 0f;
        overlay.raycastTarget = false;                          // не блокує натискання на R_button
    }

    private void Update()
    {
        if (blast == null)
        {
            overlay.fillAmount = 0f;
            findTimer -= Time.unscaledDeltaTime;
            if (findTimer <= 0f)
            {
                findTimer = 0.5f;
                blast = FindFirstObjectByType<RockBlast>();
            }
            return;
        }

        overlay.fillAmount = blast.CooldownLeft01;
    }

    // Підключи до onClick кнопки R_button (мобільна версія): натискання = те саме, що клавіша R
    public void PressR()
    {
        if (blast == null) blast = FindFirstObjectByType<RockBlast>();
        if (blast != null) blast.TryBlast();
    }
}