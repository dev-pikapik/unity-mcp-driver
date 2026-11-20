using System.Collections;
using System.Text;
using UnityEngine;

public class SaveSystem : MonoBehaviour
{
    public Inventory inventory;
    public float interval = 2f;
    public int saveCount;
    public string lastSave;

    void Start()
    {
        InvokeRepeating(nameof(Autosave), interval, interval);
    }

    void Autosave()
    {
        StartCoroutine(Save());
    }

    IEnumerator Save()
    {
        var data = new StringBuilder();
        foreach (var item in inventory.items)
        {
            data.Append(item).Append(';');
            yield return new WaitForSeconds(0.1f);
        }
        lastSave = data.ToString();
        PlayerPrefs.SetString("save", lastSave);
        saveCount++;
    }
}
