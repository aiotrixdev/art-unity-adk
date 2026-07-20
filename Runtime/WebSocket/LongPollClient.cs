using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace ART.ADK
{
    internal class LongPollOptions
    {
        public string Endpoint { get; set; }
        public string InitialConnectionId { get; set; }
        public Func<Task<Dictionary<string, string>>> GetAuthHeaders { get; set; }
        public Action<List<JObject>> OnMessages { get; set; }
        public Action<string> OnError { get; set; }
        public int RetryDelayMs { get; set; } = 1000;
        public int EmptyPollDelayMs { get; set; } = 500;
        public int MaxEmptyPollDelayMs { get; set; } = 5000;
    }

    /// <summary>
    /// HTTP long-polling fallback when WebSocket is unavailable.
    /// </summary>
    internal class LongPollClient
    {
        private readonly LongPollOptions _opts;
        private string _connectionId;
        private bool _isRunning;
        private CancellationTokenSource _cts;

        public LongPollClient(LongPollOptions opts)
        {
            _opts = opts;
            _connectionId = opts.InitialConnectionId;
        }

        public void Start(string connectionId = null)
        {
            if (_isRunning) return;
            if (connectionId != null) _connectionId = connectionId;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            _ = PollLoop();
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
        }

        private async Task PollLoop()
        {
            var backoffEmpty = (double)_opts.EmptyPollDelayMs;
            var maxBackoff = (double)_opts.MaxEmptyPollDelayMs;
            var retryDelay = (double)_opts.RetryDelayMs;

            while (_isRunning && !_cts.IsCancellationRequested)
            {
                try
                {
                    var url = _opts.Endpoint;
                    if (!string.IsNullOrEmpty(_connectionId))
                        url += $"?connection_id={Uri.EscapeDataString(_connectionId)}";

                    using var request = UnityWebRequest.Get(url);
                    request.timeout = 35;

                    var headers = await _opts.GetAuthHeaders();
                    foreach (var kv in headers)
                        request.SetRequestHeader(kv.Key, kv.Value);

                    var op = request.SendWebRequest();
                    while (!op.isDone)
                        await Task.Yield();

                    if (request.responseCode == 204)
                    {
                        await Task.Delay((int)backoffEmpty, _cts.Token);
                        backoffEmpty = Math.Min(backoffEmpty * 2, maxBackoff);
                        continue;
                    }

                    if (request.responseCode != 200)
                    {
                        _opts.OnError?.Invoke($"LongPoll HTTP {request.responseCode}");
                        await Task.Delay((int)retryDelay, _cts.Token);
                        continue;
                    }

                    backoffEmpty = _opts.EmptyPollDelayMs;

                    var json = JObject.Parse(request.downloadHandler.text);
                    if (string.IsNullOrEmpty(_connectionId))
                        _connectionId = json["connection_id"]?.ToString();

                    if (json["messages"] is JArray messages && messages.Count > 0)
                    {
                        var list = new List<JObject>();
                        foreach (var m in messages)
                        {
                            if (m is JObject obj) list.Add(obj);
                        }
                        _opts.OnMessages?.Invoke(list);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _opts.OnError?.Invoke(ex.Message);
                    try { await Task.Delay((int)retryDelay, _cts.Token); }
                    catch { return; }
                }
            }
        }
    }
}
