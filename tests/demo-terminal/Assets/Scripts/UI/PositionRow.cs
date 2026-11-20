using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Northwind.Trading.UI
{
    public class PositionRow : MonoBehaviour
    {
        public Text symbolText;
        public Text quantityText;
        public Text averageText;
        public Text pnlText;
        public string symbol;

        public void Bind(Position position, double lastPrice)
        {
            symbol = position.symbol;
            symbolText.text = position.symbol;
            quantityText.text = position.quantity.ToString("0.####", CultureInfo.InvariantCulture);
            quantityText.color = position.quantity >= 0 ? TerminalView.Up : TerminalView.Down;
            averageText.text = position.averagePrice.ToString("F2", CultureInfo.InvariantCulture);
            double pnl = position.UnrealizedPnl(lastPrice);
            pnlText.text = TerminalView.Signed(pnl);
            pnlText.color = TerminalView.Tone(pnl);
        }

        public void OnCloseClicked()
        {
            TradingService.Instance.ClosePosition(symbol);
        }
    }
}
