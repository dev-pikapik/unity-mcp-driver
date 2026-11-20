using UnityEngine;

public class Shop : MonoBehaviour
{
    public Inventory inventory;
    public GameObject panel;
    public int coins = 1000;
    public int price = 50;
    public string itemName = "Potion";

    public void Open()
    {
        panel.SetActive(true);
    }

    public void Close()
    {
        panel.SetActive(false);
    }

    public void Buy()
    {
        if (coins < price) return;
        coins -= price;
        inventory.Add(itemName);
    }
}
