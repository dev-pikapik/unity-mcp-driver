using System;
using System.Collections;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Northwind.Trading
{
    public enum FeedState { Connecting, Live, Reconnecting, Offline }

    public class FeedClient : MonoBehaviour
    {
        public const string DefaultUrl = "http://127.0.0.1:18765";

        public static FeedClient Instance { get; private set; }
        public static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        public MarketData market;
        public string baseUrl = DefaultUrl;
        public int historyCandles = 120;
        public float tickInterval = 0.5f;
        public float bookInterval = 1f;
        public FeedState state = FeedState.Connecting;
        public string lastError;

        PriceModel offlineModel;
        CancellationTokenSource stop;

        public bool IsOnline => state == FeedState.Live || state == FeedState.Reconnecting;

        public event Action StateChanged;

        void Awake()
        {
            Instance = this;
            baseUrl = ResolveUrl().TrimEnd('/');
        }

        static string ResolveUrl()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-feed") return args[i + 1];
            var env = Environment.GetEnvironmentVariable("DEMO_FEED_URL");
            return string.IsNullOrEmpty(env) ? DefaultUrl : env;
        }

        void Start()
        {
            stop = new CancellationTokenSource();
            StartCoroutine(Run());
        }

        void OnDestroy()
        {
            stop?.Cancel();
        }

        IEnumerator Run()
        {
            using (var req = UnityWebRequest.Get(baseUrl + "/api/candles?limit=" + historyCandles))
            {
                req.timeout = 2;
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    lastError = req.error;
                    Debug.LogWarning("Market feed " + baseUrl + " unavailable (" + req.error + "), using offline demo feed");
                    yield return RunOffline();
                    yield break;
                }
                market.ApplyHistory(JsonUtility.FromJson<CandleHistory>(req.downloadHandler.text));
            }

            Debug.Log("Market feed connected: " + baseUrl);
            SetState(FeedState.Live);
            PollBook(stop.Token);
            var wait = new WaitForSecondsRealtime(tickInterval);
            while (true)
            {
                using (var req = UnityWebRequest.Get(baseUrl + "/api/tick"))
                {
                    req.timeout = 2;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                    {
                        market.ApplyTick(JsonUtility.FromJson<Tick>(req.downloadHandler.text));
                        SetState(FeedState.Live);
                    }
                    else
                    {
                        lastError = req.error;
                        SetState(FeedState.Reconnecting);
                    }
                }
                yield return wait;
            }
        }

        async void PollBook(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var json = await Http.GetStringAsync(baseUrl + "/api/book");
                    if (token.IsCancellationRequested) return;
                    market.ApplyBook(JsonUtility.FromJson<OrderBook>(json));
                }
                catch (HttpRequestException e) { lastError = e.Message; }
                catch (TaskCanceledException) { lastError = "order book request timed out"; }
                try { await Task.Delay(TimeSpan.FromSeconds(bookInterval), token); }
                catch (TaskCanceledException) { return; }
            }
        }

        IEnumerator RunOffline()
        {
            offlineModel = new PriceModel(UnixNow());
            long step = offlineModel.StepAt(UnixNow());
            market.ApplyHistory(offlineModel.History(step, historyCandles));
            market.ApplyBook(offlineModel.BookAt(step));
            SetState(FeedState.Offline);
            float nextBook = 0;
            var wait = new WaitForSecondsRealtime(tickInterval);
            while (true)
            {
                step = offlineModel.StepAt(UnixNow());
                market.ApplyTick(offlineModel.TickAt(step));
                if (Time.realtimeSinceStartup >= nextBook)
                {
                    market.ApplyBook(offlineModel.BookAt(step));
                    nextBook = Time.realtimeSinceStartup + bookInterval;
                }
                yield return wait;
            }
        }

        void SetState(FeedState s)
        {
            if (state == s) return;
            state = s;
            StateChanged?.Invoke();
        }

        public static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}
