using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using UnityEngine;

namespace Northwind.Trading
{
    public enum OrderSide { Buy, Sell }
    public enum OrderType { Market, Limit }
    public enum OrderStatus { Open, Filled, Cancelled, Rejected }

    [Serializable]
    public class Order
    {
        public int id;
        public string symbol;
        public OrderSide side;
        public OrderType type;
        public double quantity;
        public double limitPrice;
        public OrderStatus status;
        public double fillPrice;
        public string createdAt;
        public string reason;
        public string exchangeRef;
    }

    [Serializable]
    public class Position
    {
        public string symbol;
        public double quantity;
        public double averagePrice;
        public double realizedPnl;

        public double UnrealizedPnl(double lastPrice) => quantity * (lastPrice - averagePrice);
    }

    [Serializable]
    class OrderTicket
    {
        public int clientOrderId;
        public string symbol;
        public string side;
        public string type;
        public double quantity;
        public double limitPrice;
    }

    [Serializable]
    class OrderAck
    {
        public string id;
        public string status;
        public double fillPrice;
    }

    public class TradingService : MonoBehaviour
    {
        public static TradingService Instance { get; private set; }

        public Account account;
        public MarketData market;
        public double cash;
        public double realizedPnl;
        public List<Order> orders = new List<Order>();
        public List<Position> positions = new List<Position>();

        int nextOrderId = 1;

        public event Action Changed;
        public event Action<Order> OrderUpdated;

        public double PositionValue
        {
            get
            {
                double v = 0;
                foreach (var p in positions) v += p.quantity * market.lastPrice;
                return v;
            }
        }

        public double Equity => cash + PositionValue;
        public double TotalPnl => Equity - account.startingCash;

        void Awake()
        {
            Instance = this;
            cash = account.startingCash;
        }

        void OnEnable() => market.PriceChanged += CheckOpenOrders;
        void OnDisable() => market.PriceChanged -= CheckOpenOrders;

        public Order PlaceOrder(OrderSide side, double qty, double limitPrice = 0)
        {
            var order = new Order
            {
                id = nextOrderId++,
                symbol = market.symbol,
                side = side,
                type = limitPrice > 0 ? OrderType.Limit : OrderType.Market,
                quantity = qty,
                limitPrice = limitPrice,
                status = OrderStatus.Open,
                createdAt = DateTime.Now.ToString("HH:mm:ss")
            };
            orders.Add(order);

            if (double.IsNaN(qty) || qty <= 0) Reject(order, "Quantity must be greater than zero");
            else if (qty > account.maxOrderQuantity) Reject(order, "Quantity is above the " + account.maxOrderQuantity.ToString("N0") + " limit");
            else if (double.IsNaN(limitPrice) || limitPrice < 0) Reject(order, "Limit price must be positive");
            else if (market.lastPrice <= 0) Reject(order, "No market price yet");
            else if (Crossed(order)) Fill(order);

            Report(order);
            OrderUpdated?.Invoke(order);
            Changed?.Invoke();
            return order;
        }

        public bool CancelOrder(int orderId)
        {
            var order = orders.Find(o => o.id == orderId);
            if (order == null || order.status != OrderStatus.Open) return false;
            order.status = OrderStatus.Cancelled;
            OrderUpdated?.Invoke(order);
            Changed?.Invoke();
            return true;
        }

        public int CancelAllOrders()
        {
            int n = 0;
            foreach (var o in orders)
                if (o.status == OrderStatus.Open) { o.status = OrderStatus.Cancelled; n++; }
            if (n > 0) Changed?.Invoke();
            return n;
        }

        public Order ClosePosition(string symbol)
        {
            var p = positions.Find(x => x.symbol == symbol);
            if (p == null) return null;
            return PlaceOrder(p.quantity > 0 ? OrderSide.Sell : OrderSide.Buy, Math.Abs(p.quantity));
        }

        public IEnumerable<Order> OpenOrders()
        {
            foreach (var o in orders)
                if (o.status == OrderStatus.Open) yield return o;
        }

        bool Crossed(Order o)
        {
            if (o.type == OrderType.Market) return true;
            return o.side == OrderSide.Buy ? market.lastPrice <= o.limitPrice : market.lastPrice >= o.limitPrice;
        }

        void CheckOpenOrders()
        {
            bool any = false;
            foreach (var o in orders)
            {
                if (o.status != OrderStatus.Open || !Crossed(o)) continue;
                Fill(o);
                OrderUpdated?.Invoke(o);
                any = true;
            }
            if (any) Changed?.Invoke();
        }

        void Fill(Order o)
        {
            double price = market.lastPrice;
            if (o.type == OrderType.Limit) price = o.side == OrderSide.Buy ? Math.Min(price, o.limitPrice) : Math.Max(price, o.limitPrice);
            if (o.side == OrderSide.Buy && o.quantity * price > cash)
            {
                Reject(o, "Not enough demo cash");
                return;
            }

            double signed = o.side == OrderSide.Buy ? o.quantity : -o.quantity;
            cash -= signed * price;
            o.status = OrderStatus.Filled;
            o.fillPrice = price;

            var p = positions.Find(x => x.symbol == o.symbol);
            if (p == null)
            {
                positions.Add(new Position { symbol = o.symbol, quantity = signed, averagePrice = price });
                return;
            }
            if (Math.Sign(p.quantity) == Math.Sign(signed))
            {
                p.averagePrice = (p.averagePrice * p.quantity + price * signed) / (p.quantity + signed);
                p.quantity += signed;
                return;
            }
            double closing = Math.Min(Math.Abs(signed), Math.Abs(p.quantity)) * Math.Sign(p.quantity);
            double pnl = closing * (price - p.averagePrice);
            p.realizedPnl += pnl;
            realizedPnl += pnl;
            p.quantity += signed;
            if (Math.Abs(p.quantity) < 1e-9) positions.Remove(p);
            else if (Math.Sign(p.quantity) == Math.Sign(signed)) p.averagePrice = price;
        }

        void Reject(Order o, string reason)
        {
            o.status = OrderStatus.Rejected;
            o.reason = reason;
        }

        async void Report(Order order)
        {
            var feed = FeedClient.Instance;
            if (order.status == OrderStatus.Rejected) return;
            if (feed == null || !feed.IsOnline)
            {
                order.exchangeRef = "LOCAL-" + order.id;
                return;
            }
            var ticket = new OrderTicket
            {
                clientOrderId = order.id,
                symbol = order.symbol,
                side = order.side.ToString(),
                type = order.type.ToString(),
                quantity = order.quantity,
                limitPrice = order.limitPrice
            };
            try
            {
                var body = new StringContent(JsonUtility.ToJson(ticket), Encoding.UTF8, "application/json");
                var response = await FeedClient.Http.PostAsync(feed.baseUrl + "/api/orders", body);
                var ack = JsonUtility.FromJson<OrderAck>(await response.Content.ReadAsStringAsync());
                order.exchangeRef = ack.id;
                Debug.Log(string.Format(CultureInfo.InvariantCulture, "Order #{0} {1} {2} {3} acknowledged as {4} ({5})", order.id, order.side, order.quantity, order.symbol, ack.id, ack.status));
                OrderUpdated?.Invoke(order);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Order #" + order.id + " was not reported to the feed: " + e.Message);
            }
        }
    }
}
