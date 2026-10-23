using UnityEngine;

public class CoinSpinner : MonoBehaviour
{
    [SerializeField] private float rotateSpeed = 90f;   // градусів за секунду
    [SerializeField] private float bobHeight = 0.25f;   // наскільки високо підстрибує
    [SerializeField] private float bobSpeed = 2f;       // як швидко

    private Vector3 _startPos;

    private void Awake()
    {
        // КРОК 1 (готово): запам'ятовуємо, де монета стоїть спочатку
        _startPos = transform.position;
    }

    private void Update()
    {
        // КРОК 2: обертання навколо вертикальної осі
        transform.Rotate(0f, , 0f);

        // КРОК 3: порахувати зміщення вгору-вниз

        // КРОК 4: виставити позицію = початкова + зміщення
    }
}