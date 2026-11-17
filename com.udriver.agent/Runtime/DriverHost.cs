using System;
using UnityEngine;

namespace UDriver
{
    /// <summary>Lives on a hidden DontDestroyOnLoad object; runs tool calls and probes on the main thread.</summary>
    public sealed class DriverHost : MonoBehaviour
    {
        float _nextHook;

        void Update()
        {
            EventStore.Tick();
            Job job;
            while (Driver.Jobs.TryDequeue(out job)) StartCoroutine(Driver.RunJob(job));
            if (Time.unscaledTime >= _nextHook)
            {
                _nextHook = Time.unscaledTime + 0.5f;
                try { UiTools.HookPanels(); } catch (Exception e) { Debug.LogWarning("[UDriver] HookPanels: " + e.Message); }
            }
        }

        void LateUpdate()
        {
            ProbeTools.Tick();
        }

        void OnDestroy()
        {
            if (Driver.Host == this) Driver.Stop();
        }
    }
}
