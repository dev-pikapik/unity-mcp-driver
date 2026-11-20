using System;
using System.Collections.Generic;
using System.Linq;
using Brightdesk.Crm.Data;
using UnityEngine.UIElements;

namespace Brightdesk.Crm.UI
{
    public class DealsView
    {
        public readonly VisualElement Root;

        static readonly List<string> StageNames = Enum.GetNames(typeof(DealStage)).ToList();

        readonly CrmStore store;
        readonly ListView list;
        readonly Label countLabel;
        readonly VisualElement detailPane;
        readonly VisualElement form;
        readonly TextField titleField;
        readonly DropdownField contactField;
        readonly DoubleField amountField;
        readonly DropdownField newStageField;
        readonly Label formError;

        readonly List<Deal> visible = new List<Deal>();
        List<Contact> contactChoices = new List<Contact>();
        Deal selected;

        public DealsView(CrmStore store)
        {
            this.store = store;
            Root = Ui.Box("split");
            Root.name = "deals-screen";

            var listPane = Ui.Box("pane", "list-pane");
            var toolbar = Ui.Box("toolbar");
            toolbar.Add(Ui.Text("Pipeline", "title"));
            toolbar.Add(Ui.Box("spacer"));
            var newButton = Ui.Button("New deal", "new-deal", "primary");
            newButton.clicked += OnNewClicked;
            toolbar.Add(newButton);
            listPane.Add(toolbar);

            var header = Ui.Box("list-header");
            header.Add(Ui.Text("TITLE", "col-title"));
            header.Add(Ui.Text("CONTACT", "col-contact"));
            header.Add(Ui.Text("AMOUNT", "col-amount"));
            header.Add(Ui.Text("STAGE", "col-stage"));
            listPane.Add(header);

            list = new ListView { name = "deal-list", fixedItemHeight = 44, selectionType = SelectionType.Single };
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
            detailPane.name = "deal-detail";
            Root.Add(detailPane);

            form = Ui.Box();
            form.name = "deal-form";
            var formTitle = Ui.Text("New deal", "title");
            formTitle.style.marginBottom = 14;
            form.Add(formTitle);
            titleField = Ui.Field(form, "Title", "deal-title");
            contactField = new DropdownField("Contact") { name = "deal-contact" };
            contactField.AddToClassList("form-field");
            form.Add(contactField);
            amountField = new DoubleField("Amount (USD)") { name = "deal-amount" };
            amountField.AddToClassList("form-field");
            form.Add(amountField);
            newStageField = new DropdownField("Stage", StageNames, 0) { name = "deal-stage" };
            newStageField.AddToClassList("form-field");
            form.Add(newStageField);
            formError = Ui.Text("", "error");
            form.Add(formError);
            var buttons = Ui.Box("button-row");
            var save = Ui.Button("Save", "save-deal", "primary");
            save.clicked += OnSaveClicked;
            var cancel = Ui.Button("Cancel", "cancel-deal");
            cancel.clicked += OnCancelClicked;
            buttons.Add(save);
            buttons.Add(cancel);
            form.Add(buttons);

            ShowEmpty();
        }

        public void Refresh()
        {
            visible.Clear();
            visible.AddRange(store.deals);
            list.RefreshItems();
            countLabel.text = store.deals.Count + " deals  ·  " + store.deals.Count(d => d.IsOpen) + " open  ·  " + Ui.Money(store.OpenPipeline) + " in open pipeline";
            int index = selected == null ? -1 : visible.IndexOf(selected);
            if (index >= 0)
            {
                list.SetSelectionWithoutNotify(new[] { index });
                ShowDetail(selected);
            }
        }

        string ContactName(int id)
        {
            var c = store.FindContact(id);
            return c == null ? "Deleted contact" : c.name;
        }

        VisualElement MakeRow()
        {
            var row = Ui.Box("row");
            row.Add(Ui.Text("", "col-title"));
            row.Add(Ui.Text("", "col-contact"));
            row.Add(Ui.Text("", "col-amount"));
            var stageCell = Ui.Box("col-stage");
            stageCell.Add(Ui.Text("", "stage"));
            row.Add(stageCell);
            return row;
        }

        void BindRow(VisualElement row, int index)
        {
            var d = visible[index];
            row.name = "deal-row-" + d.id;
            ((Label)row[0]).text = d.title;
            ((Label)row[1]).text = ContactName(d.contactId);
            ((Label)row[2]).text = Ui.Money(d.amount);
            SetStagePill((Label)row[3][0], d.stage);
        }

        static void SetStagePill(Label pill, DealStage stage)
        {
            pill.text = stage.ToString();
            foreach (var s in StageNames) pill.RemoveFromClassList("stage-" + s.ToLowerInvariant());
            pill.AddToClassList("stage-" + stage.ToString().ToLowerInvariant());
        }

        void OnSelectionChanged(IEnumerable<object> items)
        {
            selected = items.OfType<Deal>().FirstOrDefault();
            if (selected != null) ShowDetail(selected);
            else if (!detailPane.Contains(form)) ShowEmpty();
        }

        void ShowEmpty()
        {
            detailPane.Clear();
            detailPane.Add(Ui.Text("Select a deal to see it and move it through the pipeline.", "empty"));
        }

        void ShowDetail(Deal d)
        {
            detailPane.Clear();
            detailPane.Add(Ui.Text(d.title, "title"));
            var contact = store.FindContact(d.contactId);
            detailPane.Add(Ui.Text(contact == null ? "Deleted contact" : contact.name + "  ·  " + contact.company, "subtitle"));
            detailPane.Add(Ui.Text(Ui.Money(d.amount), "amount"));
            var pill = Ui.Text("", "stage");
            SetStagePill(pill, d.stage);
            detailPane.Add(pill);

            detailPane.Add(Ui.Text("MOVE TO STAGE", "section"));
            var stageField = new DropdownField("Stage", StageNames, (int)d.stage) { name = "selected-deal-stage" };
            stageField.AddToClassList("form-field");
            stageField.RegisterValueChangedCallback(OnStageChanged);
            detailPane.Add(stageField);

            if (contact != null)
            {
                detailPane.Add(Ui.Text("CONTACT", "section"));
                detailPane.Add(Ui.FieldRow("Email", contact.email));
                detailPane.Add(Ui.FieldRow("Phone", contact.phone));
            }
        }

        void OnStageChanged(ChangeEvent<string> e)
        {
            if (selected != null && Enum.TryParse(e.newValue, out DealStage stage)) store.SetStage(selected.id, stage);
        }

        void OnNewClicked()
        {
            list.ClearSelection();
            selected = null;
            contactChoices = store.contacts.OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase).ToList();
            contactField.choices = contactChoices.Select(c => c.name + " (" + c.company + ")").ToList();
            contactField.index = -1;
            contactField.SetValueWithoutNotify("Choose a contact");
            titleField.value = "";
            amountField.value = 0;
            newStageField.index = 0;
            formError.text = "";
            detailPane.Clear();
            detailPane.Add(form);
            titleField.Focus();
        }

        void OnCancelClicked() => ShowEmpty();

        void OnSaveClicked()
        {
            if (titleField.value.Trim().Length == 0) { formError.text = "Title is required."; return; }
            if (contactField.index < 0 || contactField.index >= contactChoices.Count) { formError.text = "Choose a contact for this deal."; return; }
            if (amountField.value <= 0) { formError.text = "Amount must be greater than zero."; return; }
            var deal = store.AddDeal(titleField.value, contactChoices[contactField.index].id, amountField.value, (DealStage)newStageField.index);
            selected = deal;
            Refresh();
            list.ScrollToItem(visible.IndexOf(deal));
        }
    }
}
