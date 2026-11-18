using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

public class EndingCinematicStarter : MonoBehaviour
{
    public PlayableDirector director;

    [Header("Камера синематики")]
    public Camera cinemaCamera;

    [Header("Обʼєкти синематики (rockmove, CinemaObjects) — увімкнуться на старті")]
    public GameObject[] enableObjects;

    [Header("Додатково вимкнути (HUD, UI)")]
    public GameObject[] disableObjects;

    [Header("Додатково вимкнути скрипти")]
    public Behaviour[] disableScripts;

    [Header("Автопошук старого каменя (можна лишити як є)")]
    public string[] rockScriptNames = { "RockAbsorber", "PlanetRockController", "RockController" };
    public string[] cameraScriptNames = { "PlanetCamera", "FollowCamera" };
    public string rockNamePart = "rock";
    public string rockTag = "";  // необовʼязково

    public bool playOnStart = false;

    bool started;

    void Start()
    {
        if (playOnStart) Begin();
    }

    bool IsCinema(Transform t)
    {
        if (cinemaCamera && t.IsChildOf(cinemaCamera.transform)) return true;
        if (t.IsChildOf(transform)) return true;
        foreach (var e in enableObjects)
            if (e && t.IsChildOf(e.transform)) return true;
        return false;
    }

    static bool NameIn(string typeName, string[] names)
    {
        foreach (var n in names)
            if (!string.IsNullOrEmpty(n) && typeName == n) return true;
        return false;
    }

    [ContextMenu("Test Begin")]
    public void Begin()
    {
        if (started) return;
        started = true;

        foreach (var b in disableScripts) if (b) b.enabled = false;
        foreach (var g in disableObjects) if (g) g.SetActive(false);

        var toHide = new HashSet<GameObject>();

        // 1) за скриптами каменя і камери
        foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (!mb || IsCinema(mb.transform)) continue;
            string tn = mb.GetType().Name;

            if (NameIn(tn, rockScriptNames))
                toHide.Add(mb.gameObject);
            else if (NameIn(tn, cameraScriptNames))
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

        // 3) за іменем + Rigidbody
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
            Debug.Log("EndingCinematicStarter: вимикаю " + go.name);
            go.SetActive(false);
        }

        // Показати синематику
        foreach (var g in enableObjects) if (g) g.SetActive(true);
        if (cinemaCamera) cinemaCamera.gameObject.SetActive(true);

        if (director) director.Play();
    }
}