using System;
using System.Collections;
using UnityEngine;

namespace UDriver
{
    /// <summary>Lives on a hidden DontDestroyOnLoad object; runs tool calls and probes on the main thread.</summary>
    public sealed class DriverHost : MonoBehaviour
    {
        float _nextHook;

        void Pump()
        {
            Job job;
            while (Driver.Jobs.TryDequeue(out job)) StartCoroutine(Driver.RunJob(job));
        }

        IEnumerator Start()
        {
            var end = new WaitForEndOfFrame();
            while (true) { yield return end; RecordTools.Tick(2); Pump(); }
        }

        void Update()
        {
            EventStore.Tick();
            RecordTools.Tick(0);
            Pump();
            if (Time.unscaledTime >= _nextHook)
            {
                _nextHook = Time.unscaledTime + 0.5f;
                try { UiTools.HookPanels(); } catch (Exception e) { Debug.LogWarning("[UDriver] HookPanels: " + e.Message); }
            }
        }

        void LateUpdate()
        {
            ProbeTools.Tick();
            RecordTools.Tick(1);
            Pump();
        }

        void OnDestroy()
        {
            if (Driver.Host == this) Driver.Stop();
        }
    }
}
