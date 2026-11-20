using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Northwind.Trading.UI
{
    public class OrderRow : MonoBehaviour
    {
        public Text sideText;
        public Text quantityText;
        public Text priceText;
        public Text timeText;
        public int orderId;

        public void Bind(Order order)
        {
            orderId = order.id;
            sideText.text = order.side == OrderSide.Buy ? "BUY" : "SELL";
            sideText.color = order.side == OrderSide.Buy ? TerminalView.Up : TerminalView.Down;
            quantityText.text = order.quantity.ToString("0.####", CultureInfo.InvariantCulture);
            priceText.text = order.limitPrice.ToString("F2", CultureInfo.InvariantCulture);
            timeText.text = order.createdAt;
        }

        public void OnCancelClicked()
        {
            TradingService.Instance.CancelOrder(orderId);
        }
    }
}
