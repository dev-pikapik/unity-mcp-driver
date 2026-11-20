using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Northwind.Trading.UI
{
    public class TerminalView : MonoBehaviour
    {
        public static readonly Color Up = new Color32(38, 166, 154, 255);
        public static readonly Color Down = new Color32(239, 83, 80, 255);
        public static readonly Color Muted = new Color32(125, 138, 153, 255);

        public MarketData market;
        public TradingService trading;
        public FeedClient feed;

        public Text symbolText;
        public Text lastPriceText;
        public Text changeText;
        public Text accountBadgeText;
        public CandleChart chart;
        public LineSeries averageLine;
        public Text averageLegend;

        public InputField quantityInput;
        public InputField limitPriceInput;
        public Text ticketMessage;

        public OrderRow orderTemplate;
        public Text openOrdersTitle;
        public Text openOrdersEmpty;
        public PositionRow positionTemplate;
        public Text positionsEmpty;

        public Text cashText;
        public Text equityText;
        public Text pnlText;
        public Text statusText;

        readonly List<OrderRow> orderRows = new List<OrderRow>();
        readonly List<PositionRow> positionRows = new List<PositionRow>();

        void Start()
        {
            chart.candles = market.candles;
            averageLine.values = market.movingAverage;
            averageLegend.text = "MA(" + market.maPeriod + ")";
            symbolText.text = market.symbol;
            accountBadgeText.text = trading.account.accountName.ToUpperInvariant() + "  ·  " + trading.account.currency;
            market.PriceChanged += OnPriceChanged;
            trading.Changed += RefreshTrading;
            feed.StateChanged += RefreshStatus;
            RefreshTrading();
            RefreshStatus();
        }

        void OnDestroy()
        {
            market.PriceChanged -= OnPriceChanged;
            trading.Changed -= RefreshTrading;
            feed.StateChanged -= RefreshStatus;
        }

        public void OnBuyClicked() => Submit(OrderSide.Buy);
        public void OnSellClicked() => Submit(OrderSide.Sell);

        public void OnCancelAllClicked()
        {
            int n = trading.CancelAllOrders();
            ShowMessage(n == 0 ? "No open orders to cancel" : "Cancelled " + n + (n == 1 ? " order" : " orders"), Muted);
        }

        public void OnQuantityEdited(string text)
        {
            if (TryParse(text, out double qty) && qty > 0) quantityInput.text = Format(qty, "0.####");
        }

        public void SetLimitPrice(double price)
        {
            limitPriceInput.text = Format(price, "F2");
        }

        void Submit(OrderSide side)
        {
            if (!TryParse(quantityInput.text, out double qty) || qty <= 0)
            {
                ShowMessage("Enter a quantity greater than zero", Down);
                return;
            }
            double limit = 0;
            if (!string.IsNullOrWhiteSpace(limitPriceInput.text) && (!TryParse(limitPriceInput.text, out limit) || limit <= 0))
            {
                ShowMessage("Limit price must be a positive number, or empty for a market order", Down);
                return;
            }

            var order = trading.PlaceOrder(side, qty, limit);
            string what = (side == OrderSide.Buy ? "Buy " : "Sell ") + Format(qty, "0.####") + " " + order.symbol;
            switch (order.status)
            {
                case OrderStatus.Filled:
                    ShowMessage(what + " filled at " + Format(order.fillPrice, "F2"), side == OrderSide.Buy ? Up : Down);
                    break;
                case OrderStatus.Open:
                    ShowMessage(what + " limit " + Format(order.limitPrice, "F2") + " is waiting", Muted);
                    break;
                default:
                    ShowMessage(what + " rejected: " + order.reason, Down);
                    break;
            }
        }

        void OnPriceChanged()
        {
            lastPriceText.text = Format(market.lastPrice, "F2");
            changeText.text = Signed(market.Change) + "  (" + (market.Change >= 0 ? "+" : "") + Format(market.ChangePercent, "F2") + "%)";
            changeText.color = Tone(market.Change);
            lastPriceText.color = changeText.color;
            chart.Refresh();
            averageLine.Refresh();
            RefreshTrading();
            RefreshStatus();
        }

        void RefreshTrading()
        {
            int open = 0;
            foreach (var o in trading.OpenOrders()) Row(orderRows, orderTemplate, open++).Bind(o);
            Trim(orderRows, open);
            openOrdersTitle.text = open == 0 ? "Open orders" : "Open orders (" + open + ")";
            openOrdersEmpty.gameObject.SetActive(open == 0);

            for (int i = 0; i < trading.positions.Count; i++) Row(positionRows, positionTemplate, i).Bind(trading.positions[i], market.lastPrice);
            Trim(positionRows, trading.positions.Count);
            positionsEmpty.gameObject.SetActive(trading.positions.Count == 0);

            cashText.text = Format(trading.cash, "N2");
            equityText.text = Format(trading.Equity, "N2");
            pnlText.text = Signed(trading.TotalPnl);
            pnlText.color = Tone(trading.TotalPnl);
        }

        void RefreshStatus()
        {
            string updated = market.lastTickTime > 0
                ? "  ·  updated " + DateTimeOffset.FromUnixTimeMilliseconds((long)(market.lastTickTime * 1000)).ToLocalTime().ToString("HH:mm:ss")
                : "";
            switch (feed.state)
            {
                case FeedState.Live:
                    statusText.text = "Connected to demo feed " + feed.baseUrl + updated;
                    break;
                case FeedState.Reconnecting:
                    statusText.text = "Demo feed " + feed.baseUrl + " not responding, retrying" + updated;
                    break;
                case FeedState.Offline:
                    statusText.text = "offline demo feed  ·  " + feed.baseUrl + " not reachable, prices generated locally" + updated;
                    break;
                default:
                    statusText.text = "Connecting to demo feed " + feed.baseUrl + "...";
                    break;
            }
        }

        void ShowMessage(string text, Color color)
        {
            ticketMessage.text = text;
            ticketMessage.color = color;
        }

        static T Row<T>(List<T> rows, T template, int index) where T : Component
        {
            while (rows.Count <= index)
            {
                var row = Instantiate(template, template.transform.parent);
                row.name = template.name.Replace("Template", "") + " " + (rows.Count + 1);
                rows.Add(row);
            }
            rows[index].gameObject.SetActive(true);
            return rows[index];
        }

        static void Trim<T>(List<T> rows, int count) where T : Component
        {
            for (int i = count; i < rows.Count; i++) rows[i].gameObject.SetActive(false);
        }

        static bool TryParse(string text, out double value) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        public static string Format(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
        public static string Signed(double value) => (value >= 0 ? "+" : "-") + Format(Math.Abs(value), "N2");
        public static Color Tone(double value) => value > 0 ? Up : value < 0 ? Down : Muted;
    }
}
