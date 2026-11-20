using System;
using System.Collections.Generic;

namespace Brightdesk.Crm.Data
{
    [Serializable]
    public class Contact
    {
        public int id;
        public string name;
        public string email;
        public string phone;
        public string company;
        public List<string> tags = new List<string>();

        public string Initials
        {
            get
            {
                var parts = (name ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return "?";
                return (parts[0].Substring(0, 1) + (parts.Length > 1 ? parts[parts.Length - 1].Substring(0, 1) : "")).ToUpperInvariant();
            }
        }

        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            query = query.Trim();
            return Has(name, query) || Has(email, query) || Has(company, query) || Has(phone, query) || tags.Exists(t => Has(t, query));
        }

        static bool Has(string field, string query) =>
            field != null && field.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
