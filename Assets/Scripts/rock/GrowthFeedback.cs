using UnityEngine;

// Тимчасовий зворотний зв'язок росту: баннер при переході на нову стадію, шкала прогресу
// до наступної, а також необов'язкові частинки й звук. Повісь на камінь поруч із RockAbsorber.
// Коли зʼявиться справжній UI, його можна замінити, а подія RockAbsorber.StageChanged лишиться.
[RequireComponent(typeof(RockAbsorber))]
public class GrowthFeedback : MonoBehaviour
{
    [Header("Баннер нової стадії")]
    public float bannerTime = 2.5f;
    public int bannerFontSize = 54;

    [Header("Шкала прогресу до наступної стадії")]
    public bool showProgressBar = true;

    [Header("Ефекти (необов'язково)")]
    [Tooltip("Частинки, які спалахують на камені при переході на нову стадію (став дочірнім до каменя)")]
    public ParticleSystem burst;
    public AudioClip stageUpSound;
    [Range(0f, 1f)] public float soundVolume = 0.8f;

    RockAbsorber rock;
    AudioSource audioSrc;
    GUIStyle bannerStyle, barStyle;
    int lastStage;
    float bannerUntil;
    string bannerText = "";

    void Start()
    {
        rock = GetComponent<RockAbsorber>();
        lastStage = rock.Stage;
        rock.StageChanged += OnStage;

        if (stageUpSound != null)
        {
            audioSrc = gameObject.AddComponent<AudioSource>();
            audioSrc.playOnAwake = false;
            audioSrc.spatialBlend = 0f;   // 2D: чути однаково, де б не був камінь
        }
    }

    void OnDestroy()
    {
        if (rock != null) rock.StageChanged -= OnStage;
    }

    void OnStage(int stage)
    {
        // вниз (після R) нічого не святкуємо, тільки вгору
        if (stage > lastStage)
        {
            bannerText = rock.StageName.ToUpper();
            bannerUntil = Time.time + bannerTime;

            if (burst != null)
            {
                burst.transform.position = rock.WorldCenter;
                burst.Play();
            }
            if (audioSrc != null) audioSrc.PlayOneShot(stageUpSound, soundVolume);
        }
        lastStage = stage;
    }

    void OnGUI()
    {
        if (rock == null) return;

        if (bannerStyle == null)
        {
            bannerStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = bannerFontSize
            };
            barStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = 18
            };
        }

        // баннер: зникає в останні 0.6 с
        if (Time.time < bannerUntil)
        {
            float a = Mathf.Clamp01((bannerUntil - Time.time) / 0.6f);
            Rect r = new Rect(0, Screen.height * 0.22f, Screen.width, 90);
            DrawShadowedLabel(r, bannerText, bannerStyle, a);
        }

        // шкала прогресу
        if (showProgressBar)
        {
            float w = Mathf.Min(420f, Screen.width * 0.6f), h = 14f;
            Rect bar = new Rect((Screen.width - w) * 0.5f, Screen.height - 50f, w, h);

            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);

            bool last = rock.Stage >= rock.StageCount - 1;
            GUI.color = last ? new Color(1f, 0.85f, 0.2f, 1f) : new Color(1f, 1f, 1f, 0.95f);
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * rock.StageProgress, bar.height), Texture2D.whiteTexture);
            GUI.color = old;

            string label = last
                ? rock.StageName + "  (MAX)"
                : rock.StageName + "  →  " + rock.stages[rock.Stage + 1].name;
            DrawShadowedLabel(new Rect(bar.x, bar.y - 26f, bar.width, 24f), label, barStyle, 1f);
        }
    }

    static void DrawShadowedLabel(Rect r, string text, GUIStyle style, float alpha)
    {
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, alpha * 0.8f);
        GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.Label(r, text, style);
        GUI.color = old;
    }
}
