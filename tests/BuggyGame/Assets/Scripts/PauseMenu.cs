using UnityEngine;
using UnityEngine.UI;

public class PauseMenu : MonoBehaviour
{
    public Text label;
    public bool paused;

    public void Toggle()
    {
        paused = !paused;
        Time.timeScale = paused ? 0f : 1f;
        label.text = paused ? "Resume" : "Pause";
    }
}
