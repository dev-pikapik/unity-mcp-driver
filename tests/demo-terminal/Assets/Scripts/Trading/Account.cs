using UnityEngine;

namespace Northwind.Trading
{
    [CreateAssetMenu(menuName = "Northwind/Demo Account", fileName = "DemoAccount")]
    public class Account : ScriptableObject
    {
        public string accountName = "Paper account";
        public double startingCash = 100000;
        public string currency = "USD-DEMO";
        public double maxOrderQuantity = 10000;
    }
}
