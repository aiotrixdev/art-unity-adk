using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ART.ADK
{
    /// <summary>
    /// Request/response interception system for middleware-like message processing.
    /// </summary>
    public sealed class Interceptor
    {
        private readonly string _interceptorName;
        private object _interceptorData;
        private readonly IWebSocketHandler _websocketHandler;
        private readonly Action<JObject, Action<object>, Action<string>> _fn;
        public EventEmitter Emitter { get; } = new EventEmitter();

        public Interceptor(
            string interceptorName,
            Action<JObject, Action<object>, Action<string>> fn,
            IWebSocketHandler websocketHandler)
        {
            _interceptorName = interceptorName;
            _fn = fn;
            _websocketHandler = websocketHandler;
        }

        public async Task ValidateInterception()
        {
            Debug.Log($"[ART-Interceptor] Validating interception for: {_interceptorName}");
            try 
            {
                _interceptorData = await HelperFunctions.GetInterceptorConfig(_interceptorName, _websocketHandler);
                Debug.Log($"[ART-Interceptor] Successfully received config for {_interceptorName}: {_interceptorData}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ART-Interceptor] Failed to validate interception for {_interceptorName}: {ex.Message}");
                throw;
            }
        }

        public void Reconnect()
        {
            Debug.Log($"[ART] reconnecting interceptor {_interceptorName}");
            _ = Task.Run(async () =>
            {
                try { await ValidateInterception(); }
                catch { }
            });
        }

        private JObject CreateResponse(
            JObject config, string id, string refId,
            string channel, string ns, string evt,
            string pipelineId, string interceptorName,
            string attemptId, string type, object content)
        {
            var response = new JObject(config);
            response["channel"] = channel;
            response["namespace"] = ns;
            response["event"] = evt;
            response["id"] = id;
            response["ref_id"] = refId;
            response["return_flag"] = type;
            response["pipeline_id"] = pipelineId;
            response["interceptor_name"] = interceptorName;
            response["attempt_id"] = attemptId;

            try
            {
                response["content"] = content is JToken jt
                    ? jt.ToString(Formatting.None)
                    : JsonConvert.SerializeObject(content);
            }
            catch
            {
                response["content"] = "";
            }

            return response;
        }

        private void Execute(JObject request)
        {
            Debug.Log($"[ART-Interceptor] Executing callback for interceptor: {_interceptorName}");
            AcknowledgeInterceptor(request);

            var id = request["id"]?.ToString() ?? "";
            var channel = request["channel"]?.ToString() ?? "";
            var ns = request["namespace"]?.ToString() ?? "";
            var from = request["from"]?.ToString() ?? "";
            var to = request["to"];
            var evt = request["event"]?.ToString() ?? "";
            var interceptorName = request["interceptor_name"]?.ToString() ?? "";
            var pipelineId = request["pipeline_id"]?.ToString() ?? "";
            var attemptId = request["attempt_id"]?.ToString() ?? "";
            var refId = request["ref_id"]?.ToString() ?? "";

            var config = new JObject
            {
                ["channel"] = channel,
                ["namespace"] = ns,
                ["event"] = evt,
                ["interceptor_name"] = interceptorName,
                ["from"] = from,
                ["to"] = to
            };

            Action<object> resolve = data =>
            {
                JObject sanitized;
                if (data is JObject jobj)
                {
                    sanitized = jobj;
                    if (sanitized["attempt_id"] != null || sanitized["pipeline_id"] != null)
                        sanitized = sanitized["data"] as JObject ?? new JObject();
                }
                else
                {
                    try { sanitized = JObject.FromObject(data); }
                    catch { sanitized = new JObject(); }
                }

                var response = CreateResponse(config, id, refId, channel, ns, evt,
                    pipelineId, interceptorName, attemptId, "resolve", sanitized);
                SendJson(response);
            };

            Action<string> reject = error =>
            {
                Debug.Log($"[ART-Interceptor] Interceptor {_interceptorName} REJECTED message. Error: {error}");
                var errResponse = new JObject
                {
                    ["rawData"] = request["data"],
                    ["error"] = error
                };
                var response = CreateResponse(config, id, refId, channel, ns, evt,
                    pipelineId, interceptorName, attemptId, "reject", errResponse);
                SendJson(response);
            };

            Debug.Log($"[ART-Interceptor] Invoking user function for {_interceptorName}...");
            _fn(request, resolve, reject);
        }

        private void AcknowledgeInterceptor(JObject request)
        {
            var response = new JObject { ["return_flag"] = "IA" };
            var keys = new[] { "channel", "namespace", "id", "ref_id", "from", "to",
                              "pipeline_id", "interceptor_name", "attempt_id" };
            foreach (var k in keys)
            {
                if (request[k] != null) response[k] = request[k];
            }

            if (request["data"] != null)
            {
                try
                {
                    response["content"] = request["data"] is JToken jt
                        ? jt.ToString(Formatting.None)
                        : JsonConvert.SerializeObject(request["data"]);
                }
                catch { response["content"] = ""; }
            }

            Debug.Log($"[ART-Interceptor] Sending Acknowledgment (IA) for {_interceptorName}");
            SendJson(response);
        }

        public async Task HandleMessage(string channel, JObject data)
        {
            Debug.Log($"[ART-Interceptor] Received message for interception on channel '{channel}' via interceptor '{_interceptorName}'");
            var mutable = new JObject(data);
            var dataStr = data["data"]?.ToString();
            if (dataStr != null)
            {
                try
                {
                    var parsed = JToken.Parse(dataStr);
                    mutable["data"] = parsed;
                }
                catch { }
            }
            Execute(mutable);
        }

        private void SendJson(JObject dict)
        {
            _websocketHandler.SendMessage(dict.ToString(Formatting.None));
        }
    }
}
