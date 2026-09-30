using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KioskPaymentPoc;

public enum KioskState
{
    Ready,
    PaymentPending,
    PaymentConfirmed,
    SessionStarting,
    SessionActive,
    SessionComplete
}

public sealed class TxnLogEntry
{
    public DateTime Time { get; init; }
    public string Event { get; init; } = "";
    public string TxnId { get; init; } = "";
    public decimal Amount { get; init; }
    public KioskState State { get; init; }
}

/// <summary>
/// Core kiosk logic: the payment→session state machine, webhook handling with
/// idempotency (one confirmed payment == exactly one photo session, duplicate
/// callbacks are ignored), a mocked LumaBooth lock/unlock, persistent processed-
/// transaction state, and a transaction log. UI-agnostic; raises Changed/Logged
/// so any front-end (WinForms here) can render it.
/// </summary>
public sealed class KioskController
{
    private readonly string _stateFile = Path.Combine(AppContext.BaseDirectory, "kiosk_state.json");

    private readonly HashSet<string> _processedTxns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TxnLogEntry> _log = new();

    public KioskState State { get; private set; } = KioskState.Ready;
    public decimal Price { get; private set; } = 15m;          // QAR
    public string Currency => "QAR";
    public bool PaymentsEnabled { get; private set; } = true;
    public bool LumaBoothLocked { get; private set; } = true;  // starts on the lock screen
    public bool GatewayConnected { get; private set; } = true;
    public string? CurrentTxnId { get; private set; }
    public decimal LastAmount { get; private set; }
    public string LastResult { get; private set; } = "";

    /// <summary>When true, the next session start is forced to fail (to show recovery/retry).</summary>
    public bool SimulateLumaBoothFailure { get; set; }

    public IReadOnlyList<TxnLogEntry> Log => _log;

    public event Action? Changed;
    public event Action<TxnLogEntry>? Logged;

    public KioskController() => LoadState();

    // ---- customer / gateway entry points ----

    /// <summary>Customer taps PAY NOW. Creates a pending transaction and shows the gateway screen.</summary>
    public string StartPayment()
    {
        if (!PaymentsEnabled) { Note("payments disabled", "", 0); return ""; }
        if (State != KioskState.Ready) { Note("busy - payment ignored", CurrentTxnId ?? "", 0); return ""; }

        CurrentTxnId = "PMT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        LastAmount = Price;
        Transition(KioskState.PaymentPending);
        Note("payment requested", CurrentTxnId, Price);
        return CurrentTxnId;
    }

    /// <summary>
    /// Handles a payment-gateway webhook/callback. This is the idempotency heart:
    /// a transaction id that was already processed can NEVER start a second session.
    /// </summary>
    public void ProcessWebhook(string txnId, decimal amount, string status)
    {
        if (string.IsNullOrWhiteSpace(txnId)) return;

        if (_processedTxns.Contains(txnId))
        {
            Note("DUPLICATE webhook ignored", txnId, amount);   // <-- no second session
            return;
        }

        if (!string.Equals(status, "confirmed", StringComparison.OrdinalIgnoreCase))
        {
            Note($"webhook '{status}' - not confirmed", txnId, amount);
            return;
        }

        // Mark processed BEFORE acting, and persist, so a crash or duplicate
        // that races in cannot double-spend the payment.
        _processedTxns.Add(txnId);
        SaveState();

        CurrentTxnId = txnId;
        LastAmount = amount;
        Transition(KioskState.PaymentConfirmed);
        Note("payment confirmed", txnId, amount);

        StartSession(txnId, amount);
    }

    // ---- session lifecycle (mock LumaBooth) ----

    private void StartSession(string txnId, decimal amount)
    {
        Transition(KioskState.SessionStarting);

        // Mock LumaBooth API: ExitLockScreen + StartPrintSession
        if (SimulateLumaBoothFailure)
        {
            Note("LumaBooth failed to start - will retry", txnId, amount);
            LastResult = "session start failed";
            OnChanged();
            return;   // session is retryable via RetryFailedSession(); payment stays consumed but undelivered
        }

        LumaBoothLocked = false;
        Note("LumaBooth: ExitLockScreen + StartPrintSession", txnId, amount);
        Transition(KioskState.SessionActive);
        LastResult = "session active";
    }

    /// <summary>Called by the UI when the (timed) photo session finishes.</summary>
    public void CompleteSession()
    {
        if (State != KioskState.SessionActive) return;
        Transition(KioskState.SessionComplete);
        Note("session complete", CurrentTxnId ?? "", LastAmount);

        LumaBoothLocked = true;                                  // lock booth again
        Note("LumaBooth locked - payment screen restored", CurrentTxnId ?? "", LastAmount);
        LastResult = "delivered";
        Transition(KioskState.Ready);
    }

    /// <summary>Recovery path: retry a payment that was confirmed but whose session never started.</summary>
    public void RetryFailedSession()
    {
        if (State != KioskState.SessionStarting || CurrentTxnId is null) return;
        Note("retrying failed session", CurrentTxnId, LastAmount);
        SimulateLumaBoothFailure = false;
        StartSession(CurrentTxnId, LastAmount);
    }

    // ---- admin ----

    public void SetPrice(decimal price) { if (price > 0) { Price = price; SaveState(); Note($"price set to {price} {Currency}", "", price); } }
    public void SetPaymentsEnabled(bool on) { PaymentsEnabled = on; Note(on ? "payments enabled" : "payments disabled", "", 0); }
    public void ManualLock() { LumaBoothLocked = true; Note("admin: manual lock", "", 0); OnChanged(); }
    public void ManualUnlock() { LumaBoothLocked = false; Note("admin: manual unlock", "", 0); OnChanged(); }

    // ---- persistence ----

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile)) return;
            var s = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_stateFile));
            if (s is null) return;
            Price = s.Price > 0 ? s.Price : Price;
            foreach (var id in s.ProcessedTxns ?? Enumerable.Empty<string>())
                _processedTxns.Add(id);
        }
        catch { /* start clean if state is unreadable */ }
    }

    private void SaveState()
    {
        try
        {
            var s = new Persisted { Price = Price, ProcessedTxns = _processedTxns.ToList() };
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best-effort */ }
    }

    private sealed class Persisted
    {
        public decimal Price { get; set; }
        public List<string>? ProcessedTxns { get; set; }
    }

    // ---- helpers ----

    private void Transition(KioskState next) { State = next; OnChanged(); }

    private void Note(string ev, string txnId, decimal amount)
    {
        var entry = new TxnLogEntry { Time = DateTime.Now, Event = ev, TxnId = txnId, Amount = amount, State = State };
        _log.Add(entry);
        Logged?.Invoke(entry);
    }

    private void OnChanged() => Changed?.Invoke();
}
