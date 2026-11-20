using UnityEngine;
using UnityEngine.UI;

public class SettingsMenu : MonoBehaviour
{
    public GameObject panel;
    public Text volumeLabel;
    [Range(0f, 1f)] public float volume = 0.8f;

    public void Open()
    {
        panel.SetActive(true);
        volumeLabel.text = "Volume: " + Mathf.RoundToInt(volume * 100f) + "%";
    }

    public void Close()
    {
        panel.SetActive(false);
    }
}
