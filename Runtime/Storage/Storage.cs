using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace ART.ADK
{
    /// <summary>
    /// Phase-tagged storage failure. Mirrors the Kotlin <c>UploadError</c>
    /// (phases: validate, signed-url, put, confirm, list, get, delete).
    /// </summary>
    public class UploadError : Exception
    {
        public string Phase { get; }
        public int? Status { get; }

        /// <summary>Alias of <see cref="Status"/> kept for existing callers.</summary>
        public long? StatusCode => Status;

        public UploadError(
            string message,
            string phase,
            int? status = null,
            Exception cause = null)
            : base(message, cause)
        {
            Phase = phase;
            Status = status;
        }
    }

    [Serializable]
    public sealed class UploadOptions
    {
        public string Filename;

        // A byte[] has no MIME type, so pass it explicitly (Kotlin does the same).
        public string ContentType;

        public string ConfigType;
        public string ConfigId;
        public List<string> Scopes;
        public long? TtlSeconds;

        // Kotlin uses timeoutMs, not timeoutSeconds.
        public long? TimeoutMs;

        public Action<float> OnProgress;

        // Not used by this backend (the Kotlin Storage has no thread scoping); kept so callers
        // such as Agent / AgentThread can still set it.
        public string ThreadId;

        public UploadOptions()
        {
            ContentType = "application/octet-stream";
            ConfigType = "media";
            Scopes = new List<string>();
        }

         /// <summary>Returns a copy of these options (equivalent to Kotlin's <c>copy()</c>).</summary>
    // public UploadOptions Clone()
    // {
    //     var copy = (UploadOptions)MemberwiseClone();
    //     // MemberwiseClone is shallow; copy the list so changes to the clone don't affect the original.
    //     copy.Scopes = Scopes != null ? new List<string>(Scopes) : null;
    //     return copy;
    // }
    }

    [Serializable]
    public sealed class FileRef
    {
        public string FileId;
        public string Name;
        public string ReadUrl;
        public long Size;
        public string ContentType;
    }

    [Serializable]
    public sealed class ListOptions
    {
        public string ConfigType;
        public string ConfigId;
        public int? Page;
        public int? Limit;

        // Not used by this backend; see UploadOptions.ThreadId.
        public string ThreadId;

//         public ListOptions Clone()
// {
//     var copy = (ListOptions)MemberwiseClone();
//     // If ListOptions has any List<> or other reference-type fields, copy them here too.
//     return copy;
// }
    }

    [Serializable]
    public sealed class StorageFile
    {
        public string FileId;
        public string Name;
        public string ConfigType;
        public string ConfigId;
        public long Size;
        public string ContentType;
        public string Status;
        public string CreatedAt;
        public string ExpiresAt;
        public string ReadUrl;
    }

    [Serializable]
    public sealed class ListFilesResult
    {
        public List<StorageFile> Files = new List<StorageFile>();
        public int Total;
    }

    /// <summary>Accepted by <see cref="Storage.GetFile(string, FileAccessOptions)"/>; only the timeout is used.</summary>
    public class FileAccessOptions
    {
        public string ThreadId { get; set; } = "";
        public long? TimeoutMs { get; set; }
    }

    /// <summary>
    /// Authenticated ART storage client. Mirrors the Kotlin <c>Storage</c>.
    ///
    /// Flow:
    /// 1. POST /upload/signed-url
    /// 2. PUT bytes to the returned upload_url
    /// 3. POST /upload/confirm/{fileId}
    /// </summary>
    public sealed class Storage
    {
        /// <summary>Unity helper: reads a file from disk and uploads it.</summary>
        public async Task<FileRef> UploadFile(string path, UploadOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new UploadError("file does not exist", "validate");

            options = options ?? new UploadOptions();
            if (string.IsNullOrWhiteSpace(options.Filename))
                options.Filename = Path.GetFileName(path);

            return await Upload(File.ReadAllBytes(path), options);
        }

        public async Task<FileRef> Upload(byte[] file, UploadOptions options = null)
        {
            if (file == null || file.Length == 0)
                throw new UploadError("file is empty", "validate");

            options = options ?? new UploadOptions();

            Auth auth;
            try
            {
                auth = Auth.GetInstance();
            }
            catch (Exception ex)
            {
                throw new UploadError("call connect() before upload()", "validate", null, ex);
            }

            var fileName = options.Filename ?? "upload.bin";
            var contentType = options.ContentType ?? "application/octet-stream";
            var configId = options.ConfigId ?? auth.GetCredentials().ProjectKey;

            var init = await StorageCall(
                "signed-url",
                "POST",
                "/upload/signed-url",
                new
                {
                    config_type = options.ConfigType ?? "media",
                    config_id = configId,
                    file_name = fileName,
                    file_size = file.Length,
                    content_type = contentType,
                    scopes = options.Scopes ?? new List<string>(),
                    ttl_seconds = options.TtlSeconds
                },
                options.TimeoutMs);

            var fileId = init["file_id"]?.ToString();
            var uploadUrl = init["upload_url"]?.ToString();
            if (string.IsNullOrEmpty(fileId) || string.IsNullOrEmpty(uploadUrl))
                throw new UploadError("signed-url missing file_id/upload_url", "signed-url");

            await PutToStorage(uploadUrl, file, contentType, options);

            var done = await StorageCall(
                "confirm",
                "POST",
                "/upload/confirm/" + Escape(fileId),
                null,
                options.TimeoutMs);

            var doneFile = done["file"] as JObject;

            return new FileRef
            {
                FileId = fileId,
                Name = fileName,
                ReadUrl = done["read_url"]?.ToString() ?? string.Empty,
                Size = doneFile?["file_size"]?.Value<long?>() ?? file.LongLength,
                ContentType = contentType
            };
        }

        public async Task<ListFilesResult> ListFiles(ListOptions options = null)
        {
            options = options ?? new ListOptions();

            var query = new List<string>();
            if (options.ConfigType != null) query.Add("config_type=" + Escape(options.ConfigType));
            if (options.ConfigId != null) query.Add("config_id=" + Escape(options.ConfigId));
            if (options.Page.HasValue) query.Add("page=" + options.Page.Value);
            if (options.Limit.HasValue) query.Add("limit=" + options.Limit.Value);

            var path = "/files" + (query.Count == 0 ? "" : "?" + string.Join("&", query));
            var data = await StorageCall("list", "GET", path);

            var result = new ListFilesResult
            {
                Total = data["total"]?.Value<int?>() ?? 0
            };

            if (data["files"] is JArray files)
            {
                foreach (var item in files)
                {
                    if (item is JObject f)
                        result.Files.Add(ToStorageFile(f));
                }
            }

            return result;
        }

        public async Task<StorageFile> GetFile(string fileId, long? timeoutMs = null)
        {
            var data = await StorageCall("get", "GET", "/file/" + Escape(fileId), null, timeoutMs);
            return ToStorageFile(data["file"] as JObject, data["read_url"]?.ToString());
        }

        /// <summary>Overload for callers that pass <see cref="FileAccessOptions"/>; only the timeout is used.</summary>
        public Task<StorageFile> GetFile(string fileId, FileAccessOptions opts)
            => GetFile(fileId, opts?.TimeoutMs);

        public async Task DeleteFile(string fileId, bool hard = false, long? timeoutMs = null)
        {
            var path = "/file/" + Escape(fileId) + (hard ? "/hard" : "");
            await StorageCall("delete", "DELETE", path, null, timeoutMs);
        }

        /// <summary>Overload used by Adk.DeleteFile; the cancellation token is not used by this backend.</summary>
        public Task DeleteFile(string fileId, bool hard, CancellationToken signal, long? timeoutMs)
            => DeleteFile(fileId, hard, timeoutMs);

        // ---------------------------------------------------------
        // Internals
        // ---------------------------------------------------------

        private static async Task<JObject> StorageCall(
            string op,
            string method,
            string path,
            object body = null,
            long? timeoutMs = null)
        {
            CredentialStore creds;
            try
            {
                creds = Auth.GetInstance().GetCredentials();
            }
            catch (Exception ex)
            {
                throw new UploadError("call connect() before using storage", "validate", null, ex);
            }

            // Kotlin: ${BASE_URL.removeSuffix("/ws")}/api/${enc(org)}/storage
            var baseUrl = ArtConstants.BASE_URL ?? "";
            if (baseUrl.EndsWith("/ws", StringComparison.Ordinal))
                baseUrl = baseUrl.Substring(0, baseUrl.Length - 3);
            baseUrl += "/api/" + Escape(creds.OrgTitle) + "/storage";

            try
            {
                var auth = Auth.GetInstance();
                var authData = await auth.Authenticate();

                using var request = new UnityWebRequest(baseUrl + path, method.ToUpperInvariant());
                request.downloadHandler = new DownloadHandlerBuffer();

                if (body != null)
                {
                    var bodyBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
                    request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                         method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
                         method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
                {
                    // Kotlin sends an empty RequestBody for POST/PUT/PATCH when body == null.
                    request.uploadHandler = new UploadHandlerRaw(Array.Empty<byte>());
                }

                request.SetRequestHeader("Authorization", "Bearer " + authData.AccessToken);
                request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("X-Org", creds.OrgTitle);
                request.SetRequestHeader("Environment", creds.Environment);
                request.SetRequestHeader("ProjectKey", creds.ProjectKey);

                // Kotlin timeoutMs -> milliseconds; UnityWebRequest.timeout is seconds.
                if (timeoutMs.HasValue)
                    request.timeout = Math.Max(1, (int)Math.Ceiling(timeoutMs.Value / 1000.0));

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                    await Task.Yield();

                if (request.responseCode < 200 || request.responseCode >= 300)
                    throw new InvalidOperationException($"HTTP {request.responseCode} {request.error}");

                var text = request.downloadHandler?.text?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return new JObject();

                var root = JObject.Parse(text);
                return root["data"] as JObject ?? new JObject();
            }
            catch (UploadError)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new UploadError($"storage {op} failed: {ex.Message}", op, null, ex);
            }
        }

        private static async Task PutToStorage(
            string url,
            byte[] file,
            string contentType,
            UploadOptions options)
        {
            // Kotlin default is 60,000 ms.
            var timeoutMs = options.TimeoutMs ?? 60_000L;

            options.OnProgress?.Invoke(0f);

            try
            {
                using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT);
                request.uploadHandler = new UploadHandlerRaw(file) { contentType = contentType };
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("x-ms-blob-type", "BlockBlob");
                request.SetRequestHeader("Content-Type", contentType);
                request.timeout = Math.Max(1, (int)Math.Ceiling(timeoutMs / 1000.0));

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    options.OnProgress?.Invoke(operation.progress);
                    await Task.Yield();
                }

                if (request.responseCode < 200 || request.responseCode >= 300)
                    throw new UploadError($"storage PUT failed ({request.responseCode})", "put", (int)request.responseCode);

                options.OnProgress?.Invoke(1f);
            }
            catch (UploadError)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new UploadError("storage PUT failed (network/timeout)", "put", null, ex);
            }
        }

        private static StorageFile ToStorageFile(JObject f, string readUrl = null)
        {
            return new StorageFile
            {
                FileId = f?["id"]?.ToString() ?? "",
                Name = f?["original_name"]?.ToString() ?? "",
                ConfigType = f?["config_type"]?.ToString() ?? "",
                ConfigId = f?["config_id"]?.ToString() ?? "",
                Size = f?["file_size"]?.Value<long?>() ?? 0L,
                ContentType = f?["content_type"]?.ToString() ?? "",
                Status = f?["status"]?.ToString() ?? "",
                CreatedAt = f?["created_at"]?.ToString() ?? "",
                ExpiresAt = f?["expires_at"]?.ToString(),
                ReadUrl = readUrl ?? f?["read_url"]?.ToString()
            };
        }

        private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);
    }
}