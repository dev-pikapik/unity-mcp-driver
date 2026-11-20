using UnityEngine;

public class RewardPanel : MonoBehaviour
{
    public Shop shop;
    public RectTransform claimButton;
    public int reward = 100;
    public bool claimed;

    void OnEnable()
    {
        claimButton.gameObject.SetActive(!claimed);
        claimButton.anchoredPosition = new Vector2(0f, -Screen.height * 0.5f - claimButton.sizeDelta.y * 2f);
    }

    public void Claim()
    {
        if (claimed) return;
        claimed = true;
        shop.coins += reward;
        claimButton.gameObject.SetActive(false);
    }
}
