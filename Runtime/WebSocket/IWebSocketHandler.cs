using System;
using System.Threading.Tasks;

namespace ART.ADK
{

        /// <summary>
    /// A client transport failure, not confirmation that a server execution failed.
    /// </summary>
    public class ConnectionError : Exception
    {
        public string Code { get; set;}
        public int? Status { get; set;}

        public ConnectionError(string message, string code, int? status = null) : base(message)
        {
            Code = code;
            Status = status;
        }
    }
    /// <summary>
    /// Interface for WebSocket operations, used by subscriptions and interceptors.
    /// </summary>
    public interface IWebSocketHandler
    {
        // Task Wait();
        // bool SendMessage(string message);
        // ConnectionDetail GetConnection();
        // Task<string> Encrypt(string data, string recipientPublicKey);
        // Task<string> Decrypt(string encryptedHash, string senderPublicKey);
        // Task<object> PushForSecureLine(string eventName, object data, bool listen);
        // void RemoveSubscription(string channel);
        Task WaitForConnection();

        Task EnsureAuthorization();

        void SendMessage(string message);

        ConnectionDetail GetConnection(); // may return null

        Task<string> Encrypt(string data, string recipientPublicKey);

        Task<string> Decrypt(string encryptedHash, string senderPublicKey);

        Task<object> PushForSecureLine(string eventName, object data, bool listen);

        void RemoveSubscription(string channel);

        void OnConnectionFailure(Action<ConnectionError> listener);

        void OffConnectionFailure(Action<ConnectionError> listener);
    }
}
