using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace KioskPaymentPoc;

/// <summary>
/// Minimal real HTTP endpoint that receives payment-gateway webhooks and feeds
/// them to the <see cref="KioskController"/>. Demonstrates actual REST/webhook
/// callback handling (POST /webhook with JSON {txnId, amount, status}). Falls back
/// gracefully if the OS won't grant the URL namespace — the app is still fully
/// exercisable from the admin panel.
/// </summary>
public sealed class WebhookServer
{
    private readonly HttpListener _listener = new();
    private readonly KioskController _controller;
    private readonly Action<Action> _dispatch;   // marshal work onto the UI thread

    public string Url { get; }
    public bool Running { get; private set; }
    public event Action<string>? Info;

    public WebhookServer(KioskController controller, Action<Action> dispatch, int port = 8080)
    {
        _controller = controller;
        _dispatch = dispatch;
        Url = $"http://localhost:{port}/webhook/";
        _listener.Prefixes.Add(Url);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            Running = true;
            Info?.Invoke($"webhook endpoint listening on {Url}");
            _ = Task.Run(Loop);
        }
        catch (Exception ex)
        {
            Running = false;
            Info?.Invoke($"webhook endpoint unavailable ({ex.GetType().Name}) - use the admin panel to simulate");
        }
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                body = await reader.ReadToEndAsync();

            string responseJson = "{\"ok\":true}";
            try
            {
                var evt = JsonSerializer.Deserialize<WebhookEvent>(
                    body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (evt is not null && !string.IsNullOrWhiteSpace(evt.TxnId))
                    _dispatch(() => _controller.ProcessWebhook(evt.TxnId!, evt.Amount, evt.Status ?? "confirmed"));
            }
            catch { responseJson = "{\"ok\":false,\"error\":\"bad payload\"}"; }

            var buffer = Encoding.UTF8.GetBytes(responseJson);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = buffer.Length;
            await ctx.Response.OutputStream.WriteAsync(buffer);
            ctx.Response.Close();
        }
    }

    public void Stop()
    {
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        Running = false;
    }

    private sealed class WebhookEvent
    {
        public string? TxnId { get; set; }
        public decimal Amount { get; set; }
        public string? Status { get; set; }
    }
}
