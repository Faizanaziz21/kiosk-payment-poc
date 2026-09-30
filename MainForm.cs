using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace KioskPaymentPoc;

/// <summary>
/// Operator/admin view for the kiosk PoC: shows the live customer screen, the
/// state machine, gateway/LumaBooth status, a transaction log, and admin controls.
/// The interesting logic lives in <see cref="KioskController"/>; this is the shell.
/// </summary>
public sealed class MainForm : Form
{
    private readonly KioskController _ctrl = new();
    private readonly WebhookServer _server;
    private readonly System.Windows.Forms.Timer _sessionTimer = new() { Interval = 1000 };
    private int _sessionSecondsLeft;
    private string _lastPendingTxn = "";

    private readonly bool _demo;
    private readonly string? _gifDir;

    // UI
    private readonly Label _lblTitle = new();
    private readonly Label _lblState = new();
    private readonly Label _lblSub = new();
    private readonly Panel _customer = new();
    private readonly Label _lblCustomer = new();
    private readonly Label _lblStatus = new();
    private readonly DataGridView _log = new();

    public MainForm(bool demo = false, string? gifDir = null)
    {
        _demo = demo;
        _gifDir = gifDir;

        Text = "Kiosk Payment Controller  —  LumaBooth Integration (PoC)";
        Width = 980; Height = 640;
        StartPositionCenter();
        Font = new Font("Segoe UI", 9f);
        BuildUi();

        _ctrl.Changed += OnControllerChanged;
        _ctrl.Logged += OnControllerLogged;
        _sessionTimer.Tick += OnSessionTick;

        // Real webhook endpoint (best-effort); marshal callbacks onto the UI thread.
        _server = new WebhookServer(_ctrl, Dispatch);
        _server.Info += msg => Dispatch(() => AddInfo(msg));
        if (_gifDir == null) _server.Start();

        RefreshUi();

        if (_gifDir != null)
        {
            Shown += (_, _) => RunCapture(_gifDir);
            return;
        }
    }

    private void StartPositionCenter() => StartPosition = FormStartPosition.CenterScreen;

    private void BuildUi()
    {
        // header
        var top = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(17, 24, 39) };
        _lblTitle.Text = "Kiosk Payment Controller";
        _lblTitle.Font = new Font("Segoe UI Semibold", 13.5f);
        _lblTitle.ForeColor = Color.White;
        _lblTitle.AutoSize = true; _lblTitle.Location = new Point(18, 12);
        _lblState.Font = new Font("Segoe UI Semibold", 12f);
        _lblState.AutoSize = true; _lblState.Location = new Point(18, 44);
        _lblSub.ForeColor = Color.FromArgb(156, 163, 175);
        _lblSub.AutoSize = true; _lblSub.Location = new Point(18, 70);
        top.Controls.Add(_lblTitle); top.Controls.Add(_lblState); top.Controls.Add(_lblSub);

        // customer screen (left)
        var left = new Panel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(16) };
        _customer.Dock = DockStyle.Top; _customer.Height = 210;
        _customer.BackColor = Color.FromArgb(37, 99, 235);
        _lblCustomer.Dock = DockStyle.Fill;
        _lblCustomer.TextAlign = ContentAlignment.MiddleCenter;
        _lblCustomer.Font = new Font("Segoe UI Semibold", 15f);
        _lblCustomer.ForeColor = Color.White;
        _customer.Controls.Add(_lblCustomer);
        _lblStatus.Dock = DockStyle.Fill;
        _lblStatus.Font = new Font("Consolas", 9.5f);
        _lblStatus.Padding = new Padding(2, 12, 2, 2);
        left.Controls.Add(_lblStatus);
        left.Controls.Add(_customer);

        // transaction log (fill)
        var logHeader = new Label
        {
            Dock = DockStyle.Top, Height = 26, Text = "  Transaction log",
            Font = new Font("Segoe UI Semibold", 9.5f), TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(238, 241, 245)
        };
        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true; _log.AllowUserToAddRows = false; _log.RowHeadersVisible = false;
        _log.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _log.BackgroundColor = Color.White; _log.BorderStyle = BorderStyle.None;
        _log.EnableHeadersVisualStyles = false;
        _log.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(238, 241, 245);
        _log.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f);
        _log.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 253);
        _log.Columns.Add("t", "Time");
        _log.Columns.Add("ev", "Event");
        _log.Columns.Add("id", "Txn ID");
        _log.Columns.Add("amt", "Amount");
        _log.Columns[0].FillWeight = 16; _log.Columns[1].FillWeight = 46;
        _log.Columns[2].FillWeight = 26; _log.Columns[3].FillWeight = 12;

        var center = new Panel { Dock = DockStyle.Fill };
        center.Controls.Add(_log); center.Controls.Add(logHeader);

        // admin controls (bottom)
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 96, BackColor = Color.FromArgb(245, 247, 250) };
        int x = 16, y1 = 12, y2 = 52, h = 32;
        Button B(string text, int bx, int by, int bw, EventHandler on)
        {
            var b = new Button { Text = text, Location = new Point(bx, by), Size = new Size(bw, h) };
            b.Click += on; bottom.Controls.Add(b); return b;
        }
        B("Customer: PAY NOW", x, y1, 150, (_, _) => { _lastPendingTxn = _ctrl.StartPayment(); });
        B("Gateway webhook ✓", x + 158, y1, 150, (_, _) => _ctrl.ProcessWebhook(_lastPendingTxn, _ctrl.Price, "confirmed"));
        B("Duplicate webhook", x + 316, y1, 150, (_, _) => _ctrl.ProcessWebhook(_lastPendingTxn, _ctrl.Price, "confirmed"));
        B("LumaBooth fail next", x + 474, y1, 150, (_, _) => { _ctrl.SimulateLumaBoothFailure = true; AddInfo("next session will fail (test recovery)"); });
        B("Retry session", x + 632, y1, 150, (_, _) => _ctrl.RetryFailedSession());
        B("Lock", x, y2, 90, (_, _) => _ctrl.ManualLock());
        B("Unlock", x + 98, y2, 90, (_, _) => _ctrl.ManualUnlock());
        B("Payments on/off", x + 196, y2, 130, (_, _) => _ctrl.SetPaymentsEnabled(!_ctrl.PaymentsEnabled));
        B("Price +1 QAR", x + 334, y2, 120, (_, _) => _ctrl.SetPrice(_ctrl.Price + 1));

        Controls.Add(center);
        Controls.Add(left);
        Controls.Add(top);
        Controls.Add(bottom);
    }

    // ---- controller events (marshalled) ----

    private void OnControllerChanged()
    {
        if (InvokeRequired) { BeginInvoke(RefreshUi); return; }
        RefreshUi();
    }

    private void OnControllerLogged(TxnLogEntry e)
    {
        if (InvokeRequired) { BeginInvoke(() => AddLogRow(e)); return; }
        AddLogRow(e);
    }

    private void OnSessionTick(object? sender, EventArgs e)
    {
        if (--_sessionSecondsLeft <= 0)
        {
            _sessionTimer.Stop();
            _ctrl.CompleteSession();
        }
        else RefreshUi();
    }

    // ---- rendering ----

    private void RefreshUi()
    {
        (string label, Color color) = _ctrl.State switch
        {
            KioskState.Ready            => ("READY", Color.FromArgb(37, 99, 235)),
            KioskState.PaymentPending   => ("PAYMENT PENDING", Color.FromArgb(217, 119, 6)),
            KioskState.PaymentConfirmed => ("PAYMENT CONFIRMED", Color.FromArgb(22, 163, 74)),
            KioskState.SessionStarting  => ("SESSION STARTING", Color.FromArgb(124, 58, 237)),
            KioskState.SessionActive    => ("SESSION ACTIVE", Color.FromArgb(124, 58, 237)),
            KioskState.SessionComplete  => ("SESSION COMPLETE", Color.FromArgb(22, 163, 74)),
            _                           => ("—", Color.Gray)
        };
        _lblState.Text = "● " + label;
        _lblState.ForeColor = color;
        _lblSub.Text = $"Gateway: {(_ctrl.GatewayConnected ? "connected" : "offline")}    "
                     + $"LumaBooth: {(_ctrl.LumaBoothLocked ? "LOCKED" : "UNLOCKED")}    "
                     + $"Webhook: {(_server?.Running == true ? _server.Url : "admin-simulated")}";

        _customer.BackColor = color;
        _lblCustomer.Text = _ctrl.State switch
        {
            KioskState.Ready            => $"Photo Session\n{_ctrl.Price:0} {_ctrl.Currency}\n\n[  PAY NOW  ]",
            KioskState.PaymentPending   => "Waiting for payment…\n\nComplete payment\non your card/app",
            KioskState.PaymentConfirmed => "Payment successful —\nGet ready!",
            KioskState.SessionStarting  => "Starting your\nphoto session…",
            KioskState.SessionActive    => $"📸  Session in progress\n\n{_sessionSecondsLeft}s left",
            KioskState.SessionComplete  => "Thank you!\nPrinting your photos…",
            _                           => ""
        };

        _lblStatus.Text =
            $"State        : {_ctrl.State}\n" +
            $"Payments     : {(_ctrl.PaymentsEnabled ? "ENABLED" : "DISABLED")}\n" +
            $"Price        : {_ctrl.Price:0} {_ctrl.Currency}\n" +
            $"LumaBooth    : {(_ctrl.LumaBoothLocked ? "locked" : "unlocked")}\n" +
            $"Last Txn ID  : {(_ctrl.CurrentTxnId ?? "-")}\n" +
            $"Last Amount  : {_ctrl.LastAmount:0} {_ctrl.Currency}\n" +
            $"Last Result  : {_ctrl.LastResult}";

        // auto-run the timed session when it becomes active
        if (_ctrl.State == KioskState.SessionActive && !_sessionTimer.Enabled && _gifDir == null)
        {
            _sessionSecondsLeft = 6;
            _sessionTimer.Start();
        }
    }

    private void AddLogRow(TxnLogEntry e)
    {
        int i = _log.Rows.Add(e.Time.ToString("HH:mm:ss"), e.Event,
                              string.IsNullOrEmpty(e.TxnId) ? "-" : e.TxnId,
                              e.Amount > 0 ? $"{e.Amount:0} {_ctrl.Currency}" : "-");
        if (e.Event.Contains("DUPLICATE")) _log.Rows[i].DefaultCellStyle.ForeColor = Color.FromArgb(220, 38, 38);
        else if (e.Event.Contains("confirmed") || e.Event.Contains("complete") || e.Event.Contains("delivered"))
            _log.Rows[i].DefaultCellStyle.ForeColor = Color.FromArgb(22, 163, 74);
        if (_log.Rows.Count > 0) _log.FirstDisplayedScrollingRowIndex = _log.Rows.Count - 1;
    }

    private void AddInfo(string msg) =>
        AddLogRow(new TxnLogEntry { Time = DateTime.Now, Event = msg, TxnId = "", Amount = 0, State = _ctrl.State });

    private void Dispatch(Action a)
    {
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    // ---- capture (for the demo GIF) ----

    private void RunCapture(string dir)
    {
        Directory.CreateDirectory(dir);
        int f = 0;
        void Shot() { SaveFrame(Path.Combine(dir, $"frame_{f++:00}.png")); Thread.Sleep(80); }

        RefreshUi(); Application.DoEvents(); Shot();                         // READY
        _lastPendingTxn = _ctrl.StartPayment(); Application.DoEvents(); Shot(); // PAYMENT PENDING
        _ctrl.ProcessWebhook(_lastPendingTxn, _ctrl.Price, "confirmed");     // → confirmed → session active
        _sessionSecondsLeft = 6; RefreshUi(); Application.DoEvents(); Shot();
        _sessionSecondsLeft = 3; RefreshUi(); Application.DoEvents(); Shot(); // SESSION ACTIVE
        _ctrl.CompleteSession(); Application.DoEvents(); Shot();             // COMPLETE → READY
        _ctrl.ProcessWebhook(_lastPendingTxn, _ctrl.Price, "confirmed");     // DUPLICATE → ignored
        RefreshUi(); Application.DoEvents(); Shot();
        Close();
    }

    private void SaveFrame(string path)
    {
        using var bmp = new Bitmap(ClientSize.Width, ClientSize.Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
        bmp.Save(path, ImageFormat.Png);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _server?.Stop();
        base.OnFormClosed(e);
    }
}
