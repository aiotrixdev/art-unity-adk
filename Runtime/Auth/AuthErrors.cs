using System;

namespace ART.ADK
{
    /// <summary>
    /// An HTTP-level authentication failure (C# port of the Kotlin/TS AuthHttpError).
    /// Status is the HTTP status code; Body is the parsed error body, if any
    /// (e.g. a Newtonsoft JObject).
    /// </summary>
    internal class AuthHttpError : ARTAuthenticationException
    {
        public int Status { get; }
        public object Body { get; }

        public Boolean IsTerminal {get; set;}

        public AuthHttpError(string message, int status, object body = null, Boolean isTerminal = false) : base(message)
        {
            Status = status;
            Body = body;
            IsTerminal = isTerminal;
        }
    }

    internal static class AuthErrors
    {
        /// <summary>
        /// A terminal auth error is one that retrying cannot fix: the credentials
        /// or grant are rejected outright, so the connection must stop instead of
        /// looping. Port of the Kotlin/TS isTerminalAuthError.
        ///
        /// NOTE: 401 (unauthorized) and 403 (forbidden) are the classic terminal
        /// cases. If your auth.ts treats other statuses (e.g. 400 invalid_grant)
        /// as terminal, widen the check here to match it exactly.
        /// </summary>
        public static bool IsTerminalAuthError(object err)
        {
            return err is AuthHttpError e && (e.Status == 401 || e.Status == 403);
        }
    }
}
