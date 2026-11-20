namespace Brightdesk.Crm.Data
{
    static class SampleData
    {
        static readonly (string name, string company, string domain, string tags)[] People =
        {
            ("Amelia Novak", "Harbor Logistics", "harborlogistics.com", "decision maker,renewal"),
            ("Daniel Okafor", "Harbor Logistics", "harborlogistics.com", "technical"),
            ("Priya Raman", "Harbor Logistics", "harborlogistics.com", "billing"),
            ("Lucas Moreau", "Harbor Logistics", "harborlogistics.com", "newsletter"),
            ("Hannah Weiss", "Harbor Logistics", "harborlogistics.com", "technical,newsletter"),
            ("Marcus Chen", "Pinecrest Health", "pinecresthealth.org", "decision maker,VIP"),
            ("Sofia Alvarez", "Pinecrest Health", "pinecresthealth.org", "procurement"),
            ("Ethan Brooks", "Pinecrest Health", "pinecresthealth.org", "technical"),
            ("Leila Haddad", "Pinecrest Health", "pinecresthealth.org", "newsletter"),
            ("Tomasz Zielinski", "Pinecrest Health", "pinecresthealth.org", "billing"),
            ("Grace Kim", "Lumen Analytics", "lumenanalytics.io", "decision maker"),
            ("Oliver Grant", "Lumen Analytics", "lumenanalytics.io", "technical,VIP"),
            ("Nadia Petrova", "Lumen Analytics", "lumenanalytics.io", "renewal"),
            ("Samuel Adeyemi", "Lumen Analytics", "lumenanalytics.io", "newsletter"),
            ("Isabel Costa", "Lumen Analytics", "lumenanalytics.io", "procurement,billing"),
            ("Henry Lindqvist", "Oakridge Foods", "oakridgefoods.com", "decision maker"),
            ("Mei Tanaka", "Oakridge Foods", "oakridgefoods.com", "technical"),
            ("Rafael Duarte", "Oakridge Foods", "oakridgefoods.com", "procurement"),
            ("Chloe Bennett", "Oakridge Foods", "oakridgefoods.com", "newsletter,renewal"),
            ("Arjun Mehta", "Oakridge Foods", "oakridgefoods.com", "billing"),
            ("Zoe Fischer", "Vantage Robotics", "vantagerobotics.com", "decision maker,VIP"),
            ("Kwame Mensah", "Vantage Robotics", "vantagerobotics.com", "technical"),
            ("Elena Rossi", "Vantage Robotics", "vantagerobotics.com", "procurement"),
            ("Jacob Murphy", "Vantage Robotics", "vantagerobotics.com", "newsletter"),
            ("Aisha Rahman", "Vantage Robotics", "vantagerobotics.com", "renewal,billing"),
        };

        static readonly (string title, int contact, double amount, DealStage stage)[] Deals =
        {
            ("Fleet tracking rollout", 1, 48000, DealStage.Proposal),
            ("Warehouse sensors pilot", 2, 12500, DealStage.Qualified),
            ("Annual support renewal", 3, 9600, DealStage.Won),
            ("Patient portal redesign", 6, 72000, DealStage.Proposal),
            ("Clinic scheduling module", 7, 18400, DealStage.Lead),
            ("Data warehouse migration", 11, 56000, DealStage.Qualified),
            ("BI dashboards add-on", 12, 8800, DealStage.Won),
            ("Usage analytics renewal", 13, 15000, DealStage.Lost),
            ("Cold chain monitoring", 16, 39500, DealStage.Lead),
            ("Supplier portal", 18, 22000, DealStage.Proposal),
            ("Seasonal demand forecasting", 19, 14750, DealStage.Qualified),
            ("Robot fleet telemetry", 21, 91000, DealStage.Qualified),
            ("Field service app", 22, 26500, DealStage.Lead),
            ("Spare parts e-shop", 23, 31200, DealStage.Lost),
            ("Extended warranty program", 25, 11300, DealStage.Won),
        };

        public static CrmStore Create()
        {
            var store = new CrmStore();
            for (int i = 0; i < People.Length; i++)
            {
                var p = People[i];
                var parts = p.name.ToLowerInvariant().Split(' ');
                store.contacts.Add(new Contact
                {
                    id = store.nextContactId++,
                    name = p.name,
                    email = parts[0] + "." + parts[1] + "@" + p.domain,
                    phone = "+1 (555) 01" + (10 + i * 3).ToString("00"),
                    company = p.company,
                    tags = new System.Collections.Generic.List<string>(p.tags.Split(','))
                });
            }
            foreach (var d in Deals)
                store.deals.Add(new Deal { id = store.nextDealId++, title = d.title, contactId = d.contact, amount = d.amount, stage = d.stage });
            return store;
        }
    }
}
