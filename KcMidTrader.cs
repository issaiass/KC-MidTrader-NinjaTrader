// =====================================================================================
//  KcMidTrader.cs  -  NinjaTrader 8 indicator
//
//  Conceptualized and developed with AI by Rangel Alvarado.
//
//  Adds a "KC MIDBAND" section to the bottom of Chart Trader that lets you:
//    * set a quantity (default 1)
//    * BUY or SELL at the Keltner Channel midband with one click
//    * keep those orders ATTACHED to the midband (they follow it as it moves)
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
using System.Linq;
using System.Text;
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
            public bool         Track;
            public bool         PendingReplace;   // cancel sent, waiting to re-submit as other type
            public double       ReplacePrice;
            public bool         Paused;           // tracking paused because mid crossed market (Block mode)
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

        // ---------------------------------------------------------------- market / KC state
        private volatile bool kcFound;
        private double lastMid   = double.NaN;
        private double lastAsk   = double.NaN;
        private double lastBid   = double.NaN;
        private double lastPrice = double.NaN;
        private double shownMid  = double.NaN;
        private int    shownCount = -1;
        private bool   trackAll  = true;

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
        private System.Windows.Controls.ComboBox          kcCombo;        // which Keltner Channel to track
        private List<KcSource> kcList = new List<KcSource>();
        private volatile KcSource selectedKc;
        private bool   suppressComboEvent;
        private string lastDiagnostic;
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
            }
            else if (State == State.Historical)
            {
                trackAll = TrackByDefault;
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
                ChartControl.Dispatcher.InvokeAsync(() => DetectKeltner());

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
            try
            {
                for (int i = barIndex; i >= 0 && i >= barIndex - 2; i--)
                {
                    if (src.MidPlot >= 0)
                    {
                        if (src.MidPlot >= src.Indicator.Values.Length) return false;
                        Series<double> ser = src.Indicator.Values[src.MidPlot];
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

        // Returns a message for the status line.
        private string Submit(Account acct, OrderAction action, int qty, double price,
                              NinjaTrader.NinjaScript.AtmStrategy atm, bool track)
        {
            bool   isBuy = action == OrderAction.Buy;
            double refPx = RefPrice(isBuy);
            if (double.IsNaN(refPx))
                return "No market data yet.";

            OrderType? type = DesiredType(isBuy, price, refPx);
            if (type == null)
                return "Midband is at the market - not placed.";
            if (type.Value == OrderType.StopMarket &&
                CrossBehavior == NinjaTrader.NinjaScript.KcMidTraderTypes.KcCrossBehavior.Block)
                return string.Format("Blocked: midband {0} is {1} the market (a limit would fill now).",
                    Instrument.MasterInstrument.FormatPrice(price), isBuy ? "above" : "below");

            double lmt = type.Value == OrderType.Limit      ? price : 0;
            double stp = type.Value == OrderType.StopMarket ? price : 0;

            Order o;
            try
            {
                if (atm != null)
                {
                    // ATM entry orders must be named "Entry".
                    o = acct.CreateOrder(Instrument, action, type.Value, OrderEntry.Manual, OrderTif, qty,
                                         lmt, stp, string.Empty, "Entry", NinjaTrader.Core.Globals.MaxDate, null);
                    NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(atm, o);
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
                return "Error: " + ex.Message;
            }

            lock (sync)
            {
                TrackedOrder t = new TrackedOrder();
                t.Order = o; t.Account = acct; t.Action = action; t.Quantity = qty;
                t.AtmTemplate = atm; t.Track = track;
                tracked.Add(t);
            }

            return string.Format("{0} {1} {2} @ {3}{4}",
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
            List<TrackedOrder> toResubmit = new List<TrackedOrder>();
            string msg = null;

            lock (sync)
            {
                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    TrackedOrder t = tracked[i];
                    OrderState st = t.Order.OrderState;

                    if (t.PendingReplace)
                    {
                        if (st == OrderState.Cancelled)
                        {
                            toResubmit.Add(t);
                            tracked.RemoveAt(i);
                        }
                        else if (st == OrderState.Filled || st == OrderState.Rejected)
                        {
                            tracked.RemoveAt(i);   // filled before the cancel landed: do NOT re-submit
                            msg = "Order " + st + " before replace - nothing re-submitted.";
                        }
                        continue;
                    }

                    if (Order.IsTerminalState(st))
                    {
                        if (st == OrderState.Rejected) msg = "Order rejected - see the Log tab.";
                        tracked.RemoveAt(i);
                    }
                }
            }

            foreach (TrackedOrder t in toResubmit)
            {
                int remaining = t.Quantity - t.Order.Filled;
                if (remaining <= 0) continue;
                msg = "Replaced: " + Submit(t.Account, t.Action, remaining, t.ReplacePrice, t.AtmTemplate, t.Track);
            }

            if (msg != null) SetStatus(msg);
            UpdateReadout();
        }

        private void TrackOrders(double target)
        {
            if (double.IsNaN(target)) return;

            List<TrackedOrder> snapshot;
            lock (sync) snapshot = tracked.Where(x => x.Track && !x.PendingReplace).ToList();

            foreach (TrackedOrder t in snapshot)
            {
                Order o = t.Order;
                if (!IsChangeable(o)) continue;

                bool   isBuy   = t.Action == OrderAction.Buy;
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

                // Midband crossed to the other side of the market (or sits on it).
                if (want == null || CrossBehavior == NinjaTrader.NinjaScript.KcMidTraderTypes.KcCrossBehavior.Block)
                {
                    if (!t.Paused) SetStatus("Tracking paused: midband crossed the market.");
                    t.Paused = true;
                    continue;
                }

                // SwitchToStopMarket: cancel, then re-submit with the other type once the cancel is confirmed.
                lock (sync) { t.PendingReplace = true; t.ReplacePrice = target; }
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
                                tracked.Add(t);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Print("KcMidTrader adopt error: " + ex.Message); }
        }

        private int CancelAll()
        {
            List<TrackedOrder> list;
            lock (sync)
            {
                list = tracked.ToList();
                tracked.Clear();       // also drops pending replacements so nothing is re-submitted
            }

            int n = 0;
            foreach (TrackedOrder t in list)
            {
                if (Order.IsTerminalState(t.Order.OrderState)) continue;
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

                        // Any indicator whose name or class contains the filter text (default "Keltner").
                        if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                            typeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
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
                kcCombo.IsEnabled = kcList.Count > 1;   // nothing to choose with only one
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
            lastMid    = double.NaN;   // readout refreshes on the next tick with the new midband
            SetStatusNow("Tracking " + kcList[idx].Label + ". Working KC orders follow this midband.");
        }

        // Re-scan the chart every time the list is opened, so it always reflects what's on the chart.
        private void OnKcComboOpened(object sender, EventArgs e)
        {
            DetectKeltner();
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

            panel = new System.Windows.Controls.Grid();
            panel.Margin = new Thickness(0, 6, 0, 2);
            panel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            panel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
            for (int i = 0; i < 7; i++)
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
                ToolTip = "Keltner Channel whose midband the KC orders use"
            };
            kcCombo.SelectionChanged += OnKcComboChanged;
            kcCombo.DropDownOpened   += OnKcComboOpened;
            System.Windows.Controls.Grid.SetColumn(kcLabel, 0);
            System.Windows.Controls.Grid.SetColumn(kcCombo, 1);
            kcRow.Children.Add(kcLabel); kcRow.Children.Add(kcCombo);
            Place(kcRow, 1, 0, 2);

            // Rows 2-3: quantity selector, then track checkbox
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

            trackBox = new System.Windows.Controls.CheckBox
            {
                Content = "Track mid", IsChecked = TrackByDefault, Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 1, 1, 1),
                ToolTip = "Keep KC orders attached to the midband as it moves"
            };
            // Quantity gets the FULL panel width (row 1) so it is never clipped in a narrow Chart Trader;
            // "Track mid" goes on its own row (row 2).
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
            Place(qtyRow, 2, 0, 2);
            Place(trackBox, 3, 0, 2);

            // Row 4: Buy / Sell
            buyButton  = MakeButton("Buy @ Mid",  btnStyle, Color.FromRgb(0x1E, 0x7A, 0x3C));
            sellButton = MakeButton("Sell @ Mid", btnStyle, Color.FromRgb(0xA3, 0x24, 0x24));
            Place(buyButton, 4, 0, 1); Place(sellButton, 4, 1, 1);

            // Row 5: Cancel
            cancelButton = MakeButton("Cancel KC Orders", btnStyle, Color.FromRgb(0x4A, 0x4F, 0x5A));
            Place(cancelButton, 5, 0, 2);

            // Row 6: status
            statusText = new System.Windows.Controls.TextBlock
            {
                Text = "Ready", Foreground = fg, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 2, 2)
            };
            Place(statusText, 6, 0, 2);

            buyButton.Click    += OnBuyClick;
            sellButton.Click   += OnSellClick;
            cancelButton.Click += OnCancelClick;
            trackBox.Click     += OnTrackClick;

            panel.Visibility = Visibility.Collapsed;   // shown only when a Keltner Channel is found

            if (chartWindow.MainTabControl != null)
            {
                chartWindow.MainTabControl.SelectionChanged += OnTabChanged;
                tabHooked = true;
            }
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
            if (trackBox     != null) trackBox.Click     -= OnTrackClick;
            if (kcCombo != null)
            {
                kcCombo.SelectionChanged -= OnKcComboChanged;
                kcCombo.DropDownOpened   -= OnKcComboOpened;
            }
            if (qtyBox != null)
            {
                qtyBox.PreviewKeyDown   -= OnQtyKeyDown;
                qtyBox.PreviewTextInput -= OnQtyTextInput;
                qtyBox.MouseWheel       -= OnQtyWheel;
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

            SetStatusNow(Submit(acct, action, qty, mid, atm, track));
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

        private const string OrderNamePrefix = "KCMid";

        // =================================================================================
        //  Properties
        // =================================================================================
        [Display(Name = "Indicator name contains", Description = "Indicators on the chart whose name contains this text are listed in the Track selector (default: Keltner).",
                 Order = 0, GroupName = "KC Mid Trader")]
        public string IndicatorNameFilter { get; set; }

        [Range(1, int.MaxValue)]
        [Display(Name = "Default quantity", Order = 1, GroupName = "KC Mid Trader")]
        public int DefaultQuantity { get; set; }

        [Display(Name = "Track midband by default", Order = 2, GroupName = "KC Mid Trader")]
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
    }
}
