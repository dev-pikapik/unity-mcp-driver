using System.Collections.Generic;
using UnityEngine;

public class ReportBuilder : MonoBehaviour
{
    public Inventory inventory;
    public int sessionEvents = 7000;
    public string lastReport;

    public void Build()
    {
        var events = new List<string>();
        for (int i = 0; i < sessionEvents; i++)
            events.Add("event:" + inventory.items[i % inventory.items.Count] + ":" + (i % 113));

        int duplicates = 0;
        for (int i = 0; i < events.Count; i++)
            for (int j = 0; j < events.Count; j++)
                if (i != j && events[i] == events[j]) duplicates++;

        lastReport = events.Count + " events, " + duplicates / 2 + " duplicate pairs";
        Debug.Log("Report: " + lastReport);
    }
}
