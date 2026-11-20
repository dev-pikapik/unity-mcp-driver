using System.Collections.Generic;
using System.Linq;
using Brightdesk.Crm.Data;
using UnityEngine.UIElements;

namespace Brightdesk.Crm.UI
{
    public class ContactsView
    {
        public readonly VisualElement Root;

        readonly CrmStore store;
        readonly TextField search;
        readonly ListView list;
        readonly Label countLabel;
        readonly VisualElement detailPane;
        readonly TextField nameField, emailField, phoneField, companyField, tagsField;
        readonly Label formTitle, formError;
        readonly VisualElement form;

        List<Contact> visible = new List<Contact>();
        Contact selected;
        int editingId;

        public ContactsView(CrmStore store)
        {
            this.store = store;
            Root = Ui.Box("split");
            Root.name = "contacts-screen";

            var listPane = Ui.Box("pane", "list-pane");
            var toolbar = Ui.Box("toolbar");
            search = new TextField { name = "contact-search", selectAllOnFocus = false, selectAllOnMouseUp = false };
            search.AddToClassList("search");
            search.textEdition.placeholder = "Search by name, company, email or tag";
            search.RegisterValueChangedCallback(OnSearchChanged);
            toolbar.Add(search);
            var newButton = Ui.Button("New contact", "new-contact", "primary");
            newButton.clicked += OnNewClicked;
            toolbar.Add(newButton);
            listPane.Add(toolbar);

            var header = Ui.Box("list-header");
            header.Add(Ui.Text("NAME", "col-name"));
            header.Add(Ui.Text("COMPANY", "col-company"));
            header.Add(Ui.Text("EMAIL", "col-email"));
            listPane.Add(header);

            list = new ListView { name = "contact-list", fixedItemHeight = 44, selectionType = SelectionType.Single };
            list.AddToClassList("list");
            list.makeItem = MakeRow;
            list.bindItem = BindRow;
            list.itemsSource = visible;
            list.selectionChanged += OnSelectionChanged;
            listPane.Add(list);
            countLabel = Ui.Text("", "footer");
            listPane.Add(countLabel);
            Root.Add(listPane);

            detailPane = Ui.Box("pane", "detail-pane");
            detailPane.name = "contact-detail";
            Root.Add(detailPane);

            form = Ui.Box();
            form.name = "contact-form";
            formTitle = Ui.Text("", "title");
            formTitle.style.marginBottom = 14;
            form.Add(formTitle);
            nameField = Ui.Field(form, "Name", "contact-name");
            emailField = Ui.Field(form, "Email", "contact-email");
            phoneField = Ui.Field(form, "Phone", "contact-phone");
            companyField = Ui.Field(form, "Company", "contact-company");
            tagsField = Ui.Field(form, "Tags", "contact-tags");
            form.Add(Ui.Text("Separate tags with commas, for example: renewal, VIP", "form-hint"));
            formError = Ui.Text("", "error");
            form.Add(formError);
            var buttons = Ui.Box("button-row");
            var save = Ui.Button("Save", "save-contact", "primary");
            save.clicked += OnSaveClicked;
            var cancel = Ui.Button("Cancel", "cancel-contact");
            cancel.clicked += OnCancelClicked;
            buttons.Add(save);
            buttons.Add(cancel);
            form.Add(buttons);

            ShowEmpty("Select a contact to see the details, or create a new one.");
        }

        public void Refresh()
        {
            visible.Clear();
            visible.AddRange(store.Search(search.value));
            list.RefreshItems();
            countLabel.text = visible.Count == store.contacts.Count
                ? store.contacts.Count + " contacts"
                : visible.Count + " of " + store.contacts.Count + " contacts match \"" + search.value.Trim() + "\"";
            int index = selected == null ? -1 : visible.IndexOf(selected);
            if (index >= 0) list.SetSelectionWithoutNotify(new[] { index });
            else list.ClearSelection();
        }

        VisualElement MakeRow()
        {
            var row = Ui.Box("row");
            row.Add(Ui.Text("", "col-name"));
            row.Add(Ui.Text("", "col-company"));
            row.Add(Ui.Text("", "col-email"));
            return row;
        }

        void BindRow(VisualElement row, int index)
        {
            var c = visible[index];
            row.name = "contact-row-" + c.id;
            ((Label)row[0]).text = c.name;
            ((Label)row[1]).text = c.company;
            ((Label)row[2]).text = c.email;
        }

        void OnSearchChanged(ChangeEvent<string> e) => Refresh();

        void OnSelectionChanged(IEnumerable<object> items)
        {
            selected = items.OfType<Contact>().FirstOrDefault();
            if (selected != null) ShowDetail(selected);
            else if (!detailPane.Contains(form)) ShowEmpty("Select a contact to see the details, or create a new one.");
        }

        void ShowDetail(Contact c)
        {
            detailPane.Clear();
            var head = Ui.Box("detail-head");
            head.Add(Ui.Text(c.Initials, "avatar"));
            var titles = Ui.Box();
            titles.Add(Ui.Text(c.name, "title"));
            titles.Add(Ui.Text(c.company, "subtitle"));
            head.Add(titles);
            detailPane.Add(head);

            detailPane.Add(Ui.FieldRow("Email", c.email));
            detailPane.Add(Ui.FieldRow("Phone", c.phone));
            detailPane.Add(Ui.FieldRow("Company", c.company));

            detailPane.Add(Ui.Text("TAGS", "section"));
            var chips = Ui.Box("chips");
            foreach (var t in c.tags) chips.Add(Ui.Text(t, "chip"));
            if (c.tags.Count == 0) chips.Add(Ui.Text("No tags", "muted"));
            detailPane.Add(chips);

            detailPane.Add(Ui.Text("DEALS", "section"));
            var contactDeals = store.DealsFor(c.id);
            foreach (var d in contactDeals) detailPane.Add(Ui.FieldRow(d.stage.ToString(), d.title + "  ·  " + Ui.Money(d.amount)));
            if (contactDeals.Count == 0) detailPane.Add(Ui.Text("No deals with this contact yet", "muted"));

            var buttons = Ui.Box("button-row");
            var edit = Ui.Button("Edit", "edit-contact");
            edit.clicked += OnEditClicked;
            var delete = Ui.Button("Delete", "delete-contact", "danger");
            delete.clicked += OnDeleteClicked;
            buttons.Add(edit);
            buttons.Add(delete);
            detailPane.Add(buttons);
        }

        void ShowEmpty(string message)
        {
            detailPane.Clear();
            detailPane.Add(Ui.Text(message, "empty"));
        }

        void ShowForm(Contact c)
        {
            editingId = c?.id ?? 0;
            formTitle.text = c == null ? "New contact" : "Edit contact";
            nameField.value = c?.name ?? "";
            emailField.value = c?.email ?? "";
            phoneField.value = c?.phone ?? "";
            companyField.value = c?.company ?? "";
            tagsField.value = c == null ? "" : string.Join(", ", c.tags);
            formError.text = "";
            detailPane.Clear();
            detailPane.Add(form);
            nameField.Focus();
        }

        void OnNewClicked()
        {
            list.ClearSelection();
            selected = null;
            ShowForm(null);
        }

        void OnEditClicked()
        {
            if (selected != null) ShowForm(selected);
        }

        void OnDeleteClicked()
        {
            if (selected == null) return;
            var name = selected.name;
            store.DeleteContact(selected.id);
            selected = null;
            ShowEmpty(name + " was deleted.");
        }

        void OnCancelClicked()
        {
            if (selected != null) ShowDetail(selected);
            else ShowEmpty("Select a contact to see the details, or create a new one.");
        }

        void OnSaveClicked()
        {
            string name = nameField.value.Trim(), email = emailField.value.Trim();
            if (name.Length == 0) { formError.text = "Name is required."; return; }
            int at = email.IndexOf('@');
            if (email.Length > 0 && (at < 1 || email.IndexOf('.', at) < at + 2 || email.EndsWith(".")))
            {
                formError.text = "Enter a valid email, like name@company.com.";
                return;
            }
            var tags = tagsField.value.Split(',');
            if (editingId == 0) selected = store.AddContact(name, email, phoneField.value, companyField.value, tags);
            else
            {
                store.UpdateContact(editingId, name, email, phoneField.value, companyField.value, tags);
                selected = store.FindContact(editingId);
            }
            ShowDetail(selected);
            Refresh();
            int index = visible.IndexOf(selected);
            if (index >= 0) list.ScrollToItem(index);
        }
    }
}
