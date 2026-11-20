using System.Collections.Generic;
using UnityEngine;

public class Inventory : MonoBehaviour
{
    public int capacity = 20;
    public List<string> items = new List<string>
    {
        "Sword", "Shield", "Helmet", "Boots", "Bow",
        "Arrows", "Potion", "Map", "Torch", "Rope",
    };

    public bool Add(string item)
    {
        items.Add(item);
        return true;
    }
}
