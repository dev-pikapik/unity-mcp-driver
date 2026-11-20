using UnityEngine;
using UnityEngine.UI;

public class HUD : MonoBehaviour
{
    public Shop shop;
    public Text coinsLabel;
    public RawImage healthBar;
    [Range(0f, 1f)] public float health = 0.75f;

    static readonly Color Full = new Color(0.2f, 0.8f, 0.3f);
    static readonly Color Empty = new Color(0.25f, 0.25f, 0.25f);

    void Update()
    {
        coinsLabel.text = "Coins: " + shop.coins;

        var bar = new Texture2D(100, 1);
        int filled = Mathf.RoundToInt(health * bar.width);
        for (int x = 0; x < bar.width; x++)
            bar.SetPixel(x, 0, x < filled ? Full : Empty);
        bar.Apply();
        healthBar.texture = bar;
    }
}
