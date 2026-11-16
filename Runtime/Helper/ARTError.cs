using System;

namespace ART.ADK
{
    /// <summary>
    /// Base exception for all ART ADK errors.
    /// </summary>
    public class ARTException : Exception
    {
        public ARTException(string message) : base(message) { }
    }

    public class ARTForbiddenException : ARTException
    {
        public ARTForbiddenException(string message) : base($"Forbidden: {message}") { }
    }

    public class ARTAuthenticationException : ARTException
    {
        public ARTAuthenticationException(string message) : base($"Auth failed: {message}") { }
    }

    public class ARTInvalidPathException : ARTException
    {
        public ARTInvalidPathException(string message) : base($"Invalid path: {message}") { }
    }

    public class ARTNotConnectedException : ARTException
    {
        public ARTNotConnectedException() : base("Not connected") { }
    }

    public class ARTEncryptionException : ARTException
    {
        public ARTEncryptionException(string message) : base($"Encryption error: {message}") { }
    }

    public class ARTDecryptionException : ARTException
    {
        public ARTDecryptionException(string message) : base($"Decryption error: {message}") { }
    }

    public class ARTTimeoutException : ARTException
    {
        public ARTTimeoutException(string message) : base($"Timeout: {message}") { }
    }

    public class ARTServerException : ARTException
    {
        public ARTServerException(string message) : base($"Server error: {message}") { }
    }

    public class ARTChannelNotFoundException : ARTException
    {
        public ARTChannelNotFoundException(string channel) : base($"Channel not found: {channel}") { }
    }

    public class ARTAckTimeoutException : ARTException
    {
        public ARTAckTimeoutException() : base("ACK timeout") { }
    }

    /// <summary>
    /// A failure of the client's connection to the ART server.
    /// <para>
    /// It does not mean that a server-side execution failed: once the client is
    /// disconnected it cannot tell whether a submitted request completed.
    /// </para>
    /// <para>
    /// Delivered by the <c>connectionFailed</c> event, and thrown by connection
    /// waits, sends and control requests once the connection has failed. After a
    /// failure the SDK stops reconnecting on its own; fix the cause and call
    /// <c>Adk.Connect()</c> again. See <see cref="ConnectionErrorCode"/> for the
    /// possible <see cref="Code"/> values.
    /// </para>
    /// </summary>
    // public class ConnectionError : ARTException
    // {
    //     /// <summary>Machine-readable reason, e.g. <c>CONNECTION_LOST</c>.</summary>
    //     public string Code { get; }

    //     /// <summary>HTTP status behind the failure, when there was one.</summary>
    //     public int? Status { get; }

    //     public ConnectionError(string message, string code, int? status = null) : base(message)
    //     {
    //         Code = code;
    //         Status = status;
    //     }

    //     public override string ToString()
    //     {
    //         return Status.HasValue
    //             ? $"{Code} ({Status.Value}): {Message}"
    //             : $"{Code}: {Message}";
    //     }
    // }

    /// <summary>
    /// Machine-readable <see cref="ConnectionError.Code"/> values.
    /// </summary>
    public static class ConnectionErrorCode
    {
        /// <summary>The connection or a control request did not become ready in time.</summary>
        public const string ConnectionTimeout = "CONNECTION_TIMEOUT";
        /// <summary>The connection dropped before a message could be sent, or could not be restored.</summary>
        public const string ConnectionLost = "CONNECTION_LOST";
        /// <summary>The app closed the connection.</summary>
        public const string ConnectionClosed = "CONNECTION_CLOSED";
        /// <summary>The app paused the connection.</summary>
        public const string ConnectionPaused = "CONNECTION_PAUSED";
        /// <summary>A newer connection attempt replaced this one.</summary>
        public const string ConnectionCancelled = "CONNECTION_CANCELLED";
        /// <summary>The connection's authorisation could not be renewed.</summary>
        public const string ConnectionAuthorizationFailed = "CONNECTION_AUTHORIZATION_FAILED";
        /// <summary>The credentials are missing or were rejected.</summary>
        public const string AuthenticationFailed = "AUTHENTICATION_FAILED";
        /// <summary>The server did not acknowledge a message.</summary>
        public const string AckTimeout = "ACK_TIMEOUT";
        /// <summary>A server billing limit was reached.</summary>
        public const string ConnectionMinutesLimitExceeded = "CONNECTION_MINUTES_LIMIT_EXCEEDED";
        /// <summary>A server concurrency limit was reached.</summary>
        public const string ConcurrentLimitExceeded = "CONCURRENT_LIMIT_EXCEEDED";
    }

  /// <summary>
    /// Payload of the <c>authenticationFailed</c> event.
    /// <para>
    /// Emitted when the server rejects the credentials, or when a token renewal
    /// fails while reconnecting or while renewing the connection's authorisation.
    /// Automatic reconnection stops; update the credentials and call
    /// <c>Adk.Connect()</c> again.
    /// </para>
    /// </summary>
    public class AuthenticationFailedDetail
    {
        /// <summary>
        /// HTTP status of the rejection (401 or 403), or <c>0</c> when the failure
        /// was not a credential rejection (for example a network error).
        /// </summary>
        public int Status { get; }

        /// <summary>Readable description of the failure.</summary>
        public string Error { get; }

        public AuthenticationFailedDetail(int status, string error)
        {
            Status = status;
            Error = error;
        }
    }

    /// <summary>
    /// Helpers for inspecting SDK errors.
    /// </summary>
    public static class ArtErrors
    {
        /// <summary>
        /// Whether <paramref name="error"/> is an <see cref="AuthHttpError"/> for
        /// rejected credentials (HTTP 401 or 403).
        /// </summary>
        public static bool IsTerminalAuthError(Exception error)
            => (error as AuthHttpError)?.IsTerminal == true;

        /// <summary>
        /// The readable message of an SDK error (<see cref="ConnectionError"/>,
        /// <see cref="AuthHttpError"/>, <see cref="ARTException"/>), without a kind
        /// prefix; <c>null</c> for other errors.
        /// </summary>
        public static string Message(Exception error)
        {
            switch (error)
            {
                case ConnectionError ce: return ce.Message;
                case AuthHttpError ae: return ae.Message;
                case ARTException ex: return ex.Message;
                default: return null;
            }
        }
    }
}
