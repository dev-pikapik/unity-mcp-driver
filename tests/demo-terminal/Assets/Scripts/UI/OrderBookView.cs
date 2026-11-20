using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Northwind.Trading.UI
{
    public class OrderBookView : MonoBehaviour
    {
        public MarketData market;
        public TerminalView terminal;
        public Text[] askPrices = new Text[0];
        public Text[] askSizes = new Text[0];
        public Image[] askDepth = new Image[0];
        public Text[] bidPrices = new Text[0];
        public Text[] bidSizes = new Text[0];
        public Image[] bidDepth = new Image[0];
        public Text spreadText;

        void OnEnable() => market.BookChanged += Refresh;
        void OnDisable() => market.BookChanged -= Refresh;

        public void OnAskClicked(int row)
        {
            var level = AskAt(row);
            if (level != null) terminal.SetLimitPrice(level.price);
        }

        public void OnBidClicked(int row)
        {
            var level = row < market.orderBook.bids.Count ? market.orderBook.bids[row] : null;
            if (level != null) terminal.SetLimitPrice(level.price);
        }

        BookLevel AskAt(int row)
        {
            int i = askPrices.Length - 1 - row;
            return i < market.orderBook.asks.Count ? market.orderBook.asks[i] : null;
        }

        void Refresh()
        {
            var book = market.orderBook;
            double max = 1;
            foreach (var l in book.asks) max = System.Math.Max(max, l.size);
            foreach (var l in book.bids) max = System.Math.Max(max, l.size);
            for (int row = 0; row < askPrices.Length; row++)
                Show(AskAt(row), askPrices[row], askSizes[row], askDepth[row], max);
            for (int row = 0; row < bidPrices.Length; row++)
                Show(row < book.bids.Count ? book.bids[row] : null, bidPrices[row], bidSizes[row], bidDepth[row], max);
            spreadText.text = book.asks.Count > 0 && book.bids.Count > 0
                ? "Spread " + (book.asks[0].price - book.bids[0].price).ToString("F2", CultureInfo.InvariantCulture)
                : "Spread -";
        }

        static void Show(BookLevel level, Text price, Text size, Image depth, double max)
        {
            price.text = level == null ? "" : level.price.ToString("F2", CultureInfo.InvariantCulture);
            size.text = level == null ? "" : level.size.ToString("N0", CultureInfo.InvariantCulture);
            depth.rectTransform.anchorMin = new Vector2(1 - (level == null ? 0 : (float)(level.size / max)), 0);
        }
    }
}
