using System.Globalization;
using Brightdesk.Crm.Data;
using UnityEngine;
using UnityEngine.UIElements;

namespace Brightdesk.Crm.UI
{
    [RequireComponent(typeof(UIDocument))]
    public class CrmApp : MonoBehaviour
    {
        public StyleSheet styles;

        CrmStore store;
        ContactsView contacts;
        DealsView deals;
        Label summary;

        void OnEnable()
        {
            store = CrmStore.Instance;
            var root = GetComponent<UIDocument>().rootVisualElement;
            root.Clear();
            root.styleSheets.Add(styles);
            root.AddToClassList("app");

            var header = Ui.Box("header");
            header.Add(Ui.Text("Brightdesk", "brand"));
            header.Add(Ui.Text("CRM", "brand-suffix"));
            header.Add(Ui.Box("spacer"));
            summary = Ui.Text("", "muted");
            summary.name = "summary";
            header.Add(summary);
            root.Add(header);

            contacts = new ContactsView(store);
            deals = new DealsView(store);
            var tabs = new TabView { name = "main-tabs" };
            var contactsTab = new Tab("Contacts") { name = "contacts-tab" };
            contactsTab.Add(contacts.Root);
            var dealsTab = new Tab("Deals") { name = "deals-tab" };
            dealsTab.Add(deals.Root);
            tabs.Add(contactsTab);
            tabs.Add(dealsTab);
            root.Add(tabs);

            store.Changed += OnStoreChanged;
            OnStoreChanged();
        }

        void OnDisable()
        {
            if (store != null) store.Changed -= OnStoreChanged;
        }

        void OnStoreChanged()
        {
            summary.text = store.contacts.Count + " contacts  ·  " + store.deals.Count + " deals  ·  open pipeline " + Ui.Money(store.OpenPipeline);
            contacts.Refresh();
            deals.Refresh();
        }
    }

    static class Ui
    {
        public static VisualElement Box(params string[] classes)
        {
            var e = new VisualElement();
            foreach (var c in classes) e.AddToClassList(c);
            return e;
        }

        public static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        public static Button Button(string text, string name, params string[] classes)
        {
            var b = new Button { text = text, name = name };
            b.AddToClassList("button");
            foreach (var c in classes) b.AddToClassList(c);
            return b;
        }

        public static TextField Field(VisualElement parent, string label, string name)
        {
            var f = new TextField(label) { name = name, selectAllOnFocus = false, selectAllOnMouseUp = false };
            f.AddToClassList("form-field");
            parent.Add(f);
            return f;
        }

        public static VisualElement FieldRow(string name, string value)
        {
            var row = Box("field-row");
            row.Add(Text(name, "field-name"));
            row.Add(Text(value));
            return row;
        }

        public static string Money(double amount) => "$" + amount.ToString("N0", CultureInfo.InvariantCulture);
    }
}
