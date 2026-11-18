using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

// Вимикає головний камінь, його керування і камеру, вмикає камеру синематики й запускає Timeline.
public class EndingCinematicStarter : MonoBehaviour
{
    public PlayableDirector director;

    [Header("Камера синематики")]
    public Camera cinemaCamera;

    [Header("Обʼєкти синематики (rockmove, CinemaObjects) — увімкнуться на старті")]
    public GameObject[] enableObjects;

    [Header("Додатково вимкнути (HUD, UI, PixelTubeBar)")]
    public GameObject[] disableObjects;

    [Header("Додатково вимкнути скрипти")]
    public Behaviour[] disableScripts;

    [Header("Автопошук старого каменя (можна лишити як є)")]
    public string[] rockScriptNames = { "RockAbsorber", "PlanetRockController", "RockController" };
    public string[] cameraScriptNames = { "PlanetCamera", "FollowCamera" };
    public string rockNamePart = "rock";
    public string rockTag = "";  // необовʼязково

    [Header("Запуск")]
    public bool playOnStart = false;
    [Tooltip("Почати Timeline з нуля")]
    public bool restartTimeline = true;
    [Tooltip("Вимкнути AudioListener на старій камері (якщо не вимкнулась разом з обʼєктом)")]
    public bool fixAudioListeners = true;

    bool started;

    void Start()
    {
        if (playOnStart) Begin();
    }

    // Чи належить обʼєкт синематиці (його не можна вимикати)
    bool IsCinema(Transform t)
    {
        if (t == null) return false;
        if (cinemaCamera && t.IsChildOf(cinemaCamera.transform)) return true;
        if (t.IsChildOf(transform)) return true;
        if (director && t.IsChildOf(director.transform)) return true;
        if (enableObjects != null)
            foreach (var e in enableObjects)
                if (e && t.IsChildOf(e.transform)) return true;
        return false;
    }

    // Обʼєкти, які не можна вимикати, бо на них тримається решта гри
    static bool IsCritical(GameObject go)
    {
        return go.GetComponent<PlanetGenerator>() != null
            || go.GetComponent<VoxelWorld>() != null
            || go.GetComponent<PlanetEndTrigger>() != null
            || go.GetComponent<PlayableDirector>() != null;
    }

    static bool NameIn(string typeName, string[] names)
    {
        if (names == null) return false;
        foreach (var n in names)
            if (!string.IsNullOrEmpty(n) && typeName == n) return true;
        return false;
    }

    [ContextMenu("Test Begin")]
    public void Begin()
    {
        if (started) return;
        started = true;

        // 0) явно задане в Inspector
        if (disableScripts != null)
            foreach (var b in disableScripts) if (b) b.enabled = false;
        if (disableObjects != null)
            foreach (var g in disableObjects) if (g) g.SetActive(false);

        var toHide = new HashSet<GameObject>();

        // 1) за скриптами каменя і камери
        foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (!mb || IsCinema(mb.transform)) continue;
            string tn = mb.GetType().Name;
            if (NameIn(tn, rockScriptNames) || NameIn(tn, cameraScriptNames))
                toHide.Add(mb.gameObject);
        }

        // 2) за тегом
        if (!string.IsNullOrEmpty(rockTag))
        {
            try
            {
                foreach (var go in GameObject.FindGameObjectsWithTag(rockTag))
                    if (!IsCinema(go.transform)) toHide.Add(go);
            }
            catch (UnityException) { }
        }

        // 3) за іменем + Rigidbody (лише якщо нічого не знайшли)
        if (toHide.Count == 0)
        {
            foreach (var rb in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
            {
                if (!rb || IsCinema(rb.transform)) continue;
                if (rb.name.ToLower().Contains(rockNamePart.ToLower()))
                    toHide.Add(rb.gameObject);
            }
        }

        // 4) стара головна камера
        if (Camera.main && !IsCinema(Camera.main.transform))
            toHide.Add(Camera.main.gameObject);

        foreach (var go in toHide)
        {
            if (go == null) continue;
            if (IsCritical(go))
            {
                // на цьому обʼєкті живе генератор/світ/тригер: вимикаємо лише камеру й скрипти каменя
                var cam = go.GetComponent<Camera>();
                if (cam) cam.enabled = false;
                var al = go.GetComponent<AudioListener>();
                if (al) al.enabled = false;
                foreach (var mb in go.GetComponents<MonoBehaviour>())
                    if (mb && (NameIn(mb.GetType().Name, rockScriptNames) || NameIn(mb.GetType().Name, cameraScriptNames)))
                        mb.enabled = false;
                continue;
            }
            Debug.Log("EndingCinematicStarter: вимикаю " + go.name);
            go.SetActive(false);
        }

        // Показати синематику
        if (enableObjects != null)
            foreach (var g in enableObjects) if (g) g.SetActive(true);

        if (cinemaCamera)
        {
            cinemaCamera.gameObject.SetActive(true);
            cinemaCamera.enabled = true;
            if (fixAudioListeners)
            {
                var listener = cinemaCamera.GetComponent<AudioListener>();
                if (listener) listener.enabled = true;
            }
        }

        if (director)
        {
            if (!director.gameObject.activeInHierarchy) director.gameObject.SetActive(true);
            if (restartTimeline) director.time = 0;
            director.Play();
        }
        else
        {
            Debug.LogWarning("[EndingCinematicStarter] PlayableDirector не призначено, Timeline не запуститься", this);
        }
    }
}