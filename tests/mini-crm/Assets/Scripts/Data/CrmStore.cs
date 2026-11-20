using System;
using System.Collections.Generic;
using System.Linq;

namespace Brightdesk.Crm.Data
{
    public class CrmStore
    {
        static CrmStore instance;
        public static CrmStore Instance => instance ?? (instance = SampleData.Create());

        public List<Contact> contacts = new List<Contact>();
        public List<Deal> deals = new List<Deal>();
        public int nextContactId = 1;
        public int nextDealId = 1001;

        public event Action Changed;

        public Contact FindContact(int id) => contacts.Find(c => c.id == id);
        public Deal FindDeal(int id) => deals.Find(d => d.id == id);

        public List<Contact> Search(string query) =>
            contacts.Where(c => c.Matches(query)).OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase).ToList();

        public List<Deal> DealsFor(int contactId) => deals.FindAll(d => d.contactId == contactId);

        public Contact AddContact(string name, string email, string phone, string company, IEnumerable<string> tags)
        {
            var c = new Contact { id = nextContactId++ };
            Fill(c, name, email, phone, company, tags);
            contacts.Add(c);
            Changed?.Invoke();
            return c;
        }

        public bool UpdateContact(int id, string name, string email, string phone, string company, IEnumerable<string> tags)
        {
            var c = FindContact(id);
            if (c == null) return false;
            Fill(c, name, email, phone, company, tags);
            Changed?.Invoke();
            return true;
        }

        public bool DeleteContact(int id)
        {
            if (contacts.RemoveAll(c => c.id == id) == 0) return false;
            Changed?.Invoke();
            return true;
        }

        public Deal AddDeal(string title, int contactId, double amount, DealStage stage)
        {
            var d = new Deal { id = nextDealId++, title = (title ?? "").Trim(), contactId = contactId, amount = amount, stage = stage };
            deals.Add(d);
            Changed?.Invoke();
            return d;
        }

        public bool SetStage(int dealId, DealStage stage)
        {
            var d = FindDeal(dealId);
            if (d == null || d.stage == stage) return false;
            d.stage = stage;
            Changed?.Invoke();
            return true;
        }

        public double OpenPipeline => deals.Where(d => d.IsOpen).Sum(d => d.amount);

        static void Fill(Contact c, string name, string email, string phone, string company, IEnumerable<string> tags)
        {
            c.name = (name ?? "").Trim();
            c.email = (email ?? "").Trim();
            c.phone = (phone ?? "").Trim();
            c.company = (company ?? "").Trim();
            c.tags = (tags ?? Enumerable.Empty<string>()).Select(t => (t ?? "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
