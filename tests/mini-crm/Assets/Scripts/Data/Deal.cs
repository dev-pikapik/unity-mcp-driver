using System;

namespace Brightdesk.Crm.Data
{
    public enum DealStage { Lead, Qualified, Proposal, Won, Lost }

    [Serializable]
    public class Deal
    {
        public int id;
        public string title;
        public int contactId;
        public double amount;
        public DealStage stage;

        public bool IsOpen => stage != DealStage.Won && stage != DealStage.Lost;
    }
}
