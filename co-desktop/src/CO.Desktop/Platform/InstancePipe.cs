// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace CO.Desktop.Platform;

// Freedom F14e.2: a second start of CO Desktop (a task of the jump list "--app <module>/<page>", a pinned app window, a
// shortcut) hands its request to the running copy over a pipe of this Windows user and exits. Requests: "show" (bring
// the main window) and "app <module>/<page>"; one short line each.
internal static class InstancePipe
{
    private const int MaxLine = 256;

    private static string Name => "CO.Desktop.Open." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    /// <summary>Sends one request to the running copy; false when there is none that answers.</summary>
    public static bool Send(string line)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            c.Connect(3000);
            var b = Encoding.UTF8.GetBytes(line.Length > MaxLine ? line[..MaxLine] : line);
            c.Write(b, 0, b.Length);
            c.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Log.Write("instance pipe send: " + ex.Message);
            return false;
        }
    }

    /// <summary>Serves requests of later starts until the app exits; each line goes to onRequest.</summary>
    public static async Task ListenAsync(Action<string> onRequest, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var s = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await s.WaitForConnectionAsync(ct);
                var buf = new byte[MaxLine];
                int n = 0;
                using var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
                read.CancelAfter(TimeSpan.FromSeconds(3));
                while (n < buf.Length)
                {
                    int r = await s.ReadAsync(buf.AsMemory(n), read.Token);
                    if (r == 0) break;
                    n += r;
                }
                string line = Encoding.UTF8.GetString(buf, 0, n).Trim();
                if (line.Length > 0) onRequest(line);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log.Write("instance pipe: " + ex.Message);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
