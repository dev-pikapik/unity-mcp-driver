using NUnit.Framework;
using UnityEngine;

public class InventoryTests
{
    Inventory inventory;

    [SetUp]
    public void SetUp()
    {
        inventory = new GameObject("Inventory").AddComponent<Inventory>();
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(inventory.gameObject);
    }

    [Test]
    public void AddAppendsItemWhenThereIsRoom()
    {
        int before = inventory.items.Count;
        Assert.IsTrue(inventory.Add("Lantern"));
        Assert.AreEqual(before + 1, inventory.items.Count);
        Assert.Contains("Lantern", inventory.items);
    }

    [Test]
    public void AddRefusesItemsBeyondCapacity()
    {
        inventory.capacity = inventory.items.Count;
        bool added = inventory.Add("Lantern");
        Assert.IsFalse(added, "Add must refuse an item when the inventory is full");
        Assert.AreEqual(inventory.capacity, inventory.items.Count);
    }
}
