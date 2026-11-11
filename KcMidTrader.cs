// =====================================================================================
//  KcMidTrader.cs  -  NinjaTrader 8 indicator
//
//  Conceptualized and developed with AI by Rangel Alvarado.
//
//  Adds a "KC MIDBAND" section to the bottom of Chart Trader that lets you:
//    * set a quantity (default 1)
//    * set a ticks offset from the midband (buy above, sell below; 0 keeps the mid)
//    * show or hide that offset zone (light blue above, light red below)
//    * BUY or SELL at the selected line plus that offset with one click (Midline by default)
//    * keep those orders ATTACHED to the offset price (they follow the selected line as it moves)
//    * CANCEL every order this tool placed with one click
//
//  The section is only shown while a (visible) stock Keltner Channel is on the chart.
//  The midband is recomputed internally with the SAME Period / Offset multiplier as the
//  Keltner Channel found on the chart, so values match what you see.
//
//  Install: copy to  Documents\NinjaTrader 8\bin\Custom\Indicators\  and compile (F5)
//           in the NinjaScript Editor. See the PDF manual for step-by-step instructions.
//
//  IMPORTANT: Chart Trader UI injection is not officially supported by NinjaTrader.
//             Test on the Sim101 account before using it on a live account.
// =====================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// Enums live in their own namespace so NinjaScript's generated code never has to resolve them.
namespace NinjaTrader.NinjaScript.KcMidTraderTypes
{
    public enum KcCrossBehavior
    {
        // Midband on the "wrong" side of the market: do not place / pause tracking.
        Block,
        // Midband on the "wrong" side of the market: use a Stop Market order at the midband.
        SwitchToStopMarket
    }

    public enum KcTrackMode
    {
        // Re-price working orders once per bar, using the midband of the bar that just closed.
        OnBarClose,
        // Re-price working orders whenever the (tick-rounded) midband changes.
        OnPriceChange
    }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class KcMidTrader : Indicator
    {
        // ---------------------------------------------------------------- tracked orders
        private class TrackedOrder
        {
            public Order        Order;
            public Account      Account;
            public OrderAction  Action;
            public int          Quantity;
            public NinjaTrader.NinjaScript.AtmStrategy AtmTemplate;
            public string       AtmName;          // template name captured at submit; selector objects are reused
            public bool         FollowAtm;        // unfilled (NF) entry is re-submitted when the ATM selector changes
            public string       Label;            // Ord-xxxxxx-MMM-dd-yy_HH-mm-ss-fff (checkbox key; list text adds side, qty, F/NF)
            public bool         Track;
            public bool         PendingReplace;   // cancel sent, waiting to re-submit
            public bool         SwapAtm;          // re-submit uses ReplaceAtmName (empty = no ATM)
            public string       ReplaceAtmName;
            public NinjaTrader.NinjaScript.AtmStrategy ReplaceAtm;
            public double       ReplacePrice;
            public bool         Paused;           // tracking paused because price crossed market (Block mode)
            public bool         EntryFilled;      // at least one fill; stop/target may now exist
            public bool         SelectedForAtm;   // user opted this filled order into ATM changes
            public double       FillPrice;
            public NinjaTrader.NinjaScript.StrategyBase AtmOwner;
            public readonly List<Order> StopOrders = new List<Order>();
            public readonly List<Order> TargetOrders = new List<Order>();
        }

        // A Keltner-type indicator found on the chart, and which of its plots is the midband.
        private class KcSource
        {
            public NinjaTrader.Gui.NinjaScript.IndicatorRenderBase Indicator;
            public int    MidPlot;     // index into Values[]; -1 = use the middle value of the first 3 plots
            public string Label;
        }

        private readonly object             sync    = new object();
        private readonly List<TrackedOrder> tracked = new List<TrackedOrder>();
        private readonly List<Account>      hookedAccounts = new List<Account>();
        private readonly Dictionary<string, bool> checkedLabels = new Dictionary<string, bool>();

        // ---------------------------------------------------------------- market / KC state
        private volatile bool kcFound;
        private double lastMid   = double.NaN;
        private double lastAsk   = double.NaN;
        private double lastBid   = double.NaN;
        private double lastPrice = double.NaN;
        private double shownMid  = double.NaN;
        private int    shownCount = -1;
        private bool   trackAll  = true;
        private volatile int  ticksOffset;     // Chart Trader offset, already trimmed to the ceiling
        private volatile bool showZone = true;

        // ---------------------------------------------------------------- UI
        private NinjaTrader.Gui.Chart.Chart               chartWindow;
        private System.Windows.Controls.Grid              chartTraderGrid;
        private System.Windows.Controls.Grid              panel;
        private System.Windows.Controls.RowDefinition     panelRow;
        private System.Windows.Controls.TextBlock         midText;
        private System.Windows.Controls.TextBlock         statusText;
        private NinjaTrader.Gui.Tools.QuantityUpDown      qtySelector;   // native NT quantity control (preferred)
        private System.Windows.Controls.TextBox           qtyBox;        // fallback if the native control can't be created
        private System.Windows.Controls.CheckBox          trackBox;
        private System.Windows.Controls.CheckBox          toggleTrackBox; // unlocks the Track list so another indicator can be chosen
        private bool                                      trackPickerOn;  // false = Track stays on the Keltner (or the last choice)
        private System.Windows.Controls.TextBox           offsetBox;
        private System.Windows.Controls.Button            offsetMinusButton, offsetPlusButton;
        private System.Windows.Controls.CheckBox          showZoneBox;
        private System.Windows.Controls.ComboBox          kcCombo;        // which Keltner Channel to track
        private System.Windows.Controls.ComboBox          lineCombo;      // which plot of that channel
        private System.Windows.Controls.ScrollViewer      orderListHost;
        private System.Windows.Controls.StackPanel        orderListPanel;
        private List<KcSource> kcList = new List<KcSource>();
        private List<int>      linePlots = new List<int>();
        private volatile KcSource selectedKc;
        private volatile int   selectedPlot = int.MinValue;   // int.MinValue = not chosen yet
        private string selectedLineName = "Midline";
        private bool   suppressComboEvent;
        private bool   suppressLineEvent;
        private string lastDiagnostic;
        private string orderListSig;
        private Brush  panelBrush;
        private NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector atmSelector;
        private int    suppressAtmEvents;
        private int    inPrune;
        private System.Windows.Controls.Button            buyButton, sellButton, cancelButton, minusButton, plusButton;
        private bool panelInserted;
        private bool tabHooked;

        // =================================================================================
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description              = "Adds Buy/Sell @ Keltner midband, auto-tracking and Cancel buttons to Chart Trader. "
                                         + "Conceptualized and developed with AI by Rangel Alvarado.";
                Name                     = "KcMidTrader";
                Calculate                = Calculate.OnPriceChange;
                IsOverlay                = true;
                DisplayInDataBox         = false;
                DrawOnPricePanel         = false;
                PaintPriceMarkers        = false;
                IsSuspendedWhileInactive = false;   // keep tracking even if the chart tab is not visible

                IndicatorNameFilter      = "Keltner";
                DefaultQuantity          = 1;
                TrackByDefault           = true;
                TrackMode                = NinjaTrader.NinjaScript.KcMidTraderTypes.KcTrackMode.OnPriceChange;
                CrossBehavior            = NinjaTrader.NinjaScript.KcMidTraderTypes.KcCrossBehavior.Block;
                OrderTif                 = TimeInForce.Gtc;
                UseChartTraderAtm        = true;
                AdoptExistingOrders      = true;
                MaxTicksOffset           = 100;
            }
            else if (State == State.Historical)
            {
                trackAll    = TrackByDefault;
                ticksOffset = 0;
                showZone    = true;
                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() =>
                    {
                        CreateUi();
                        if (TabSelected())
                            InsertUi();
                        DetectKeltner();
                    });
                }
            }
            else if (State == State.Realtime)
            {
                if (AdoptExistingOrders)
                    AdoptOrders();
                if (ChartControl != null)
                    ChartControl.Dispatcher.InvokeAsync(() => DetectKeltner());
            }
            else if (State == State.Terminated)
            {
                UnhookAccounts();
                // Orders are intentionally NOT cancelled here: indicators are reloaded often
                // (F5, adding indicators...). Re-adoption picks them up again on reload.
                if (ChartControl != null)
                {
                    ChartControl.Dispatcher.InvokeAsync(() =>
                    {
                        RemoveUi();
                        if (tabHooked && chartWindow != null && chartWindow.MainTabControl != null)
                            chartWindow.MainTabControl.SelectionChanged -= OnTabChanged;
                        tabHooked = false;
                        UnhookButtons();
                    });
                }
            }
        }

        // =================================================================================
        //  Data
        // =================================================================================
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType == MarketDataType.Ask)       lastAsk   = e.Price;
            else if (e.MarketDataType == MarketDataType.Bid)  lastBid   = e.Price;
            else if (e.MarketDataType == MarketDataType.Last) lastPrice = e.Price;
        }

        protected override void OnBarUpdate()
        {
            lastPrice = Close[0];

            if (State == State.Realtime && IsFirstTickOfBar && ChartControl != null)
                ChartControl.Dispatcher.InvokeAsync(() =>
                {
                    DetectKeltner();
                    if (atmSelector == null) HookAtmSelector();
                });

            KcSource src = selectedKc;
            if (!kcFound || src == null)
            {
                if (State == State.Realtime) ProcessReplacementsAndPrune();
                return;
            }

            // The midband is read straight from the selected indicator's own plot, so it always
            // matches what is drawn on the chart (any Keltner variant, any settings, any input).
            double rawNow;
            if (!TryReadMid(src, CurrentBar, out rawNow))
                return;
            double midNow = Instrument.MasterInstrument.RoundToTickSize(rawNow);
            lastMid = midNow;

            if (State != State.Realtime)
                return;

            UpdateReadout();
            ProcessReplacementsAndPrune();

            if (TrackMode == NinjaTrader.NinjaScript.KcMidTraderTypes.KcTrackMode.OnBarClose)
            {
                if (!IsFirstTickOfBar)
                    return;
                double rawPrev;
                if (TryReadMid(src, CurrentBar - 1, out rawPrev))
                    TrackOrders(Instrument.MasterInstrument.RoundToTickSize(rawPrev));
            }
            else
            {
                TrackOrders(midNow);
            }
        }

        // Latest valid midband value at or just before barIndex (an indicator calculated
        // "On bar close" has no value yet on the bar that is still forming).
        private bool TryReadMid(KcSource src, int barIndex, out double value)
        {
            value = double.NaN;
            int plot = ActivePlot(src);
            try
            {
                for (int i = barIndex; i >= 0 && i >= barIndex - 2; i--)
                {
                    if (plot >= 0)
                    {
                        if (src.Indicator.Values == null || plot >= src.Indicator.Values.Length) return false;
                        Series<double> ser = src.Indicator.Values[plot];
                        if (i < ser.Count && ser.IsValidDataPointAt(i))
                        {
                            value = ser.GetValueAt(i);
                            return !double.IsNaN(value) && value > 0;
                        }
                    }
                    else
                    {
                        // Unknown plot names: midband = the middle of the first three plots.
                        if (src.Indicator.Values.Length < 3) return false;
                        double[] v = new double[3];
                        bool ok = true;
                        for (int p = 0; p < 3 && ok; p++)
                        {
                            Series<double> ser = src.Indicator.Values[p];
                            ok = i < ser.Count && ser.IsValidDataPointAt(i);
                            if (ok) v[p] = ser.GetValueAt(i);
                        }
                        if (ok)
                        {
                            Array.Sort(v);
                            value = v[1];
                            return !double.IsNaN(value) && value > 0;
                        }
                    }
                }
            }
            catch (Exception ex) { Print("KcMidTrader read error: " + ex.Message); }
            return false;
        }

        // =================================================================================
        //  Order logic
        // =================================================================================

        // Limit when the midband is on the "passive" side of the market, StopMarket when it is on
        // the other side, null when it is exactly at the market (would fill immediately).
        private OrderType? DesiredType(bool isBuy, double price, double refPx)
        {
            int cmp = price.ApproxCompare(refPx);
            if (cmp == 0) return null;
            if (isBuy)  return cmp < 0 ? OrderType.Limit : OrderType.StopMarket;
            return            cmp > 0 ? OrderType.Limit : OrderType.StopMarket;
        }

        private double RefPrice(bool isBuy)
        {
            double px = isBuy ? lastAsk : lastBid;
            if (double.IsNaN(px) || px <= 0) px = lastPrice;
            return px;
        }

        // MaxTicksOffset is only a typing ceiling. The offset itself is the trimmed Chart Trader value.
        private int OffsetCeiling()
        {
            return Math.Max(0, MaxTicksOffset);
        }

        private int ClampOffset(int n)
        {
            if (n < 0) return 0;
            int ceiling = OffsetCeiling();
            return n > ceiling ? ceiling : n;
        }

        // Buy sits n ticks above the mid; sell sits n ticks below. n = 0 keeps the midband price.
        private double OffsetPrice(bool isBuy, double mid)
        {
            int n = ClampOffset(ticksOffset);
            double tick = TickSize;
            if (tick <= 0 || n == 0) return mid;
            double px = mid + (isBuy ? n : -n) * tick;
            return Instrument.MasterInstrument.RoundToTickSize(px);
        }

        // Plot the data thread should read. int.MinValue means the source's own midline.
        private int ActivePlot(KcSource src)
        {
            int plot = selectedPlot;
            if (plot == int.MinValue) return src == null ? -1 : src.MidPlot;
            return plot;
        }

        // Returns a message for the status line. label null creates a new Ord- id.
        private string Submit(Account acct, OrderAction action, int qty, double price,
                              NinjaTrader.NinjaScript.AtmStrategy atm, bool track, string label, bool followAtm, string atmName)
        {
            bool   isBuy = action == OrderAction.Buy;
            double refPx = RefPrice(isBuy);
            if (double.IsNaN(refPx))
                return "No market data yet.";

            OrderType? type = DesiredType(isBuy, price, refPx);
            if (type == null)
                return "Order price is at the market - not placed.";
            if (type.Value == OrderType.StopMarket &&
                CrossBehavior == NinjaTrader.NinjaScript.KcMidTraderTypes.KcCrossBehavior.Block)
                return string.Format("Blocked: order price {0} is {1} the market (a limit would fill now).",
                    Instrument.MasterInstrument.FormatPrice(price), isBuy ? "above" : "below");

            double lmt = type.Value == OrderType.Limit      ? price : 0;
            double stp = type.Value == OrderType.StopMarket ? price : 0;
            if (string.IsNullOrEmpty(label))
                label = NewOrderLabel();

            // null atmName means "read it now". A stored name is used as-is, including "" for no ATM.
            // StartAtmStrategy(string) wants the template name. The selector's object is often the
            // live instance ("Template - 1"), and passing that object does not retarget the entry.
            string chosenAtm = atmName ?? AtmTemplateKey(atm);
            if (chosenAtm == null) chosenAtm = "";

            Order o;
            try
            {
                if (chosenAtm.Length > 0)
                {
                    // ATM entry orders must be named "Entry". The Ord- label stays on our tracked order.
                    o = acct.CreateOrder(Instrument, action, type.Value, OrderEntry.Manual, OrderTif, qty,
                                         lmt, stp, string.Empty, "Entry", NinjaTrader.Core.Globals.MaxDate, null);
                    System.Threading.Interlocked.Increment(ref suppressAtmEvents);
                    try { NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(chosenAtm, o); }
                    finally { System.Threading.Interlocked.Decrement(ref suppressAtmEvents); }
                }
                else
                {
                    o = acct.CreateOrder(Instrument, action, type.Value, OrderEntry.Manual, OrderTif, qty,
                                         lmt, stp, string.Empty, OrderNamePrefix + (isBuy ? "Buy" : "Sell"),
                                         NinjaTrader.Core.Globals.MaxDate, null);
                    acct.Submit(new[] { o });
                }
            }
            catch (Exception ex)
            {
                Print("KcMidTrader submit error: " + ex.Message);
                return label + "  Error: " + ex.Message;
            }

            HookAccount(acct);
            lock (sync)
            {
                TrackedOrder t = new TrackedOrder();
                t.Order = o; t.Account = acct; t.Action = action; t.Quantity = qty;
                t.AtmTemplate = atm; t.AtmName = chosenAtm; t.Track = track; t.Label = label;
                t.FollowAtm = chosenAtm.Length > 0 || followAtm;
                bool selected;
                if (checkedLabels.TryGetValue(label, out selected))
                    t.SelectedForAtm = selected;
                tracked.Add(t);
            }

            return string.Format("{0}  {1} {2} {3} @ {4}{5}",
                label,
                isBuy ? "BUY" : "SELL", qty,
                type.Value == OrderType.Limit ? "LMT" : "STP",
                Instrument.MasterInstrument.FormatPrice(price),
                track ? " (tracking)" : "");
        }

        private static bool IsChangeable(Order o)
        {
            return o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
                || o.OrderState == OrderState.PartFilled;
        }

        // Handles cancel/replace hand-offs and removes finished orders from the list.
        private void ProcessReplacementsAndPrune()
        {
            if (System.Threading.Interlocked.CompareExchange(ref inPrune, 1, 0) != 0) return;
            try
            {
            List<TrackedOrder> toResubmit = new List<TrackedOrder>();
            string msg = null;

            lock (sync)
            {
                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    TrackedOrder t = tracked[i];
                    if (t.Order == null) { tracked.RemoveAt(i); continue; }
                    OrderState st = t.Order.OrderState;

                    NoteFill(t);

                    if (t.PendingReplace)
                    {
                        if (st == OrderState.Cancelled && t.Order.Filled <= 0)
                        {
                            toResubmit.Add(t);
                            tracked.RemoveAt(i);
                        }
                        else if (st == OrderState.Filled || (st == OrderState.Cancelled && t.Order.Filled > 0))
                        {
                            // Filled before the cancel landed: do NOT re-submit. Keep it if an ATM still protects it.
                            t.PendingReplace = false;
                            t.SwapAtm = false;
                            msg = (t.Label ?? "Order") + " filled before replace - left on its current ATM.";
                        }
                        else if (st == OrderState.Rejected)
                        {
                            tracked.RemoveAt(i);
                            msg = (t.Label ?? "Order") + " rejected before replace - nothing re-submitted.";
                        }
                        continue;
                    }

                    if (Order.IsTerminalState(st))
                    {
                        if (st == OrderState.Rejected)
                        {
                            msg = (t.Label ?? "Order") + " rejected - see the Log tab.";
                            tracked.RemoveAt(i);
                            continue;
                        }
                        // A fill stays on the list as F (or NF while a partial is still open).
                        // Only a cancel that never filled is dropped.
                        if (t.Order.Filled <= 0)
                            tracked.RemoveAt(i);
                    }
                }
            }

            foreach (TrackedOrder t in toResubmit)
            {
                int remaining = t.Quantity - t.Order.Filled;
                if (remaining <= 0) continue;
                NinjaTrader.NinjaScript.AtmStrategy atm = t.SwapAtm ? t.ReplaceAtm : t.AtmTemplate;
                string atmName = t.SwapAtm ? (t.ReplaceAtmName ?? "") : (t.AtmName ?? "");
                msg = "Replaced: " + Submit(t.Account, t.Action, remaining, t.ReplacePrice, atm, t.Track, t.Label, t.FollowAtm, atmName);
            }

            if (msg != null) SetStatus(msg);
            UpdateReadout();
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref inPrune, 0);
            }
        }

        private void TrackOrders(double mid)
        {
            if (double.IsNaN(mid)) return;

            List<TrackedOrder> snapshot;
            lock (sync) snapshot = tracked.Where(x => x.Track && !x.PendingReplace).ToList();

            foreach (TrackedOrder t in snapshot)
            {
                Order o = t.Order;
                if (!IsChangeable(o)) continue;

                bool   isBuy   = t.Action == OrderAction.Buy;
                double target  = OffsetPrice(isBuy, mid);
                double current = o.OrderType == OrderType.Limit ? o.LimitPrice : o.StopPrice;
                OrderType? want = DesiredType(isBuy, target, RefPrice(isBuy));

                if (want != null && want.Value == o.OrderType)
                {
                    t.Paused = false;
                    if (current.ApproxCompare(target) == 0) continue;
                    try
                    {
                        if (o.OrderType == OrderType.Limit) o.LimitPriceChanged = target;
                        else                                o.StopPriceChanged  = target;
                        t.Account.Change(new[] { o });
                    }
                    catch (Exception ex) { Print("KcMidTrader change error: " + ex.Message); }
                    continue;
                }

                // Offset price crossed to the other side of the market (or sits on it).
                if (want == null || CrossBehavior == NinjaTrader.NinjaScript.KcMidTraderTypes.KcCrossBehavior.Block)
                {
                    if (!t.Paused) SetStatus("Tracking paused: order price crossed the market.");
                    t.Paused = true;
                    continue;
                }

                // SwitchToStopMarket: cancel, then re-submit with the other type once the cancel is confirmed.
                lock (sync) { t.PendingReplace = true; t.SwapAtm = false; t.ReplacePrice = target; }
                try { t.Account.Cancel(new[] { o }); }
                catch (Exception ex) { Print("KcMidTrader cancel error: " + ex.Message); }
            }
        }

        // Picks up working orders placed by this tool before a reload/restart.
        // (ATM entries are named "Entry" by NinjaTrader and cannot be identified, so they are not adopted.)
        private void AdoptOrders()
        {
            try
            {
                lock (Account.All)
                {
                    foreach (Account acct in Account.All)
                    {
                        Order[] orders;
                        lock (acct.Orders) orders = acct.Orders.ToArray();
                        foreach (Order o in orders)
                        {
                            if (o.Instrument == null || o.Instrument.FullName != Instrument.FullName) continue;
                            if (o.Name == null || !o.Name.StartsWith(OrderNamePrefix)) continue;
                            if (Order.IsTerminalState(o.OrderState)) continue;
                            lock (sync)
                            {
                                if (tracked.Any(x => x.Order == o)) continue;
                                TrackedOrder t = new TrackedOrder();
                                t.Order = o; t.Account = acct; t.Action = o.OrderAction;
                                t.Quantity = o.Quantity; t.Track = trackAll;
                                t.Label = LabelFromExistingOrder(o);
                                tracked.Add(t);
                                HookAccount(acct);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Print("KcMidTrader adopt error: " + ex.Message); }
        }

        private int CancelAll()
        {
            List<TrackedOrder> list = new List<TrackedOrder>();
            lock (sync)
            {
                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    TrackedOrder t = tracked[i];
                    if (t.Order == null || Order.IsTerminalState(t.Order.OrderState)) continue;
                    t.PendingReplace = false;   // a manual cancel must not be re-submitted
                    t.SwapAtm = false;
                    list.Add(t);
                    if (t.Order.Filled <= 0)
                        tracked.RemoveAt(i);
                }
            }

            int n = 0;
            foreach (TrackedOrder t in list)
            {
                try { t.Account.Cancel(new[] { t.Order }); n++; }
                catch (Exception ex) { Print("KcMidTrader cancel error: " + ex.Message); }
            }
            return n;
        }

        // =================================================================================
        //  Keltner detection (UI thread)
        // =================================================================================
        private void DetectKeltner()
        {
            List<KcSource> found = new List<KcSource>();
            List<string>   onChart = new List<string>();
            string filter = string.IsNullOrWhiteSpace(IndicatorNameFilter) ? "Keltner" : IndicatorNameFilter.Trim();
            try
            {
                if (ChartControl != null && ChartControl.Indicators != null)
                {
                    foreach (var item in ChartControl.Indicators)
                    {
                        NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind =
                            item as NinjaTrader.Gui.NinjaScript.IndicatorRenderBase;
                        if (ind == null || ReferenceEquals(ind, this)) continue;

                        string typeName = ind.GetType().Name;
                        string name     = ind.Name ?? typeName;
                        onChart.Add(name);

                        // Default list is the name filter (Keltner). Toggle track opens the list to every
                        // visible indicator. The current choice stays listed after the list is locked again.
                        bool matches = name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       typeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                        bool keepCurrent = selectedKc != null && ReferenceEquals(ind, selectedKc.Indicator);
                        if (!trackPickerOn && !matches && !keepCurrent)
                            continue;
                        if (!ind.IsVisible) continue;

                        int midPlot = FindMidPlot(ind);
                        if (midPlot == -2) continue;   // no usable plots

                        KcSource src = new KcSource();
                        src.Indicator = ind;
                        src.MidPlot   = midPlot;
                        src.Label     = string.Format("#{0}  {1}", found.Count + 1, SafeDisplayName(ind));
                        found.Add(src);
                    }
                }
            }
            catch (Exception ex) { Print("KcMidTrader detect error: " + ex.Message); }

            // Rebuild the selector only if the set of indicators (or their settings/labels) changed.
            bool changed = found.Count != kcList.Count;
            if (!changed)
                for (int i = 0; i < found.Count; i++)
                    if (!ReferenceEquals(found[i].Indicator, kcList[i].Indicator) || found[i].Label != kcList[i].Label)
                    { changed = true; break; }

            if (changed)
            {
                KcSource prev = selectedKc;
                kcList = found;
                // Keep the user's choice if that indicator is still on the chart; otherwise use the first listed.
                KcSource keep = prev == null ? null : kcList.FirstOrDefault(x => ReferenceEquals(x.Indicator, prev.Indicator));
                selectedKc = keep ?? (kcList.Count > 0 ? kcList[0] : null);
                RefreshCombo();
                RefreshLineCombo(keep == null);
            }

            kcFound = selectedKc != null;

            // Help in the NinjaScript Output window when nothing matches.
            string diag = kcFound ? null
                : string.Format("KcMidTrader: no visible indicator containing '{0}' found. Indicators on this chart: {1}",
                                filter, onChart.Count == 0 ? "(none)" : string.Join(", ", onChart));
            if (diag != null && diag != lastDiagnostic) Print(diag);
            lastDiagnostic = diag;

            if (panel != null)
                panel.Visibility = kcFound ? Visibility.Visible : Visibility.Collapsed;
        }

        // Index of the midband plot: by name (mid/middle/center/basis...), else stock order (Midline first
        // when names are Midline/Upper/Lower), else -1 = middle of the first three plots, -2 = unusable.
        private static int FindMidPlot(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind)
        {
            int n = ind.Values == null ? 0 : ind.Values.Length;
            if (n == 0) return -2;
            string[] keys = { "mid", "center", "centre", "basis", "middle", "average", "ma" };
            if (ind.Plots != null)
            {
                foreach (string key in keys)
                    for (int i = 0; i < ind.Plots.Length && i < n; i++)
                    {
                        string pn = ind.Plots[i] != null ? (ind.Plots[i].Name ?? "") : "";
                        bool hit = key == "ma" ? pn.Equals("ma", StringComparison.OrdinalIgnoreCase)
                                               : pn.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (hit) return i;
                    }
            }
            if (n >= 3) return -1;
            return n == 1 ? 0 : -2;
        }

        private static string SafeDisplayName(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind)
        {
            try
            {
                string d = ind.DisplayName;
                if (!string.IsNullOrWhiteSpace(d)) return d;
            }
            catch { }
            return ind.Name ?? ind.GetType().Name;
        }

        private void RefreshCombo()
        {
            if (kcCombo == null) return;
            suppressComboEvent = true;
            try
            {
                kcCombo.Items.Clear();
                foreach (KcSource k in kcList)
                    kcCombo.Items.Add(k.Label);
                KcSource sel = selectedKc;
                kcCombo.SelectedIndex = sel != null ? kcList.IndexOf(sel) : -1;
                // Locked until Toggle track is checked, so the Keltner choice cannot change by accident.
                kcCombo.IsEnabled = trackPickerOn && kcList.Count > 0;
            }
            finally { suppressComboEvent = false; }
        }

        private void OnKcComboChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            e.Handled = true;   // don't let the event bubble up to Chart Trader / tab control
            if (suppressComboEvent || kcCombo == null) return;
            int idx = kcCombo.SelectedIndex;
            if (idx < 0 || idx >= kcList.Count) return;
            selectedKc = kcList[idx];
            kcFound    = true;
            lastMid    = double.NaN;   // readout refreshes on the next tick with the new line
            RefreshLineCombo(true);
            SetStatusNow("Tracking " + kcList[idx].Label + ". Line reset to " + selectedLineName + ".");
            RereadAndTrack();
        }

        // Re-scan the chart every time the list is opened, so it always reflects what's on the chart.
        private void OnKcComboOpened(object sender, EventArgs e)
        {
            DetectKeltner();
        }

        // resetToMidline is true when the tracked indicator changed. The same indicator keeps the user's line.
        private void RefreshLineCombo(bool resetToMidline)
        {
            if (lineCombo == null) return;
            KcSource src = selectedKc;
            List<int> plots = new List<int>();
            List<string> names = new List<string>();
            int mid = src == null ? -2 : src.MidPlot;

            if (src != null && src.Indicator != null && src.Indicator.Plots != null && src.Indicator.Values != null)
            {
                int n = Math.Min(src.Indicator.Plots.Length, src.Indicator.Values.Length);
                if (mid < 0 && n >= 3)
                {
                    plots.Add(-1);
                    names.Add("Midline");
                }
                for (int i = 0; i < n; i++)
                {
                    string pn = src.Indicator.Plots[i] != null ? src.Indicator.Plots[i].Name : null;
                    if (string.IsNullOrWhiteSpace(pn)) pn = "Plot " + (i + 1);
                    plots.Add(i);
                    names.Add(pn);
                }
            }

            int prefer = selectedPlot;
            if (resetToMidline || prefer == int.MinValue || !plots.Contains(prefer))
            {
                if (plots.Count == 0) prefer = int.MinValue;
                else if (mid >= 0 && plots.Contains(mid)) prefer = mid;
                else prefer = plots[0];
            }

            suppressLineEvent = true;
            try
            {
                lineCombo.Items.Clear();
                foreach (string name in names)
                    lineCombo.Items.Add(name);
                linePlots = plots;
                int sel = plots.IndexOf(prefer);
                lineCombo.SelectedIndex = sel;
                lineCombo.IsEnabled = plots.Count > 1;
                selectedPlot = sel >= 0 ? plots[sel] : int.MinValue;
                selectedLineName = sel >= 0 ? names[sel] : "Midline";
            }
            finally { suppressLineEvent = false; }

            ApplyLineButtons(selectedLineName);
        }

        private void OnLineComboChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            e.Handled = true;
            if (suppressLineEvent || lineCombo == null) return;
            int idx = lineCombo.SelectedIndex;
            if (idx < 0 || idx >= linePlots.Count) return;
            selectedPlot = linePlots[idx];
            selectedLineName = lineCombo.SelectedItem as string ?? "Line";
            ApplyLineButtons(selectedLineName);
            lastMid = double.NaN;
            SetStatusNow("Tracking " + selectedLineName + ".");
            RereadAndTrack();
        }

        private void ApplyLineButtons(string lineName)
        {
            if (string.IsNullOrWhiteSpace(lineName)) lineName = "Line";
            if (buyButton != null) buyButton.Content = "Buy @ " + lineName;
            if (sellButton != null) sellButton.Content = "Sell @ " + lineName;
        }

        // Read the selected plot now and re-price working orders. Safe to call from the UI thread.
        private void RereadAndTrack()
        {
            KcSource src = selectedKc;
            if (src == null || CurrentBar < 0) return;
            double raw;
            try
            {
                if (!TryReadMid(src, CurrentBar, out raw)) return;
            }
            catch { return; }
            lastMid = Instrument.MasterInstrument.RoundToTickSize(raw);
            if (State == State.Realtime)
                TrackOrders(lastMid);
            UpdateReadout();
            if (ChartControl != null) ChartControl.InvalidateVisual();
        }

        // =================================================================================
        //  UI (all methods below run on the UI thread)
        // =================================================================================
        private void CreateUi()
        {
            if (panel != null) return;

            chartWindow = Window.GetWindow(ChartControl.Parent) as NinjaTrader.Gui.Chart.Chart;
            if (chartWindow == null) return;

            NinjaTrader.Gui.Chart.ChartTrader ct =
                chartWindow.FindFirst("ChartWindowChartTraderControl") as NinjaTrader.Gui.Chart.ChartTrader;
            if (ct == null) return;

            chartTraderGrid = ct.Content as System.Windows.Controls.Grid;
            if (chartTraderGrid == null) return;

            Style btnStyle = Application.Current.TryFindResource("BasicEntryButton") as Style;
            Brush fg = (Application.Current.TryFindResource("FontControlBrush") as Brush) ?? Brushes.Gainsboro;
            panelBrush = fg;

            panel = new System.Windows.Controls.Grid();
            panel.Margin = new Thickness(0, 6, 0, 2);
            panel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            panel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            // 0 header, 1 track, 2 line, 3 qty, 4 offset, 5 show zone, 6 track line,
            // 7 toggle track, 8 buy/sell, 9 cancel, 10 status, 11 order list (last in the group).
            for (int i = 0; i < 12; i++)
                panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            // Row 0: header + live midband
            System.Windows.Controls.TextBlock header = new System.Windows.Controls.TextBlock
            {
                Text = "KC MIDBAND", Foreground = fg, FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 2, 2, 2), VerticalAlignment = VerticalAlignment.Center
            };
            midText = new System.Windows.Controls.TextBlock
            {
                Text = "--", Foreground = fg, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(2, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center
            };
            Place(header, 0, 0, 1); Place(midText, 0, 1, 1);

            // Row 1: which Keltner Channel to track (defaults to the first one listed on the chart)
            System.Windows.Controls.Grid kcRow = new System.Windows.Controls.Grid { Margin = new Thickness(1, 1, 1, 3) };
            kcRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            kcRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.TextBlock kcLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Track", Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 6, 0)
            };
            kcCombo = new System.Windows.Controls.ComboBox
            {
                Height = 24, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                IsEnabled = false,
                ToolTip = "Indicator the orders follow. Locked on the Keltner channel until Toggle track is checked."
            };
            kcCombo.SelectionChanged += OnKcComboChanged;
            kcCombo.DropDownOpened   += OnKcComboOpened;
            System.Windows.Controls.Grid.SetColumn(kcLabel, 0);
            System.Windows.Controls.Grid.SetColumn(kcCombo, 1);
            kcRow.Children.Add(kcLabel); kcRow.Children.Add(kcCombo);
            Place(kcRow, 1, 0, 2);

            // Row 2: which plot to follow. Midline is the default.
            System.Windows.Controls.Grid lineRow = new System.Windows.Controls.Grid { Margin = new Thickness(1, 1, 1, 3) };
            lineRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            lineRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.TextBlock lineLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Line", Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 6, 0)
            };
            lineCombo = new System.Windows.Controls.ComboBox
            {
                Height = 24, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Plot the KC orders follow. Midline is selected by default."
            };
            lineCombo.SelectionChanged += OnLineComboChanged;
            System.Windows.Controls.Grid.SetColumn(lineLabel, 0);
            System.Windows.Controls.Grid.SetColumn(lineCombo, 1);
            lineRow.Children.Add(lineLabel); lineRow.Children.Add(lineCombo);
            Place(lineRow, 2, 0, 2);

            // Rows 3-4: quantity selector, then track checkbox
            // Preferred: NinjaTrader's own QuantityUpDown (same control as Chart Trader's "Order qty"),
            // so the arrows, mouse wheel and typing behave exactly like the native box.
            UIElement qtyElement = null;
            try
            {
                qtySelector = new NinjaTrader.Gui.Tools.QuantityUpDown();
                qtySelector.Value   = Math.Max(1, DefaultQuantity);
                qtySelector.Height  = 24;
                qtySelector.Margin  = new Thickness(1);
                qtySelector.ToolTip = "Contracts for KC midband orders";
                qtyElement = qtySelector;
            }
            catch (Exception ex)
            {
                Print("KcMidTrader: native quantity control unavailable, using fallback (" + ex.Message + ")");
                qtySelector = null;
            }

            if (qtySelector == null)
            {
                // Fallback: own spinner with explicit colors/sizes so NinjaTrader's skin can't hide or clip it.
                System.Windows.Controls.Grid qtyPanel = new System.Windows.Controls.Grid { Margin = new Thickness(1) };
                qtyPanel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(26) });
                qtyPanel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                qtyPanel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(26) });

                minusButton = MakeSpinButton("−");
                plusButton  = MakeSpinButton("+");
                qtyBox = new System.Windows.Controls.TextBox
                {
                    Text = Math.Max(1, DefaultQuantity).ToString(), Height = 24, MinWidth = 0,
                    TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x38)),
                    Foreground = Brushes.White, CaretBrush = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x5F, 0x6E)),
                    ToolTip = "Contracts for KC midband orders (type, mouse wheel, or Up/Down keys)"
                };
                qtyBox.PreviewKeyDown   += OnQtyKeyDown;
                qtyBox.PreviewTextInput += OnQtyTextInput;
                qtyBox.MouseWheel       += OnQtyWheel;
                minusButton.Click       += OnMinusClick;
                plusButton.Click        += OnPlusClick;

                System.Windows.Controls.Grid.SetColumn(minusButton, 0);
                System.Windows.Controls.Grid.SetColumn(qtyBox, 1);
                System.Windows.Controls.Grid.SetColumn(plusButton, 2);
                qtyPanel.Children.Add(minusButton); qtyPanel.Children.Add(qtyBox); qtyPanel.Children.Add(plusButton);
                qtyElement = qtyPanel;
            }

            // Quantity gets the FULL panel width so it is never clipped in a narrow Chart Trader.
            System.Windows.Controls.Grid qtyRow = new System.Windows.Controls.Grid();
            qtyRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            qtyRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.TextBlock qtyLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Qty", Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0)
            };
            FrameworkElement qfe = qtyElement as FrameworkElement;
            if (qfe != null) { qfe.HorizontalAlignment = HorizontalAlignment.Stretch; qfe.MinWidth = 0; }
            System.Windows.Controls.Grid.SetColumn(qtyLabel, 0);
            System.Windows.Controls.Grid.SetColumn(qtyElement, 1);
            qtyRow.Children.Add(qtyLabel); qtyRow.Children.Add(qtyElement);
            Place(qtyRow, 3, 0, 2);

            // Row 3: ticks offset. The number is the zone width; MaxTicksOffset only trims a typed value above the ceiling.
            System.Windows.Controls.Grid offsetRow = new System.Windows.Controls.Grid();
            offsetRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            offsetRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.TextBlock offsetLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Offset", Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0)
            };
            System.Windows.Controls.Grid offsetSpin = new System.Windows.Controls.Grid { Margin = new Thickness(1) };
            offsetSpin.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(26) });
            offsetSpin.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            offsetSpin.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(26) });
            offsetMinusButton = MakeSpinButton("−");
            offsetPlusButton  = MakeSpinButton("+");
            offsetBox = new System.Windows.Controls.TextBox
            {
                Text = "0", Height = 24, MinWidth = 0,
                TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x38)),
                Foreground = Brushes.White, CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x5F, 0x6E)),
                ToolTip = "Ticks from the selected line. Buys sit this many ticks above it; sells sit this many below. 0 uses the line. A typed value above Max ticks offset is trimmed to that ceiling."
            };
            offsetBox.PreviewKeyDown   += OnOffsetKeyDown;
            offsetBox.PreviewTextInput += OnOffsetTextInput;
            offsetBox.MouseWheel       += OnOffsetWheel;
            offsetBox.LostFocus        += OnOffsetLostFocus;
            offsetMinusButton.Click    += OnOffsetMinusClick;
            offsetPlusButton.Click     += OnOffsetPlusClick;
            System.Windows.Controls.Grid.SetColumn(offsetMinusButton, 0);
            System.Windows.Controls.Grid.SetColumn(offsetBox, 1);
            System.Windows.Controls.Grid.SetColumn(offsetPlusButton, 2);
            offsetSpin.Children.Add(offsetMinusButton);
            offsetSpin.Children.Add(offsetBox);
            offsetSpin.Children.Add(offsetPlusButton);
            System.Windows.Controls.Grid.SetColumn(offsetLabel, 0);
            System.Windows.Controls.Grid.SetColumn(offsetSpin, 1);
            offsetRow.Children.Add(offsetLabel);
            offsetRow.Children.Add(offsetSpin);
            Place(offsetRow, 4, 0, 2);

            // Rows 5-6: drawing toggle, then tracking toggle. Orders still use the offset when Show zone is off.
            showZoneBox = new System.Windows.Controls.CheckBox
            {
                Content = "Show zone", IsChecked = true, Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 1, 1, 1),
                ToolTip = "Show the buy zone above the selected line (light blue) and the sell zone below it (light red)"
            };
            trackBox = new System.Windows.Controls.CheckBox
            {
                Content = "Track line", IsChecked = TrackByDefault, Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 1, 1, 1),
                ToolTip = "Keep KC orders attached to the offset price as the selected line moves"
            };
            toggleTrackBox = new System.Windows.Controls.CheckBox
            {
                Content = "Toggle track", IsChecked = false, Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 1, 1, 1),
                ToolTip = "Unlock Track so you can pick any indicator on the chart. Uncheck to lock the current choice. Does not turn order tracking on or off."
            };
            showZoneBox.Click += OnShowZoneClick;
            trackBox.Click += OnTrackClick;
            toggleTrackBox.Click += OnToggleTrackClick;
            Place(showZoneBox, 5, 0, 2);
            Place(trackBox, 6, 0, 2);
            Place(toggleTrackBox, 7, 0, 2);

            // Row 8: Buy / Sell. The caption is updated when the line changes.
            buyButton  = MakeButton("Buy @ Midline",  btnStyle, Color.FromRgb(0x1E, 0x7A, 0x3C));
            sellButton = MakeButton("Sell @ Midline", btnStyle, Color.FromRgb(0xA3, 0x24, 0x24));
            Place(buyButton, 8, 0, 1); Place(sellButton, 8, 1, 1);

            // Row 9: Cancel
            cancelButton = MakeButton("Cancel KC Orders", btnStyle, Color.FromRgb(0x4A, 0x4F, 0x5A));
            Place(cancelButton, 9, 0, 2);

            // Row 10: status
            statusText = new System.Windows.Controls.TextBlock
            {
                Text = "Ready", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 2, 2)
            };
            Place(statusText, 10, 0, 2);

            // Last in the group. Light fill so sell red (#A32424) stays readable.
            orderListPanel = new System.Windows.Controls.StackPanel();
            orderListHost = new System.Windows.Controls.ScrollViewer
            {
                Content = orderListPanel,
                MinHeight = 36,
                MaxHeight = 110,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                Background = Brushes.White
            };
            System.Windows.Controls.Border orderListBorder = new System.Windows.Controls.Border
            {
                Child = orderListHost,
                MinHeight = 40,
                Margin = new Thickness(2, 2, 2, 2),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC5, 0xCB, 0xD3)),
                Background = Brushes.White
            };
            Place(orderListBorder, 11, 0, 2);

            buyButton.Click    += OnBuyClick;
            sellButton.Click   += OnSellClick;
            cancelButton.Click += OnCancelClick;

            panel.Visibility = Visibility.Collapsed;   // shown only when a Keltner Channel is found

            if (chartWindow.MainTabControl != null)
            {
                chartWindow.MainTabControl.SelectionChanged += OnTabChanged;
                tabHooked = true;
            }

            HookAtmSelector();
            RefreshOrderList();
        }

        private System.Windows.Controls.Button MakeButton(string text, Style style, Color bg)
        {
            System.Windows.Controls.Button b = new System.Windows.Controls.Button();
            if (style != null) b.Style = style;
            b.Content    = text;
            b.Height     = 26;
            b.Margin     = new Thickness(1);
            b.Background = new SolidColorBrush(bg);
            b.Foreground = Brushes.White;
            return b;
        }

        // Small button whose look is fully explicit (no NinjaTrader style), so its text is always visible.
        private System.Windows.Controls.Button MakeSpinButton(string text)
        {
            System.Windows.Controls.Button b = new System.Windows.Controls.Button();
            b.Style       = null;
            b.Content     = text;
            b.Width       = 26;
            b.Height      = 24;
            b.MinWidth    = 0;
            b.Padding     = new Thickness(0);
            b.FontSize    = 15;
            b.FontWeight  = FontWeights.Bold;
            b.Background  = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50));
            b.Foreground  = Brushes.White;
            b.BorderBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x5F, 0x6E));
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            b.VerticalContentAlignment   = VerticalAlignment.Center;
            b.Focusable   = false;
            return b;
        }

        private void Place(UIElement el, int row, int col, int span)
        {
            System.Windows.Controls.Grid.SetRow(el, row);
            System.Windows.Controls.Grid.SetColumn(el, col);
            System.Windows.Controls.Grid.SetColumnSpan(el, span);
            panel.Children.Add(el);
        }

        private void InsertUi()
        {
            if (panel == null || panelInserted || chartTraderGrid == null) return;
            panelRow = new System.Windows.Controls.RowDefinition { Height = GridLength.Auto };
            chartTraderGrid.RowDefinitions.Add(panelRow);
            System.Windows.Controls.Grid.SetRow(panel, chartTraderGrid.RowDefinitions.Count - 1);
            System.Windows.Controls.Grid.SetColumnSpan(panel, Math.Max(1, chartTraderGrid.ColumnDefinitions.Count));
            chartTraderGrid.Children.Add(panel);
            panelInserted = true;
        }

        private void RemoveUi()
        {
            if (!panelInserted || chartTraderGrid == null) return;
            chartTraderGrid.Children.Remove(panel);
            if (panelRow != null) chartTraderGrid.RowDefinitions.Remove(panelRow);
            panelInserted = false;
        }

        private void UnhookButtons()
        {
            if (buyButton    != null) buyButton.Click    -= OnBuyClick;
            if (sellButton   != null) sellButton.Click   -= OnSellClick;
            if (cancelButton != null) cancelButton.Click -= OnCancelClick;
            if (minusButton  != null) minusButton.Click  -= OnMinusClick;
            if (plusButton   != null) plusButton.Click   -= OnPlusClick;
            if (offsetMinusButton != null) offsetMinusButton.Click -= OnOffsetMinusClick;
            if (offsetPlusButton  != null) offsetPlusButton.Click  -= OnOffsetPlusClick;
            if (trackBox     != null) trackBox.Click     -= OnTrackClick;
            if (toggleTrackBox != null) toggleTrackBox.Click -= OnToggleTrackClick;
            if (showZoneBox  != null) showZoneBox.Click  -= OnShowZoneClick;
            if (kcCombo != null)
            {
                kcCombo.SelectionChanged -= OnKcComboChanged;
                kcCombo.DropDownOpened   -= OnKcComboOpened;
            }
            if (lineCombo != null)
                lineCombo.SelectionChanged -= OnLineComboChanged;
            if (atmSelector != null)
            {
                atmSelector.SelectionChanged -= OnAtmSelectionChanged;
                atmSelector = null;
            }
            if (qtyBox != null)
            {
                qtyBox.PreviewKeyDown   -= OnQtyKeyDown;
                qtyBox.PreviewTextInput -= OnQtyTextInput;
                qtyBox.MouseWheel       -= OnQtyWheel;
            }
            if (offsetBox != null)
            {
                offsetBox.PreviewKeyDown   -= OnOffsetKeyDown;
                offsetBox.PreviewTextInput -= OnOffsetTextInput;
                offsetBox.MouseWheel       -= OnOffsetWheel;
                offsetBox.LostFocus        -= OnOffsetLostFocus;
            }
        }

        private bool TabSelected()
        {
            if (chartWindow == null || chartWindow.MainTabControl == null) return false;
            foreach (System.Windows.Controls.TabItem tab in chartWindow.MainTabControl.Items)
            {
                NinjaTrader.Gui.Chart.ChartTab ct = tab.Content as NinjaTrader.Gui.Chart.ChartTab;
                if (ct != null && ct.ChartControl == ChartControl && tab == chartWindow.MainTabControl.SelectedItem)
                    return true;
            }
            return false;
        }

        private void OnTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count <= 0) return;
            System.Windows.Controls.TabItem tab = e.AddedItems[0] as System.Windows.Controls.TabItem;
            if (tab == null) return;   // event bubbled up from a ComboBox etc.
            NinjaTrader.Gui.Chart.ChartTab ct = tab.Content as NinjaTrader.Gui.Chart.ChartTab;
            if (ct == null) return;
            if (ct.ChartControl == ChartControl) InsertUi(); else RemoveUi();
        }

        // ---------------------------------------------------------------- Chart Trader selections
        private Account SelectedAccount()
        {
            if (chartWindow == null) return null;
            AccountSelector sel = chartWindow.FindFirst("ChartTraderControlAccountSelector") as AccountSelector;
            return sel != null ? sel.SelectedAccount : null;
        }

        private NinjaTrader.NinjaScript.AtmStrategy SelectedAtm()
        {
            if (chartWindow == null) return null;
            NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector sel =
                chartWindow.FindFirst("ChartTraderControlATMStrategySelector")
                    as NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector;
            return sel != null ? sel.SelectedAtmStrategy : null;
        }

        private int ReadQty()
        {
            if (qtySelector != null)
            {
                int v = qtySelector.Value;
                if (v < 1) { v = 1; qtySelector.Value = 1; }
                return v;
            }
            if (qtyBox == null) return Math.Max(1, DefaultQuantity);
            int q;
            if (!int.TryParse(qtyBox.Text, out q) || q < 1) q = 1;
            qtyBox.Text = q.ToString();
            return q;
        }

        private void StepQty(int delta)
        {
            if (qtySelector != null) { qtySelector.Value = Math.Max(1, ReadQty() + delta); return; }
            if (qtyBox != null) qtyBox.Text = Math.Max(1, ReadQty() + delta).ToString();
        }

        // Fallback box: Up/Down keys step the value; the event is marked handled so the chart
        // (which uses keystrokes for its own shortcuts) does not swallow them.
        private void OnQtyKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up)        { StepQty(+1); e.Handled = true; }
            else if (e.Key == Key.Down) { StepQty(-1); e.Handled = true; }
            else if (e.Key == Key.Enter || e.Key == Key.Escape) { ReadQty(); Keyboard.ClearFocus(); e.Handled = true; }
        }

        private void OnQtyTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
                if (!char.IsDigit(c)) { e.Handled = true; return; }
        }

        private void OnQtyWheel(object sender, MouseWheelEventArgs e)
        {
            StepQty(e.Delta > 0 ? +1 : -1);
            e.Handled = true;
        }

        // ---------------------------------------------------------------- handlers
        private void OnBuyClick(object sender, RoutedEventArgs e)  { PlaceFromUi(OrderAction.Buy); }
        private void OnSellClick(object sender, RoutedEventArgs e) { PlaceFromUi(OrderAction.Sell); }

        private void PlaceFromUi(OrderAction action)
        {
            DetectKeltner();
            if (!kcFound)             { SetStatusNow("No Keltner Channel on this chart."); return; }
            if (State != State.Realtime) { SetStatusNow("Waiting for real-time data..."); return; }

            double mid = lastMid;
            if (double.IsNaN(mid))    { SetStatusNow("Midband not available yet."); return; }

            Account acct = SelectedAccount();
            if (acct == null)         { SetStatusNow("Select an account in Chart Trader."); return; }

            NinjaTrader.NinjaScript.AtmStrategy atm = UseChartTraderAtm ? SelectedAtm() : null;
            int  qty   = ReadQty();
            bool track = trackBox.IsChecked == true;
            bool isBuy = action == OrderAction.Buy;

            SetStatusNow(Submit(acct, action, qty, OffsetPrice(isBuy, mid), atm, track, null, UseChartTraderAtm, null));
            UpdateReadout();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            int n = CancelAll();
            SetStatusNow(n == 0 ? "No working KC orders." : string.Format("Cancel sent for {0} order(s).", n));
            UpdateReadout();
        }

        private void OnMinusClick(object sender, RoutedEventArgs e) { StepQty(-1); }
        private void OnPlusClick(object sender, RoutedEventArgs e)  { StepQty(+1); }

        private void OnTrackClick(object sender, RoutedEventArgs e)
        {
            trackAll = trackBox.IsChecked == true;
            lock (sync) foreach (TrackedOrder t in tracked) t.Track = trackAll;
            SetStatusNow(trackAll ? "Tracking ON for KC orders." : "Tracking OFF - orders stay at their price.");
        }

        // Unlocks the Track list. The current indicator keeps being followed either way.
        private void OnToggleTrackClick(object sender, RoutedEventArgs e)
        {
            trackPickerOn = toggleTrackBox != null && toggleTrackBox.IsChecked == true;
            DetectKeltner();
            string name = selectedKc != null ? selectedKc.Label : "the current indicator";
            SetStatusNow(trackPickerOn
                ? "Track unlocked. Choose any indicator on the chart. Still on " + name + "."
                : "Track locked on " + name + ".");
        }

        private void OnOffsetMinusClick(object sender, RoutedEventArgs e) { StepOffset(-1); }
        private void OnOffsetPlusClick(object sender, RoutedEventArgs e)  { StepOffset(+1); }

        private void OnOffsetKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up)        { StepOffset(+1); e.Handled = true; }
            else if (e.Key == Key.Down) { StepOffset(-1); e.Handled = true; }
            else if (e.Key == Key.Enter || e.Key == Key.Escape)
            {
                CommitOffset();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        private void OnOffsetTextInput(object sender, TextCompositionEventArgs e)
        {
            foreach (char c in e.Text)
                if (!char.IsDigit(c)) { e.Handled = true; return; }
        }

        private void OnOffsetWheel(object sender, MouseWheelEventArgs e)
        {
            StepOffset(e.Delta > 0 ? +1 : -1);
            e.Handled = true;
        }

        private void OnOffsetLostFocus(object sender, RoutedEventArgs e)
        {
            CommitOffset();
        }

        private void OnShowZoneClick(object sender, RoutedEventArgs e)
        {
            showZone = showZoneBox != null && showZoneBox.IsChecked == true;
            if (ChartControl != null) ChartControl.InvalidateVisual();
        }

        // Applies on Enter, focus loss, spin, or wheel. A value above the ceiling is trimmed; it does not widen the zone.
        private void CommitOffset()
        {
            int parsed;
            int n = 0;
            if (offsetBox != null && int.TryParse(offsetBox.Text, out parsed))
                n = parsed;
            ApplyOffset(n);
        }

        private void StepOffset(int delta)
        {
            int current = ticksOffset;
            int parsed;
            if (offsetBox != null && int.TryParse(offsetBox.Text, out parsed))
                current = parsed;
            ApplyOffset(current + delta);
        }

        private void ApplyOffset(int n)
        {
            n = ClampOffset(n);
            bool changed = n != ticksOffset;
            ticksOffset = n;
            if (offsetBox != null && offsetBox.Text != n.ToString())
                offsetBox.Text = n.ToString();
            if (changed && !double.IsNaN(lastMid))
                TrackOrders(lastMid);
            if (changed && ChartControl != null)
                ChartControl.InvalidateVisual();
        }

        // ---------------------------------------------------------------- status / readout
        private void SetStatusNow(string msg)
        {
            if (statusText != null) statusText.Text = msg;
        }

        private void SetStatus(string msg)
        {
            if (ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() => SetStatusNow(msg));
        }

        private void UpdateReadout()
        {
            SyncOrderList();
            double mid = lastMid;
            int count;
            lock (sync) count = tracked.Count;
            if (mid.ApproxCompare(shownMid) == 0 && count == shownCount) return;
            shownMid = mid; shownCount = count;
            if (ChartControl == null) return;

            string text = double.IsNaN(mid) ? "--" : Instrument.MasterInstrument.FormatPrice(mid);
            if (count > 0) text += string.Format("  [{0}]", count);
            ChartControl.Dispatcher.InvokeAsync(() => { if (midText != null) midText.Text = text; });
        }

        // Both sides of the operation zone. Hidden when Show zone is off, the offset is 0, or there is no mid.
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!showZone || !kcFound || ChartPanel == null || chartScale == null || RenderTarget == null)
                return;

            int n = ClampOffset(ticksOffset);
            double mid = lastMid;
            double tick = TickSize;
            if (n <= 0 || double.IsNaN(mid) || mid <= 0 || tick <= 0)
                return;

            double buyPrice  = Instrument.MasterInstrument.RoundToTickSize(mid + n * tick);
            double sellPrice = Instrument.MasterInstrument.RoundToTickSize(mid - n * tick);
            PaintOffsetZone(chartScale, mid, buyPrice, true);
            PaintOffsetZone(chartScale, mid, sellPrice, false);
        }

        private void PaintOffsetZone(ChartScale chartScale, double mid, double edge, bool buySide)
        {
            float yMid  = chartScale.GetYByValue(mid);
            float yEdge = chartScale.GetYByValue(edge);

            float panelLeft   = (float)ChartPanel.X;
            float panelTop    = (float)ChartPanel.Y;
            float panelRight  = panelLeft + (float)ChartPanel.W;
            float panelBottom = panelTop + (float)ChartPanel.H;
            if (panelRight <= panelLeft || panelBottom <= panelTop)
                return;

            float top    = Math.Max(panelTop, Math.Min(yMid, yEdge));
            float bottom = Math.Min(panelBottom, Math.Max(yMid, yEdge));

            SharpDX.Color4 fillColor = buySide
                ? new SharpDX.Color4(0.45f, 0.70f, 1.00f, 0.20f)
                : new SharpDX.Color4(1.00f, 0.45f, 0.45f, 0.20f);
            SharpDX.Color4 lineColor = buySide
                ? new SharpDX.Color4(0.25f, 0.55f, 0.95f, 1.00f)
                : new SharpDX.Color4(0.90f, 0.28f, 0.28f, 1.00f);

            SharpDX.Direct2D1.SolidColorBrush fillBrush = null;
            SharpDX.Direct2D1.SolidColorBrush lineBrush = null;
            SharpDX.Direct2D1.StrokeStyle dash = null;
            try
            {
                fillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, fillColor);
                lineBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, lineColor);

                float height = bottom - top;
                if (height >= 0.5f)
                {
                    var rect = new SharpDX.RectangleF(panelLeft, top, panelRight - panelLeft, height);
                    RenderTarget.FillRectangle(rect, fillBrush);
                    RenderTarget.DrawRectangle(rect, lineBrush, 1f);
                }

                if (yEdge >= panelTop && yEdge <= panelBottom)
                {
                    var props = new SharpDX.Direct2D1.StrokeStyleProperties
                    {
                        DashStyle = SharpDX.Direct2D1.DashStyle.Dash
                    };
                    dash = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory, props);
                    RenderTarget.DrawLine(
                        new SharpDX.Vector2(panelLeft, yEdge),
                        new SharpDX.Vector2(panelRight, yEdge),
                        lineBrush, 1.5f, dash);
                }
            }
            finally
            {
                if (dash != null) dash.Dispose();
                if (lineBrush != null) lineBrush.Dispose();
                if (fillBrush != null) fillBrush.Dispose();
            }
        }

        // =================================================================================
        //  Order labels, ATM children, and Chart Trader ATM changes
        // =================================================================================
        private const string OrderNamePrefix = "KCMid";

        private static string NewOrderLabel()
        {
            return MakeOrderLabel(Guid.NewGuid().ToString("N").Substring(0, 6), DateTime.Now);
        }

        private static string LabelFromExistingOrder(Order o)
        {
            string hex = "";
            string id = o == null || o.OrderId == null ? "" : o.OrderId;
            foreach (char c in id)
            {
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
                {
                    hex += char.ToLowerInvariant(c);
                    if (hex.Length == 6) break;
                }
            }
            if (hex.Length < 6)
                hex = (hex + Guid.NewGuid().ToString("N")).Substring(0, 6);
            DateTime when = o != null ? o.Time : DateTime.Now;
            if (when.Year < 2000) when = DateTime.Now;
            return MakeOrderLabel(hex, when);
        }

        // Ord-af3658-Oct-10-26_18-35-17-560
        private static string MakeOrderLabel(string hex6, DateTime when)
        {
            return "Ord-" + hex6 + "-" + when.ToString("MMM-dd-yy_HH-mm-ss-fff", CultureInfo.InvariantCulture);
        }

        // List text color. One value is active. Switch by commenting the active value and uncommenting the other.
        private enum OrderListPalette
        {
            PanelGreenRed = 0,   // default: BUY #1E7A3C, SELL #A32424 (same greens and reds as the Buy and Sell buttons)
            // NinjaTraderBuySell = 1, // Chart Trader Buy button Green #008000, Sell button Red #FF0000
        }

        private const OrderListPalette ActiveOrderListPalette = OrderListPalette.PanelGreenRed;

        private static Color OrderListColor(bool buy)
        {
            switch (ActiveOrderListPalette)
            {
                // case OrderListPalette.NinjaTraderBuySell:
                //     return buy ? Color.FromRgb(0x00, 0x80, 0x00) : Color.FromRgb(0xFF, 0x00, 0x00);
                default:
                    return buy ? Color.FromRgb(0x1E, 0x7A, 0x3C) : Color.FromRgb(0xA3, 0x24, 0x24);
            }
        }

        private static bool IsBuyAction(OrderAction action)
        {
            return action == OrderAction.Buy || action == OrderAction.BuyToCover;
        }

        // F only when the entry is fully filled. Working, accepted, replacing, and partial stay NF.
        private static bool IsEntryFullyFilled(TrackedOrder t)
        {
            if (t == null || t.Order == null) return false;
            if (t.Order.OrderState == OrderState.Filled) return true;
            int qty = t.Quantity > 0 ? t.Quantity : t.Order.Quantity;
            return qty > 0 && t.Order.Filled >= qty;
        }

        // Ord-af3658-Oct-10-26_18-35-17-560-BUY-4-F
        private static string OrderListText(TrackedOrder t)
        {
            string side = IsBuyAction(t.Action) ? "BUY" : "SELL";
            string fill = IsEntryFullyFilled(t) ? "F" : "NF";
            return (t.Label ?? "") + "-" + side + "-" + t.Quantity + "-" + fill;
        }

        private static void NoteFill(TrackedOrder t)
        {
            if (t == null || t.Order == null || t.Order.Filled <= 0) return;
            t.EntryFilled = true;
            if (t.FillPrice <= 0 && t.Order.AverageFillPrice > 0)
                t.FillPrice = t.Order.AverageFillPrice;
        }

        private static bool KeepForProtection(TrackedOrder t)
        {
            return t.AtmTemplate != null || t.StopOrders.Count > 0 || t.TargetOrders.Count > 0;
        }

        private static bool ProtectiveFinished(TrackedOrder t)
        {
            if (t.Order == null || t.Order.OrderState != OrderState.Filled) return false;
            int n = t.StopOrders.Count + t.TargetOrders.Count;
            if (n == 0) return false;
            foreach (Order o in t.StopOrders)
                if (o != null && !Order.IsTerminalState(o.OrderState)) return false;
            foreach (Order o in t.TargetOrders)
                if (o != null && !Order.IsTerminalState(o.OrderState)) return false;
            return true;
        }

        private void HookAccount(Account acct)
        {
            if (acct == null) return;
            lock (sync)
            {
                if (hookedAccounts.Contains(acct)) return;
                hookedAccounts.Add(acct);
            }
            acct.OrderUpdate += OnAccountOrderUpdate;
        }

        private void UnhookAccounts()
        {
            Account[] accts;
            lock (sync)
            {
                accts = hookedAccounts.ToArray();
                hookedAccounts.Clear();
            }
            foreach (Account acct in accts)
            {
                try { acct.OrderUpdate -= OnAccountOrderUpdate; }
                catch { }
            }
        }

        private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
        {
            if (e == null || e.Order == null) return;
            Order o = e.Order;
            if (o.Instrument == null || Instrument == null || o.Instrument.FullName != Instrument.FullName) return;

            bool ours = false;
            lock (sync)
            {
                foreach (TrackedOrder t in tracked)
                {
                    if (t.Order != o) continue;
                    ours = true;
                    NoteFill(t);
                    try
                    {
                        NinjaTrader.NinjaScript.StrategyBase owner = o.GetOwnerStrategy();
                        if (owner != null) t.AtmOwner = owner;
                    }
                    catch { }
                    break;
                }
            }
            if (!ours)
                LinkChildOrder(o);

            if (o.OrderState == OrderState.Cancelled || o.OrderState == OrderState.Filled)
                ProcessReplacementsAndPrune();
            SyncOrderList();
        }

        private void LinkChildOrder(Order o)
        {
            string name = o.Name ?? "";
            bool stop = name.StartsWith("Stop", StringComparison.OrdinalIgnoreCase);
            bool target = name.StartsWith("Target", StringComparison.OrdinalIgnoreCase);
            if (!stop && !target) return;
            if (o.OrderEntry != OrderEntry.Automated) return;

            NinjaTrader.NinjaScript.StrategyBase owner = null;
            try { owner = o.GetOwnerStrategy(); }
            catch { owner = null; }

            lock (sync)
            {
                if (tracked.Any(t => t.StopOrders.Contains(o) || t.TargetOrders.Contains(o)))
                    return;

                TrackedOrder match = null;
                if (owner != null)
                {
                    foreach (TrackedOrder t in tracked)
                    {
                        if (t.AtmOwner == null)
                        {
                            try { t.AtmOwner = t.Order == null ? null : t.Order.GetOwnerStrategy(); }
                            catch { t.AtmOwner = null; }
                        }
                        if (t.AtmOwner != null && ReferenceEquals(t.AtmOwner, owner) && !HasChildNamed(t, name))
                        {
                            match = t;
                            break;
                        }
                    }
                }
                if (match == null)
                {
                    foreach (TrackedOrder t in tracked)
                    {
                        if (t.Account != o.Account || t.Order == null || t.Order.Filled <= 0) continue;
                        if (t.AtmTemplate == null && t.AtmOwner == null) continue;
                        if (HasChildNamed(t, name)) continue;
                        if (match == null || t.Order.Time > match.Order.Time)
                            match = t;
                    }
                }
                if (match == null) return;
                if (match.AtmOwner == null && owner != null) match.AtmOwner = owner;
                if (stop) match.StopOrders.Add(o);
                else match.TargetOrders.Add(o);
                NoteFill(match);
            }
        }

        private static bool HasChildNamed(TrackedOrder t, string name)
        {
            foreach (Order o in t.StopOrders)
                if (o != null && o.Name == name) return true;
            foreach (Order o in t.TargetOrders)
                if (o != null && o.Name == name) return true;
            return false;
        }

        private bool atmSelectorMissLogged;

        private void HookAtmSelector()
        {
            if (chartWindow == null || atmSelector != null) return;
            atmSelector = chartWindow.FindFirst("ChartTraderControlATMStrategySelector")
                as NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector;
            if (atmSelector == null)
            {
                if (!atmSelectorMissLogged)
                {
                    atmSelectorMissLogged = true;
                    Print("KcMidTrader: Chart Trader ATM selector was not found.");
                }
                return;
            }
            atmSelectorMissLogged = false;
            atmSelector.SelectionChanged += OnAtmSelectionChanged;
        }

        private void OnAtmSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (System.Threading.Interlocked.CompareExchange(ref suppressAtmEvents, 0, 0) != 0) return;
            if (!UseChartTraderAtm) return;

            // The dropdown item is often the live strategy ("Template - 1") or a plain string.
            // Its template name is what StartAtmStrategy accepts. "Custom" is not a template yet.
            object added = (e != null && e.AddedItems != null && e.AddedItems.Count > 0) ? e.AddedItems[0] : null;
            string key = added != null ? TemplateKeyFromObject(added) : null;
            NinjaTrader.NinjaScript.AtmStrategy atm = added as NinjaTrader.NinjaScript.AtmStrategy;
            if (key == null)
            {
                atm = SelectedAtm();
                key = TemplateKeyFromObject(atm);
            }
            if (key == null) return;

            ApplyAtmChange(key, atm);
        }

        // null = not a strategy choice (ignored). "" = None. Otherwise the saved template name.
        private static string TemplateKeyFromObject(object item)
        {
            if (item == null) return "";
            NinjaTrader.NinjaScript.AtmStrategy atm = item as NinjaTrader.NinjaScript.AtmStrategy;
            if (atm == null)
            {
                object content = ReadMember(item, "Content");
                atm = content as NinjaTrader.NinjaScript.AtmStrategy;
            }
            if (atm != null) return AtmTemplateKey(atm);

            string text = ItemText(item);
            if (text.Equals("None", StringComparison.OrdinalIgnoreCase)) return "";
            if (text.Length == 0 || text.Equals("Custom", StringComparison.OrdinalIgnoreCase)) return null;
            return StripInstanceSuffix(text);
        }

        private static string ItemText(object item)
        {
            if (item == null) return "";
            string[] names = { "DisplayName", "Template", "Content", "Text", "Name" };
            foreach (string name in names)
            {
                object value = ReadMember(item, name);
                if (value == null) continue;
                string text = value.ToString();
                if (string.IsNullOrWhiteSpace(text)) continue;
                text = text.Trim();
                if (text.Equals("AtmStrategy", StringComparison.OrdinalIgnoreCase)) continue;
                return text;
            }
            string fallback = item.ToString();
            if (string.IsNullOrWhiteSpace(fallback)) return "";
            fallback = fallback.Trim();
            if (fallback == item.GetType().FullName) return "";
            return fallback;
        }

        private static string AtmSelectionName(NinjaTrader.NinjaScript.AtmStrategy atm)
        {
            if (atm == null) return "";
            try
            {
                string d = atm.DisplayName;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
            catch { }
            try
            {
                string n = atm.Name;
                if (!string.IsNullOrWhiteSpace(n) && !n.Equals("AtmStrategy", StringComparison.OrdinalIgnoreCase))
                    return n.Trim();
            }
            catch { }
            return "";
        }

        private static string SafeAtmName(NinjaTrader.NinjaScript.AtmStrategy atm)
        {
            if (atm == null) return "";
            try
            {
                string d = atm.DisplayName;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
            catch { }
            try
            {
                string n = atm.Name;
                if (!string.IsNullOrWhiteSpace(n) && !n.Equals("AtmStrategy", StringComparison.OrdinalIgnoreCase))
                    return n.Trim();
            }
            catch { }
            object template = ReadMember(atm, "Template");
            if (template != null && !string.IsNullOrWhiteSpace(template.ToString()))
                return template.ToString().Trim();
            return "";
        }

        // Saved template name. A live strategy is shown as "Template - 1"; StartAtmStrategy needs
        // the template file name. "" means no ATM. null means Custom, which is not applied.
        // A template may itself end in " - number" ("ATM 1 Tradea 10 - 20"), so that tail is
        // removed only when the shorter name is the file on disk.
        private static string AtmTemplateKey(NinjaTrader.NinjaScript.AtmStrategy atm)
        {
            if (atm == null) return "";
            object template = ReadMember(atm, "Template");
            if (template != null)
            {
                string saved = template.ToString().Trim();
                if (saved.Length > 0 && !saved.Equals("AtmStrategy", StringComparison.OrdinalIgnoreCase))
                {
                    if (saved.Equals("Custom", StringComparison.OrdinalIgnoreCase)) return null;
                    return StripInstanceSuffix(saved);
                }
            }
            string name = SafeAtmName(atm);
            if (name.Length == 0 || name.Equals("None", StringComparison.OrdinalIgnoreCase)) return "";
            if (name.Equals("Custom", StringComparison.OrdinalIgnoreCase)) return null;
            return StripInstanceSuffix(name);
        }

        private static string StripInstanceSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            name = name.Trim();
            if (AtmTemplateFileExists(name)) return name;

            string stripped = Regex.Replace(name, @"\s*-\s*\d+\s*$", "").Trim();
            if (stripped.Length > 0 && !string.Equals(stripped, name, StringComparison.Ordinal)
                && AtmTemplateFileExists(stripped))
                return stripped;
            return name;
        }

        // templates\AtmStrategy\<name>.xml under the NinjaTrader user folder.
        private static bool AtmTemplateFileExists(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                string path = System.IO.Path.Combine(
                    NinjaTrader.Core.Globals.UserDataDir, "templates", "AtmStrategy", name.Trim() + ".xml");
                return System.IO.File.Exists(path);
            }
            catch { return false; }
        }

        // NF entries are cancelled and resubmitted on the newly selected template.
        // Checked filled orders get new stop/target prices.
        private void ApplyAtmChange(string key, NinjaTrader.NinjaScript.AtmStrategy atm)
        {
            if (key == null) return;
            if (atm != null && !string.Equals(AtmTemplateKey(atm) ?? "", key, StringComparison.OrdinalIgnoreCase))
                atm = null;

            List<TrackedOrder> toCancel = new List<TrackedOrder>();
            List<TrackedOrder> toReprice = new List<TrackedOrder>();
            lock (sync)
            {
                foreach (TrackedOrder t in tracked)
                {
                    if (t.PendingReplace || t.Order == null) continue;
                    bool same = string.Equals(t.AtmName ?? "", key, StringComparison.OrdinalIgnoreCase);
                    bool openEntry = t.Order.Filled == 0 && !Order.IsTerminalState(t.Order.OrderState)
                        && t.Order.OrderState != OrderState.CancelPending;
                    if (openEntry)
                    {
                        if (same) continue;
                        double px = t.Order.OrderType == OrderType.Limit ? t.Order.LimitPrice : t.Order.StopPrice;
                        t.PendingReplace = true;
                        t.SwapAtm = true;
                        t.ReplaceAtmName = key;
                        t.ReplaceAtm = key.Length == 0 ? null : atm;
                        t.ReplacePrice = px;
                        t.FollowAtm = true;
                        toCancel.Add(t);
                    }
                    else if (t.Order.Filled > 0 && t.SelectedForAtm && !same)
                        toReprice.Add(t);
                }
            }

            foreach (TrackedOrder t in toCancel)
            {
                try { t.Account.Cancel(new[] { t.Order }); }
                catch (Exception ex)
                {
                    Print("KcMidTrader ATM replace cancel error: " + ex.Message);
                    lock (sync) { t.PendingReplace = false; t.SwapAtm = false; }
                }
            }

            int changed = 0;
            List<string> notes = new List<string>();
            if (atm != null && key.Length > 0)
            {
                foreach (TrackedOrder t in toReprice)
                {
                    changed += RepriceProtective(t, atm, notes);
                    t.AtmName = key;
                }
            }

            if (toCancel.Count == 0 && toReprice.Count == 0)
                return;

            string template = key.Length == 0 ? "no ATM" : key;
            string text = string.Format("{0} NF entr{1} set to {2}. {3} selected stop/target order{4} changed.",
                toCancel.Count, toCancel.Count == 1 ? "y" : "ies", template,
                changed, changed == 1 ? "" : "s");
            if (atm == null && toReprice.Count > 0)
                text += " Placed stops and targets were left unchanged.";
            if (notes.Count > 0)
                text += " " + notes[0];
            SetStatusNow(text);
            SyncOrderList();
        }

        private int RepriceProtective(TrackedOrder t, NinjaTrader.NinjaScript.AtmStrategy atm, List<string> notes)
        {
            double fill = t.FillPrice;
            if (fill <= 0 && t.Order != null) fill = t.Order.AverageFillPrice;
            if (fill <= 0)
            {
                notes.Add((t.Label ?? "Order") + " has no fill price yet.");
                return 0;
            }

            bool isLong = t.Action == OrderAction.Buy || t.Action == OrderAction.BuyToCover;
            List<Order> stops;
            List<Order> targets;
            lock (sync)
            {
                stops = t.StopOrders.ToList();
                targets = t.TargetOrders.ToList();
            }
            if (stops.Count == 0 && targets.Count == 0)
            {
                notes.Add((t.Label ?? "Order") + " has no stop or target yet.");
                return 0;
            }

            int changed = 0;
            foreach (Order stop in stops)
                if (MoveProtective(t, stop, atm, fill, isLong, true, notes)) changed++;
            foreach (Order target in targets)
                if (MoveProtective(t, target, atm, fill, isLong, false, notes)) changed++;
            return changed;
        }

        private bool MoveProtective(TrackedOrder t, Order order, NinjaTrader.NinjaScript.AtmStrategy atm,
                                    double fill, bool isLong, bool isStop, List<string> notes)
        {
            if (order == null || !IsChangeable(order)) return false;
            int index = BracketIndex(order.Name);
            double stopVal, targetVal;
            CalculationMode mode;
            string error;
            if (!TryReadBracket(atm, index, out stopVal, out targetVal, out mode, out error))
            {
                if (!string.IsNullOrEmpty(error) && !notes.Contains(error)) notes.Add(error);
                return false;
            }

            double value = isStop ? stopVal : targetVal;
            if (!isStop && value.ApproxCompare(0) == 0)
                return false;   // runner: leave the live target where it is

            double price;
            if (!TryProtectivePrice(isLong, isStop, fill, value, mode, out price, out error))
            {
                if (!string.IsNullOrEmpty(error) && !notes.Contains(error)) notes.Add(error);
                return false;
            }

            try
            {
                if (order.OrderType == OrderType.Limit)
                {
                    if (order.LimitPrice.ApproxCompare(price) == 0) return false;
                    order.LimitPriceChanged = price;
                }
                else if (order.OrderType == OrderType.StopLimit)
                {
                    if (order.StopPrice.ApproxCompare(price) == 0) return false;
                    order.StopPriceChanged = price;
                    order.LimitPriceChanged = price;
                }
                else
                {
                    if (order.StopPrice.ApproxCompare(price) == 0) return false;
                    order.StopPriceChanged = price;
                }
                t.Account.Change(new[] { order });
                return true;
            }
            catch (Exception ex)
            {
                Print("KcMidTrader ATM reprice error: " + ex.Message);
                notes.Add("Could not change " + (order.Name ?? "order") + ".");
                return false;
            }
        }

        private static int BracketIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            Match m = Regex.Match(name, @"(\d+)\s*$");
            int n;
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out n) || n < 1) return 0;
            return n - 1;
        }

        private bool TryProtectivePrice(bool isLong, bool isStop, double fill, double value, CalculationMode mode,
                                        out double price, out string error)
        {
            price = 0;
            error = null;
            double distance;
            if (mode == CalculationMode.Ticks)
                distance = value * TickSize;
            else if (mode == CalculationMode.Price)
            {
                price = Instrument.MasterInstrument.RoundToTickSize(value);
                return price > 0;
            }
            else if (mode == CalculationMode.Percent)
                distance = Math.Abs(fill * value);
            else
            {
                error = "ATM parameter type " + mode + " is not applied.";
                return false;
            }

            price = isLong
                ? (isStop ? fill - distance : fill + distance)
                : (isStop ? fill + distance : fill - distance);
            price = Instrument.MasterInstrument.RoundToTickSize(price);
            if (price <= 0)
            {
                error = "ATM price was not usable.";
                return false;
            }
            return true;
        }

        private static bool TryReadBracket(NinjaTrader.NinjaScript.AtmStrategy atm, int index,
                                           out double stop, out double target, out CalculationMode mode, out string error)
        {
            stop = 0;
            target = 0;
            mode = CalculationMode.Ticks;
            error = null;
            object brackets = ReadMember(atm, "Brackets");
            System.Collections.IList list = brackets as System.Collections.IList;
            if (list == null)
            {
                error = "ATM template has no brackets.";
                return false;
            }
            if (index < 0 || index >= list.Count)
            {
                error = "ATM template has no bracket " + (index + 1) + ".";
                return false;
            }

            object bracket = list[index];
            object stopRaw = ReadMember(bracket, "StopLoss");
            object targetRaw = ReadMember(bracket, "Target");
            if (stopRaw == null || targetRaw == null)
            {
                error = "Could not read ATM bracket " + (index + 1) + ".";
                return false;
            }
            stop = Convert.ToDouble(stopRaw, CultureInfo.InvariantCulture);
            target = Convert.ToDouble(targetRaw, CultureInfo.InvariantCulture);

            object modeRaw = ReadMember(bracket, "CalculationMode")
                ?? ReadMember(bracket, "TargetCalculationMode")
                ?? ReadMember(atm, "CalculationMode");
            if (modeRaw == null)
                mode = CalculationMode.Ticks;
            else if (modeRaw is CalculationMode)
                mode = (CalculationMode)modeRaw;
            else if (!Enum.TryParse(modeRaw.ToString(), true, out mode))
            {
                error = "Unrecognized ATM parameter type " + modeRaw + ".";
                return false;
            }

            if (mode != CalculationMode.Ticks && mode != CalculationMode.Price && mode != CalculationMode.Percent)
            {
                error = "ATM parameter type " + mode + " is not applied.";
                return false;
            }
            return true;
        }

        private static object ReadMember(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null;
            PropertyInfo prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (prop == null) return null;
            return prop.GetValue(obj, null);
        }

        private sealed class OrderRow
        {
            public string Label;
            public string Text;
            public bool   IsBuy;
            public bool   Checkable;
            public bool   Checked;
        }

        private void SyncOrderList()
        {
            string sig;
            lock (sync)
            {
                StringBuilder sb = new StringBuilder();
                foreach (TrackedOrder t in tracked)
                {
                    bool anyFill = t.Order != null && t.Order.Filled > 0;
                    sb.Append(OrderListText(t)).Append('|').Append(anyFill ? '1' : '0').Append(';');
                }
                sig = sb.ToString();
            }
            if (sig == orderListSig) return;
            orderListSig = sig;
            if (ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(RefreshOrderList);
        }

        private void RefreshOrderList()
        {
            if (orderListPanel == null) return;
            List<OrderRow> rows = new List<OrderRow>();
            lock (sync)
            {
                foreach (TrackedOrder t in tracked)
                {
                    bool filled = t.Order != null && t.Order.Filled > 0;
                    OrderRow row = new OrderRow();
                    row.Label = t.Label ?? "";
                    row.IsBuy = IsBuyAction(t.Action);
                    row.Checkable = filled;
                    row.Checked = t.SelectedForAtm;
                    row.Text = OrderListText(t);
                    rows.Add(row);
                }
            }

            orderListPanel.Children.Clear();
            if (rows.Count == 0)
            {
                orderListPanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "No KC orders",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)),
                    FontSize = 11,
                    Margin = new Thickness(4, 4, 4, 4)
                });
                return;
            }

            foreach (OrderRow row in rows)
            {
                Brush rowBrush = new SolidColorBrush(OrderListColor(row.IsBuy));
                if (rowBrush.CanFreeze) rowBrush.Freeze();
                if (!row.Checkable)
                {
                    orderListPanel.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = row.Text,
                        Foreground = rowBrush,
                        FontSize = 10,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(2, 1, 2, 1)
                    });
                    continue;
                }

                System.Windows.Controls.CheckBox box = new System.Windows.Controls.CheckBox
                {
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = row.Text,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = rowBrush,
                        FontSize = 10
                    },
                    IsChecked = row.Checked,
                    Tag = row.Label,
                    Foreground = rowBrush,
                    Margin = new Thickness(2, 1, 2, 1)
                };
                box.Click += OnOrderCheck;
                orderListPanel.Children.Add(box);
            }
        }

        private void OnOrderCheck(object sender, RoutedEventArgs e)
        {
            System.Windows.Controls.CheckBox box = sender as System.Windows.Controls.CheckBox;
            if (box == null) return;
            string label = box.Tag as string;
            if (string.IsNullOrEmpty(label)) return;
            bool on = box.IsChecked == true;
            lock (sync)
            {
                checkedLabels[label] = on;
                foreach (TrackedOrder t in tracked)
                    if (t.Label == label) t.SelectedForAtm = on;
            }
        }

        // =================================================================================
        //  Properties
        // =================================================================================
        [Display(Name = "Indicator name contains", Description = "Indicators on the chart whose name contains this text are listed in the Track selector (default: Keltner).",
                 Order = 0, GroupName = "KC Mid Trader")]
        public string IndicatorNameFilter { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Default quantity", Order = 1, GroupName = "KC Mid Trader")]
        public int DefaultQuantity { get; set; }

        [Display(Name = "Track line by default", Description = "Initial state of Track line. Orders follow the selected Keltner plot, which starts as the midline.",
                 Order = 2, GroupName = "KC Mid Trader")]
        public bool TrackByDefault { get; set; }

        [Display(Name = "Track mode", Description = "OnBarClose = re-price once per bar. OnPriceChange = re-price whenever the rounded midband moves.",
                 Order = 3, GroupName = "KC Mid Trader")]
        public NinjaTrader.NinjaScript.KcMidTraderTypes.KcTrackMode TrackMode { get; set; }

        [Display(Name = "When midband crosses market", Description = "Block = don't place / pause tracking. SwitchToStopMarket = use a stop order at the midband.",
                 Order = 4, GroupName = "KC Mid Trader")]
        public NinjaTrader.NinjaScript.KcMidTraderTypes.KcCrossBehavior CrossBehavior { get; set; }

        [Display(Name = "Time in force", Order = 5, GroupName = "KC Mid Trader")]
        public TimeInForce OrderTif { get; set; }

        [Display(Name = "Use Chart Trader ATM strategy", Order = 6, GroupName = "KC Mid Trader")]
        public bool UseChartTraderAtm { get; set; }

        [Display(Name = "Re-adopt working KC orders on reload", Order = 7, GroupName = "KC Mid Trader")]
        public bool AdoptExistingOrders { get; set; }

        [Range(0, 100000)]
        [Display(Name = "Max ticks offset", Description = "Ceiling for the Chart Trader Offset box only. A typed value above this is trimmed to this number. It is not the offset and it is not added to the midband.",
                 Order = 8, GroupName = "KC Mid Trader")]
        public int MaxTicksOffset { get; set; }
    }
}
