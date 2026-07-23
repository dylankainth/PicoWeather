using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace WeatherVR.Data
{
    /// <summary>
    /// Reads files out of <c>StreamingAssets</c> on every platform we care about.
    ///
    /// On Android — which is the platform that matters here — StreamingAssets lives
    /// compressed inside the APK behind a <c>jar:file://</c> URL, so
    /// <see cref="File.ReadAllBytes"/> does not work and everything has to go
    /// through <see cref="UnityWebRequest"/>. On desktop we could read directly, but
    /// using the same path everywhere means the editor exercises the shipping code.
    /// </summary>
    public static class StreamingDataReader
    {
        /// <summary>Subdirectory of StreamingAssets holding the baked payload.</summary>
        public const string DataFolder = "WeatherData";

        public static string PathFor(string fileName)
            => Path.Combine(Application.streamingAssetsPath, DataFolder, fileName);

        /// <summary>
        /// Result of a read attempt. A missing file is an expected outcome, not an
        /// error: every consumer has a procedural fallback.
        /// </summary>
        public class Result
        {
            public bool Success;
            public byte[] Bytes;
            public string Error;

            public string Text => Bytes == null ? null : System.Text.Encoding.UTF8.GetString(Bytes);
        }

        /// <summary>
        /// Coroutine that reads <paramref name="fileName"/> from the data folder.
        /// Never throws; inspect <see cref="Result.Success"/>.
        /// </summary>
        public static IEnumerator Read(string fileName, Result result, float timeoutSeconds = 10f)
        {
            if (result == null) yield break;
            result.Success = false;
            result.Bytes = null;
            result.Error = null;

            string path = PathFor(fileName);
            string url = path.Contains("://") ? path : "file://" + path.Replace('\\', '/');

            using var request = UnityWebRequest.Get(url);
            request.timeout = Mathf.Max(1, Mathf.CeilToInt(timeoutSeconds));
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                result.Error = $"{fileName}: {request.error}";
                yield break;
            }

            result.Bytes = request.downloadHandler.data;
            if (result.Bytes == null || result.Bytes.Length == 0)
            {
                result.Error = $"{fileName}: file is empty";
                yield break;
            }

            result.Success = true;
        }

        /// <summary>
        /// Synchronous read for editor-side tooling. Returns null if the file is
        /// absent or unreadable. Do not call this at runtime on Android.
        /// </summary>
        public static byte[] ReadImmediate(string fileName)
        {
            try
            {
                string path = PathFor(fileName);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[WeatherVR] Could not read {fileName}: {e.Message}");
                return null;
            }
        }
    }
}
