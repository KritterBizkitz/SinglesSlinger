using System.Collections;
using UnityEngine;

namespace SinglesSlinger
{
    public static class StaticCoroutine
    {
        private sealed class CoroutineHost : MonoBehaviour { }
        private static CoroutineHost host;

        private static void EnsureHost()
        {
            if (host != null) return;

            var go = new GameObject("SinglesSlinger.StaticCoroutineHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            host = go.AddComponent<CoroutineHost>();
        }

        public static Coroutine Start(IEnumerator routine)
        {
            EnsureHost();
            return host.StartCoroutine(routine);
        }

        public static void StopAll()
        {
            if (host == null) return;
            host.StopAllCoroutines();
        }
    }
}
