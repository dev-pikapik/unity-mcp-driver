using UnityEngine;
using UnityEngine.UI;

public class DialogBox : MonoBehaviour
{
    public Text body;
    public GameObject continueButton;
    public GameObject closeButton;
    public int page;
    public string[] pages =
    {
        "A stranger waves you over: \"Traveller, the old mill is overrun by rats.\"",
        "\"Clear them out and the miller will pay you well.\"",
        "\"Take this lantern. Good luck!\"",
    };

    public void Show()
    {
        page = 0;
        body.text = pages[page];
        continueButton.SetActive(true);
        closeButton.SetActive(false);
        gameObject.SetActive(true);
    }

    public void Continue()
    {
        if (page + 1 < pages.Length)
        {
            page++;
            body.text = pages[page];
        }
        else if (page == pages.Length)
        {
            continueButton.SetActive(false);
            closeButton.SetActive(true);
        }
    }

    public void Close()
    {
        gameObject.SetActive(false);
    }
}
