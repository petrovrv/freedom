// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AiWorkbenchBridge
{
    // ============================================================= wire format (keep in sync with CO.Core/Protocol)

    internal static class DeviceWire
    {
        public const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

        public static string Now()
        {
            return DateTime.UtcNow.ToString(TimeFormat, CultureInfo.InvariantCulture);
        }

        public static byte[] Payload(params string[] fields)
        {
            foreach (string f in fields)
                if (f.IndexOf('\n') >= 0)
                    throw new ArgumentException("Signed field contains LF.");
            return Encoding.UTF8.GetBytes(String.Join("\n", fields));
        }

        public static string Hex(byte[] d)
        {
            StringBuilder sb = new StringBuilder(d.Length * 2);
            foreach (byte b in d)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // Signed by Bridge with the device-call key; CO Desktop installs only a verified, newer release.
        public static byte[] UpdatePayload(string version, string sha256Hex, long size)
        {
            return Payload("co.desktop.update.v1", version, sha256Hex, size.ToString(CultureInfo.InvariantCulture));
        }

        public static string Sha256Hex(string s)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] d = h.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in d)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static byte[] CallPayload(Dictionary<string, object> c)
        {
            // v1.9.2: a call with a chat id (devices 0.9.8+) is signed as v2 with the chat id last.
            string chat = S(c, "chatId");
            if (chat.Length > 0)
                return Payload("co.tool.call.v2", S(c, "callId"), S(c, "runId"), S(c, "taskId"), S(c, "deviceId"),
                    S(c, "tool"), Sha256Hex(S(c, "argsJson")), S(c, "issuedUtc"), B(c, "tainted") ? "1" : "0", chat);
            return Payload("co.tool.call.v1", S(c, "callId"), S(c, "runId"), S(c, "taskId"), S(c, "deviceId"),
                S(c, "tool"), Sha256Hex(S(c, "argsJson")), S(c, "issuedUtc"), B(c, "tainted") ? "1" : "0");
        }

        public static byte[] ResultPayload(Dictionary<string, object> r)
        {
            Dictionary<string, object> err = r.ContainsKey("error") ? r["error"] as Dictionary<string, object> : null;
            string errText = err == null ? "" : S(err, "code") + "\n" + S(err, "message");
            return Payload("co.tool.result.v1", S(r, "callId"), S(r, "runId"), S(r, "deviceId"), S(r, "tool"),
                B(r, "ok") ? "1" : "0", Sha256Hex(errText), Sha256Hex(S(r, "content")),
                B(r, "untrusted") ? "1" : "0", S(r, "issuedUtc"));
        }

        public static string S(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        public static bool B(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v;
        }
    }
}
