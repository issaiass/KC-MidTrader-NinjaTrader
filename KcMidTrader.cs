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
//    * on one checked filled order, point its stop or target at an indicator, remove either leg without closing the position, close the target on an EMA cross, or close either leg when MACD crosses a level
//    * with two or more EMAs, track an EMA cross: Buy or Sell stays not filled until a later favorable cross, then the ATM stop is kept and the target waits for the next cross
//    * MACD is a trigger, not a price: the order follows the Keltner midline and is sent only when the MACD line crosses the level from the window, including a buy above the market or a sell below it. A stop or target can close on a MACD level too
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
        // A protective leg stays on the ATM template, follows one plot, is removed, waits for an EMA cross (target only), or waits for a MACD level.
        private enum LegFollow
        {
            Atm,
            Indicator,
            None,
            EmaCross,
            Macd
        }

        private enum FollowKind
        {
            Atm,
            None,
            Indicator,
            EmaCross
        }

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
            public string       Label;            // Ord-xxxxxx-MMM-dd-yy_HH-mm-ss-fff (checkbox key; list text adds side, qty, F/NF or W/L)
            public bool         Track;
            public bool         Completed;        // entry is done and the filled quantity has been closed
            public double       Points;           // closed result, 4 ticks = 1 point; negative is a loss
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
            public LegFollow StopSource;
            public NinjaTrader.Gui.NinjaScript.IndicatorRenderBase StopIndicator;
            public int          StopPlot;
            public bool         StopPaused;
            public bool         StopMissing;
            public LegFollow TargetSource;
            public NinjaTrader.Gui.NinjaScript.IndicatorRenderBase TargetIndicator;
            public int          TargetPlot;
            public bool         TargetPaused;
            public bool         TargetMissing;
            public readonly List<Order> CloseOrders = new List<Order>();
            public readonly List<HeldLeg> Relink = new List<HeldLeg>();
            public NinjaTrader.Gui.NinjaScript.IndicatorRenderBase TargetEmaA;
            public NinjaTrader.Gui.NinjaScript.IndicatorRenderBase TargetEmaB;
            public int          TargetEmaSign;
            public bool         TargetCrossSent;
            public bool         TargetCrossPending;
            public bool         WaitCross;        // NF arm: no broker order until a later favorable EMA cross
            public int          ArmSign;          // EMA A vs B on the closed bar when the arm was placed or last updated
            public int          ArmBar;
            public bool         WaitMacd;         // NF arm: no broker order until MACD crosses EntryMacdLevel
            public double       EntryMacdLevel;
            public NinjaTrader.Gui.NinjaScript.IndicatorRenderBase EntryMacd;
            public int          EntryMacdSign;    // MACD vs level when the arm was placed or last updated
            public int          EntryMacdBar = -1;
            public bool         EntryMacdNoted;
            public NinjaTrader.Gui.NinjaScript.IndicatorRenderBase EntryPriceInd; // Keltner the order price follows
            public int          EntryPricePlot;
            public bool         FollowThrough;    // keep following the Keltner price on the stop side of the market
            public bool         StopMacdSet;
            public double       StopMacdLevel;
            public int          StopMacdSign;     // int.MinValue until the closed bar is known
            public bool         TargetMacdSet;
            public double       TargetMacdLevel;
            public int          TargetMacdSign;
            public bool         MacdCloseSent;
            public bool         MacdClosePending;
            public bool         MacdCloseCancelsStop;
            public int          EntryCrossBar = -1; // closed bar that opened the trade; that cross cannot close it
            public bool         ArmNoted;
        }

        // A protective order to put back if cancelling the other ATM leg takes this one with it.
        private sealed class HeldLeg
        {
            public bool IsStop;
            public Order Original;
            public double Price;
            public int Qty;
            public bool Replaced;
        }

        private sealed class FollowPick
        {
            public string Text;
            public KcSource Source;
            public FollowKind Kind;
            public override string ToString() { return Text ?? ""; }
        }

        private sealed class BandPick
        {
            public string Text;
            public int Plot;
            public override string ToString() { return Text ?? ""; }
        }

        private sealed class LegDialogRow
        {
            public bool IsStop;
            public System.Windows.Controls.ComboBox SourceBox;
            public System.Windows.Controls.ComboBox BandBox;
            public System.Windows.Controls.ComboBox EmaABox;
            public System.Windows.Controls.ComboBox EmaBBox;
            public System.Windows.Controls.Panel EmaPanel;
            public System.Windows.Controls.Button MacdButton;
            public double MacdLevel;
            public bool MacdSet;
            public NinjaTrader.Gui.NinjaScript.IndicatorRenderBase MacdIndicator;
            public int PriorIndex;
            public bool MacdPromptOpen;
        }

        // A Keltner-type indicator found on the chart, and which of its plots is the midband.
        private class KcSource
        {
            public NinjaTrader.Gui.NinjaScript.IndicatorRenderBase Indicator;
            public int    MidPlot;     // index into Values[]; -1 = use the middle value of the first 3 plots
            public string Label;
            public bool   IsEmaCross;  // synthetic Track row; not a chart indicator
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
        private System.Windows.Controls.Grid              offsetRow;      // offset label + spinner; hidden when Show zone is off
        private System.Windows.Controls.CheckBox          showZoneBox;
        private System.Windows.Controls.ComboBox          kcCombo;        // which Keltner Channel to track
        private System.Windows.Controls.ComboBox          lineCombo;      // which plot of that channel
        private System.Windows.Controls.Grid              kcRow;          // Track label + selector
        private System.Windows.Controls.Grid              lineRow;        // Line label + selector
        private System.Windows.Controls.StackPanel        nfListPanel;
        private System.Windows.Controls.StackPanel        filledListPanel;
        private System.Windows.Controls.StackPanel        doneListPanel;
        private List<KcSource> kcList = new List<KcSource>();
        private List<int>      linePlots = new List<int>();
        private volatile KcSource selectedKc;
        private volatile KcSource keltnerPrice;   // Keltner whose line is the order price while MACD is only the trigger
        private NinjaTrader.Gui.NinjaScript.IndicatorRenderBase keltnerIndicator;
        private volatile NinjaTrader.Gui.NinjaScript.IndicatorRenderBase crossEmaA;
        private volatile NinjaTrader.Gui.NinjaScript.IndicatorRenderBase crossEmaB;
        private volatile bool crossConfigured;
        private volatile int  emaOnChart;
        // Not volatile: C# rejects volatile double (CS0677). Published by the volatile
        // macdConfigured write below, which readers check before using this value.
        private double macdLevel = double.NaN;
        private volatile bool macdConfigured;
        private string shownMacdText;
        private volatile string liveAtmKey = "";
        private volatile NinjaTrader.NinjaScript.AtmStrategy liveAtm;
        private string crossEmaAName = "EMA A";
        private string crossEmaBName = "EMA B";
        private bool   crossPausedNoted;
        private volatile int   selectedPlot = int.MinValue;   // int.MinValue = not chosen yet
        private string selectedLineName = "Midline";
        private bool   suppressComboEvent;
        private bool   suppressLineEvent;
        private bool   suppressLegDialog;
        private bool   macdPromptOpen;
        private bool   trackMacdPromptQueued;
        private NinjaTrader.Gui.NinjaScript.IndicatorRenderBase pendingTrackMacd;
        private KcSource pendingTrackPrevious;
        private string lastDiagnostic;
        private string orderListSig;
        private Brush  panelBrush;
        private NinjaTrader.Gui.NinjaScript.AtmStrategy.AtmStrategySelector atmSelector;
        private int    suppressAtmEvents;
        private int    inPrune;
        private System.Windows.Controls.Button            buyButton, sellButton, cancelButton, minusButton, plusButton, legButton;
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

            // EMA cross and MACD levels use the closed bar only, even in OnPriceChange mode.
            if (State == State.Realtime && IsFirstTickOfBar)
            {
                TrackEmaCrossNow();
                TrackMacdNow();
            }

            KcSource src = selectedKc;
            if (src != null && src.IsEmaCross)
            {
                if (State != State.Realtime) return;
                UpdateReadout();
                ProcessReplacementsAndPrune();
                TrackProtectiveNow();
                return;
            }

            // MACD selects the trigger. The order price is still the Keltner line.
            KcSource price = PriceSource();
            if (!kcFound || price == null)
            {
                if (State == State.Realtime)
                {
                    ProcessReplacementsAndPrune();
                    TrackProtectiveNow();
                }
                return;
            }

            // The midband is read straight from the selected indicator's own plot, so it always
            // matches what is drawn on the chart (any Keltner variant, any settings, any input).
            double rawNow;
            if (!TryReadMid(price, CurrentBar, out rawNow))
            {
                if (State == State.Realtime)
                    TrackProtectiveNow();
                return;
            }
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
                if (TryReadMid(price, CurrentBar - 1, out rawPrev))
                    TrackOrders(Instrument.MasterInstrument.RoundToTickSize(rawPrev));
            }
            else
            {
                TrackOrders(midNow);
            }
            TrackProtectiveNow();
        }

        // Same cadence as entry tracking. Each leg reads its own indicator, so a missing
        // entry line does not stop a stop or target that is already following a plot.
        private void TrackProtectiveNow()
        {
            if (State != State.Realtime) return;
            int bar = CurrentBar;
            if (TrackMode == NinjaTrader.NinjaScript.KcMidTraderTypes.KcTrackMode.OnBarClose)
            {
                if (!IsFirstTickOfBar) return;
                bar = CurrentBar - 1;
            }
            if (bar < 0) return;
            TrackProtective(bar);
        }

        // Latest valid midband value at or just before barIndex (an indicator calculated
        // "On bar close" has no value yet on the bar that is still forming).
        private bool TryReadMid(KcSource src, int barIndex, out double value)
        {
            value = double.NaN;
            if (src == null || src.Indicator == null) return false;
            return TryReadSeries(src.Indicator, ActivePlot(src), barIndex, out value);
        }

        private bool TryReadSeries(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind, int plot, int barIndex, out double value)
        {
            value = double.NaN;
            if (ind == null) return false;
            try
            {
                for (int i = barIndex; i >= 0 && i >= barIndex - 2; i--)
                {
                    if (plot >= 0)
                    {
                        if (ind.Values == null || plot >= ind.Values.Length) return false;
                        Series<double> ser = ind.Values[plot];
                        if (i < ser.Count && ser.IsValidDataPointAt(i))
                        {
                            value = ser.GetValueAt(i);
                            return !double.IsNaN(value) && value > 0;
                        }
                    }
                    else
                    {
                        // Unknown plot names: midband = the middle of the first three plots.
                        if (ind.Values == null || ind.Values.Length < 3) return false;
                        double[] v = new double[3];
                        bool ok = true;
                        for (int p = 0; p < 3 && ok; p++)
                        {
                            Series<double> ser = ind.Values[p];
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
                              NinjaTrader.NinjaScript.AtmStrategy atm, bool track, string label, bool followAtm, string atmName,
                              bool followThrough = false)
        {
            bool   isBuy = action == OrderAction.Buy;
            double refPx = RefPrice(isBuy);
            if (double.IsNaN(refPx))
                return "No market data yet.";

            OrderType? type = DesiredType(isBuy, price, refPx);
            if (type == null)
                return "Order price is at the market - not placed.";
            if (!followThrough && type.Value == OrderType.StopMarket &&
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
                t.FollowThrough = followThrough;
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
                    if (t.Order == null)
                    {
                        if (!t.WaitCross && !t.WaitMacd) tracked.RemoveAt(i);
                        continue;
                    }
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
                msg = "Replaced: " + Submit(t.Account, t.Action, remaining, t.ReplacePrice, atm, t.Track, t.Label, t.FollowAtm, atmName, t.FollowThrough);
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
                if (o == null || t.WaitCross || t.WaitMacd || !IsChangeable(o)) continue;

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
                // A MACD entry keeps following the Keltner line on that side instead of pausing.
                if (want == null || (!t.FollowThrough && CrossBehavior == NinjaTrader.NinjaScript.KcMidTraderTypes.KcCrossBehavior.Block))
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
            int n = 0;
            lock (sync)
            {
                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    TrackedOrder t = tracked[i];
                    if (t.Order == null)
                    {
                        if (!t.WaitCross && !t.WaitMacd) continue;
                        tracked.RemoveAt(i);
                        n++;
                        continue;
                    }
                    if (Order.IsTerminalState(t.Order.OrderState)) continue;
                    t.PendingReplace = false;   // a manual cancel must not be re-submitted
                    t.SwapAtm = false;
                    list.Add(t);
                    if (t.Order.Filled <= 0)
                        tracked.RemoveAt(i);
                }
            }

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
            List<string> onChart = new List<string>();
            string filter = string.IsNullOrWhiteSpace(IndicatorNameFilter) ? "Keltner" : IndicatorNameFilter.Trim();
            // Track stays on the name filter unless Toggle track is on. The current choice stays listed after lock.
            List<KcSource> found = CollectSources(!trackPickerOn, onChart);
            List<KcSource> all = trackPickerOn ? found : CollectSources(false, null);
            int emas = 0;
            bool pairAlive = false;
            bool sawA = false, sawB = false;
            foreach (KcSource k in all)
            {
                if (!IsEma(k) || k.Indicator == null) continue;
                emas++;
                if (ReferenceEquals(k.Indicator, crossEmaA)) sawA = true;
                if (ReferenceEquals(k.Indicator, crossEmaB)) sawB = true;
            }
            pairAlive = crossConfigured && sawA && sawB && !ReferenceEquals(crossEmaA, crossEmaB);
            emaOnChart = emas;
            if (!pairAlive)
                crossConfigured = false;
            // EMA cross is a Track row, not a chart study. It needs two EMAs and a real indicator so the panel stays up.
            if (emas >= 2 && found.Count > 0)
            {
                KcSource cross = new KcSource();
                cross.IsEmaCross = true;
                cross.MidPlot = -2;
                cross.Label = "EMA cross";
                found.Add(cross);
            }

            // Rebuild the selector only if the set of indicators (or their settings/labels) changed.
            bool changed = found.Count != kcList.Count;
            if (!changed)
                for (int i = 0; i < found.Count; i++)
                    if (found[i].IsEmaCross != kcList[i].IsEmaCross
                        || !ReferenceEquals(found[i].Indicator, kcList[i].Indicator)
                        || found[i].Label != kcList[i].Label)
                    { changed = true; break; }

            if (changed)
            {
                KcSource prev = selectedKc;
                bool wasCross = prev != null && prev.IsEmaCross;
                kcList = found;
                // Keep the user's choice if that indicator is still on the chart; otherwise use the first listed.
                KcSource keep = null;
                if (wasCross)
                    keep = kcList.FirstOrDefault(x => x.IsEmaCross);
                else if (prev != null)
                    keep = kcList.FirstOrDefault(x => !x.IsEmaCross && ReferenceEquals(x.Indicator, prev.Indicator));
                selectedKc = keep ?? (kcList.Count > 0 ? kcList[0] : null);
                if (wasCross && (selectedKc == null || !selectedKc.IsEmaCross))
                    lastMid = double.NaN;
                RememberKeltner();
                RefreshCombo();
                RefreshLineCombo(keep == null);
            }

            kcFound = selectedKc != null;
            RememberKeltner();

            // Help in the NinjaScript Output window when nothing matches.
            string diag = kcFound ? null
                : string.Format("KcMidTrader: no visible indicator containing '{0}' found. Indicators on this chart: {1}",
                                filter, onChart.Count == 0 ? "(none)" : string.Join(", ", onChart));
            if (diag != null && diag != lastDiagnostic) Print(diag);
            lastDiagnostic = diag;

            if (panel != null)
                panel.Visibility = kcFound ? Visibility.Visible : Visibility.Collapsed;
            RememberAtm();
        }

        // Visible chart indicators with a usable plot. nameFilter limits the list to IndicatorNameFilter
        // (and the indicator Track is already on). The SL/TP window asks for the full set.
        // onChart, when supplied, receives every indicator name seen, including ones left out of the list.
        private List<KcSource> CollectSources(bool nameFilter, List<string> onChart)
        {
            List<KcSource> found = new List<KcSource>();
            string filter = string.IsNullOrWhiteSpace(IndicatorNameFilter) ? "Keltner" : IndicatorNameFilter.Trim();
            try
            {
                if (ChartControl != null && ChartControl.Indicators != null)
                {
                    foreach (var item in ChartControl.Indicators)
                    {
                        try
                        {
                            NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind =
                                item as NinjaTrader.Gui.NinjaScript.IndicatorRenderBase;
                            if (ind == null || ReferenceEquals(ind, this)) continue;

                            string typeName = ind.GetType().Name;
                            string name     = ind.Name ?? typeName;
                            if (onChart != null) onChart.Add(name);

                            if (nameFilter)
                            {
                                bool matches = name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               typeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                                bool keepCurrent = selectedKc != null && ReferenceEquals(ind, selectedKc.Indicator);
                                if (!matches && !keepCurrent)
                                    continue;
                            }
                            if (!ind.IsVisible) continue;

                            int midPlot = FindMidPlot(ind);
                            // A two-plot EMA, or one whose series is not ready yet, still belongs in the SL/TP list.
                            if (midPlot == -2)
                            {
                                if (!HasReadablePlot(ind)) continue;
                                midPlot = 0;
                            }

                            KcSource src = new KcSource();
                            src.Indicator = ind;
                            src.MidPlot   = midPlot;
                            src.Label     = string.Format("#{0}  {1}", found.Count + 1, SafeDisplayName(ind));
                            found.Add(src);
                        }
                        catch (Exception ex) { Print("KcMidTrader detect error: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { Print("KcMidTrader detect error: " + ex.Message); }
            return found;
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
            if (n == 1) return 0;
            // Two plots, or plots that exist before Values is filled: the first plot is usable.
            int plotCount = ind.Plots == null ? 0 : ind.Plots.Length;
            return plotCount > 0 ? 0 : -2;
        }

        private static bool HasReadablePlot(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind)
        {
            if (ind == null) return false;
            try
            {
                if (ind.Plots != null && ind.Plots.Length > 0) return true;
                return ind.Values != null && ind.Values.Length > 0;
            }
            catch { return false; }
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

        private string NameFilter()
        {
            return string.IsNullOrWhiteSpace(IndicatorNameFilter) ? "Keltner" : IndicatorNameFilter.Trim();
        }

        private static bool IsNamed(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind, string filter)
        {
            if (ind == null || string.IsNullOrWhiteSpace(filter)) return false;
            string typeName = "";
            string name = "";
            try { typeName = ind.GetType().Name ?? ""; } catch { typeName = ""; }
            try { name = ind.Name ?? ""; } catch { name = ""; }
            return typeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Keeps the Keltner used as the order price. Selecting MACD does not replace it.
        private void RememberKeltner()
        {
            string filter = NameFilter();
            if (selectedKc != null && !selectedKc.IsEmaCross && selectedKc.Indicator != null
                && IsNamed(selectedKc.Indicator, filter))
            {
                keltnerPrice = selectedKc;
                keltnerIndicator = selectedKc.Indicator;
                return;
            }

            KcSource still = null;
            if (keltnerIndicator != null)
            {
                for (int i = 0; i < kcList.Count; i++)
                    if (!kcList[i].IsEmaCross && ReferenceEquals(kcList[i].Indicator, keltnerIndicator))
                    { still = kcList[i]; break; }
            }
            if (still == null)
            {
                for (int i = 0; i < kcList.Count; i++)
                {
                    KcSource k = kcList[i];
                    if (k.IsEmaCross || k.Indicator == null || IsMacd(k)) continue;
                    if (IsNamed(k.Indicator, filter)) { still = k; break; }
                }
            }
            keltnerPrice = still;
            keltnerIndicator = still == null ? null : still.Indicator;
        }

        // Price the working order follows. MACD mode uses the Keltner line, not the oscillator.
        private KcSource PriceSource()
        {
            KcSource src = selectedKc;
            if (src != null && IsMacd(src)) return keltnerPrice;
            if (src != null && src.IsEmaCross) return null;
            return src;
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
            KcSource pick = kcList[idx];
            if (IsMacd(pick))
            {
                // SelectionChanged runs while the popup is still open. A dialog here is closed
                // by that popup and never appears. DropDownClosed opens it after the popup is gone.
                if (macdPromptOpen) return;
                pendingTrackMacd = pick.Indicator;
                pendingTrackPrevious = selectedKc;
                if (!kcCombo.IsDropDownOpen)
                    QueueTrackMacdPrompt();
                return;
            }
            pendingTrackMacd = null;
            if (pick.IsEmaCross)
            {
                KcSource previous = selectedKc;
                if (!ShowEmaCrossDialog())
                {
                    RestoreTrackSelection(previous);
                    return;
                }
                int crossIdx = IndexOfEmaCross();
                if (crossIdx < 0)
                {
                    RestoreTrackSelection(previous);
                    SetStatusNow("EMA cross needs two EMAs.");
                    return;
                }
                suppressComboEvent = true;
                try { kcCombo.SelectedIndex = crossIdx; }
                finally { suppressComboEvent = false; }
                selectedKc = kcList[crossIdx];
                kcFound = true;
                lastMid = double.NaN;
                RefreshLineCombo(true);
                SetStatusNow("Tracking EMA cross. Buy when " + crossEmaAName + " crosses above " + crossEmaBName
                    + ". Sell when " + crossEmaAName + " crosses below " + crossEmaBName + ".");
                if (ChartControl != null) ChartControl.InvalidateVisual();
                return;
            }
            selectedKc = pick;
            kcFound    = true;
            RememberKeltner();
            lastMid    = double.NaN;   // readout refreshes on the next tick with the new line
            RefreshLineCombo(true);
            SetStatusNow("Tracking " + pick.Label + ". Line reset to " + selectedLineName + ".");
            RereadAndTrack();
        }

        private int IndexOfEmaCross()
        {
            for (int i = 0; i < kcList.Count; i++)
                if (kcList[i].IsEmaCross) return i;
            return -1;
        }

        // Runs after the Track dropdown has closed. Cancel puts the previous indicator back.
        private void FinishTrackMacdPrompt(KcSource previous, NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind)
        {
            int idx = -1;
            if (ind != null)
            {
                for (int i = 0; i < kcList.Count; i++)
                    if (!kcList[i].IsEmaCross && ReferenceEquals(kcList[i].Indicator, ind))
                    { idx = i; break; }
            }
            if (idx < 0)
                return;
            // -1 means the popup is still settling. A different indicator means the user moved on.
            int shown = kcCombo == null ? -1 : kcCombo.SelectedIndex;
            if (shown >= 0 && shown < kcList.Count && shown != idx)
            {
                bool stillPrevious = false;
                if (previous != null && previous.IsEmaCross)
                    stillPrevious = kcList[shown].IsEmaCross;
                else if (previous != null)
                    stillPrevious = !kcList[shown].IsEmaCross
                        && ReferenceEquals(kcList[shown].Indicator, previous.Indicator);
                if (!stillPrevious)
                    return;
            }

            RememberKeltner();
            if (keltnerPrice == null || keltnerPrice.Indicator == null)
            {
                RestoreTrackSelection(previous);
                SetStatusNow("MACD needs a Keltner Channel on this chart. The order price is that midline.");
                return;
            }

            double level;
            if (!PromptTrackMacd(kcList[idx], out level))
            {
                RestoreTrackSelection(previous);
                return;
            }
            suppressComboEvent = true;
            try { if (kcCombo != null) kcCombo.SelectedIndex = idx; }
            finally { suppressComboEvent = false; }
            macdLevel = level;
            macdConfigured = true;
            selectedKc = kcList[idx];
            kcFound = true;
            lastMid = double.NaN;
            shownMacdText = null;
            RefreshLineCombo(true);
            SetStatusNow("Tracking the Keltner " + selectedLineName + ". MACD level is " + FormatMacd(level)
                + ". Buy or Sell uses this level and waits for the cross.");
            UpdateReadout();
            if (ChartControl != null) ChartControl.InvalidateVisual();
        }

        // The combo can be rebuilt while the modal is open, so restore by indicator, not by the old object.
        private void RestoreTrackSelection(KcSource previous)
        {
            int back = -1;
            if (previous != null && previous.IsEmaCross)
                back = IndexOfEmaCross();
            else if (previous != null)
            {
                for (int i = 0; i < kcList.Count; i++)
                    if (!kcList[i].IsEmaCross && ReferenceEquals(kcList[i].Indicator, previous.Indicator))
                    { back = i; break; }
            }
            suppressComboEvent = true;
            try { if (kcCombo != null) kcCombo.SelectedIndex = back; }
            finally { suppressComboEvent = false; }
            if (back >= 0) selectedKc = kcList[back];
        }

        // Re-scan the chart every time the list is opened, so it always reflects what's on the chart.
        private void OnKcComboOpened(object sender, EventArgs e)
        {
            DetectKeltner();
        }

        // The popup has released the mouse. Open the MACD level window on the next dispatcher turn.
        private void OnKcComboClosed(object sender, EventArgs e)
        {
            if (pendingTrackMacd == null || kcCombo == null || macdPromptOpen)
                return;
            int idx = kcCombo.SelectedIndex;
            bool kept = idx >= 0 && idx < kcList.Count
                && !kcList[idx].IsEmaCross
                && ReferenceEquals(kcList[idx].Indicator, pendingTrackMacd);
            if (!kept)
            {
                pendingTrackMacd = null;
                return;
            }
            QueueTrackMacdPrompt();
        }

        private void QueueTrackMacdPrompt()
        {
            if (trackMacdPromptQueued || macdPromptOpen || pendingTrackMacd == null || kcCombo == null)
                return;
            trackMacdPromptQueued = true;
            NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind = pendingTrackMacd;
            KcSource previous = pendingTrackPrevious;
            kcCombo.Dispatcher.BeginInvoke(new Action(() =>
            {
                trackMacdPromptQueued = false;
                if (macdPromptOpen || !ReferenceEquals(pendingTrackMacd, ind))
                    return;
                if (kcCombo != null && kcCombo.IsDropDownOpen)
                {
                    QueueTrackMacdPrompt();
                    return;
                }
                pendingTrackMacd = null;
                macdPromptOpen = true;
                try { FinishTrackMacdPrompt(previous, ind); }
                finally { macdPromptOpen = false; }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        // resetToMidline is true when the tracked indicator changed. The same indicator keeps the user's line.
        private void RefreshLineCombo(bool resetToMidline)
        {
            if (lineCombo == null) return;
            KcSource src = selectedKc;
            if (src != null && src.IsEmaCross)
            {
                suppressLineEvent = true;
                try
                {
                    lineCombo.Items.Clear();
                    lineCombo.Items.Add("Cross");
                    linePlots = new List<int>();
                    lineCombo.SelectedIndex = 0;
                    lineCombo.IsEnabled = false;
                    selectedPlot = int.MinValue;
                    selectedLineName = "Cross";
                }
                finally { suppressLineEvent = false; }
                ApplyLineButtons("Cross");
                return;
            }
            // MACD is the trigger. The Line list is the Keltner channel the order price follows.
            KcSource lineSrc = src != null && IsMacd(src) ? keltnerPrice : src;
            List<int> plots = new List<int>();
            List<string> names = new List<string>();
            CollectPlots(lineSrc, plots, names);

            int mid = lineSrc == null ? -2 : lineSrc.MidPlot;
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

        // Named plots, plus a synthetic Midline when the first three plots have no mid name.
        private static void CollectPlots(KcSource src, List<int> plots, List<string> names)
        {
            int mid = src == null ? -2 : src.MidPlot;
            if (src == null || src.Indicator == null || src.Indicator.Plots == null || src.Indicator.Values == null)
                return;
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
            KcSource src = PriceSource();
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
            // 7 toggle track, 8 buy/sell, 9 cancel, 10 status, 11 the three order lists (last in the group).
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
            kcRow = new System.Windows.Controls.Grid { Margin = new Thickness(1, 1, 1, 3) };
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
                ToolTip = "Indicator the orders follow. Locked on the Keltner channel until Toggle track is checked. MACD keeps the Keltner line as the price and uses its level only as the trigger."
            };
            kcCombo.SelectionChanged += OnKcComboChanged;
            kcCombo.DropDownOpened   += OnKcComboOpened;
            kcCombo.DropDownClosed   += OnKcComboClosed;
            System.Windows.Controls.Grid.SetColumn(kcLabel, 0);
            System.Windows.Controls.Grid.SetColumn(kcCombo, 1);
            kcRow.Children.Add(kcLabel); kcRow.Children.Add(kcCombo);
            Place(kcRow, 1, 0, 2);

            // Row 2: which plot to follow. Midline is the default.
            lineRow = new System.Windows.Controls.Grid { Margin = new Thickness(1, 1, 1, 3) };
            lineRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            lineRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.TextBlock lineLabel = new System.Windows.Controls.TextBlock
            {
                Text = "Line", Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 6, 0)
            };
            lineCombo = new System.Windows.Controls.ComboBox
            {
                Height = 24, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Keltner plot the orders follow. Midline is selected by default. With MACD in Track, this line is still the order price and MACD is only the trigger."
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

            // Row 4: ticks offset. The number is the zone width; MaxTicksOffset only trims a typed value above the ceiling.
            offsetRow = new System.Windows.Controls.Grid();
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
                ToolTip = "Show or hide Track and Line. Orders keep following the selected line either way."
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
            // Three exclusive lists: working (NF), open fills (F), closed trades (W / L plus points).
            nfListPanel = new System.Windows.Controls.StackPanel();
            filledListPanel = new System.Windows.Controls.StackPanel();
            doneListPanel = new System.Windows.Controls.StackPanel();
            System.Windows.Controls.StackPanel orderLists = new System.Windows.Controls.StackPanel();
            orderLists.Children.Add(MakeOrderSection("Not filled", "Working orders that are not filled.", nfListPanel));
            orderLists.Children.Add(MakeOrderDivider());
            orderLists.Children.Add(MakeOrderSection("Filled", "Filled orders still open. Check one to set its stop or target, or to include it when the ATM strategy changes.", filledListPanel));
            legButton = new System.Windows.Controls.Button
            {
                Content = "SL / TP",
                Height = 24,
                Margin = new Thickness(4, 2, 4, 4),
                Style = null,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)),
                Foreground = Brushes.White,
                Visibility = Visibility.Collapsed,
                ToolTip = "Remove the stop or target, keep them on the ATM, follow an indicator, close the target when two EMAs cross, or close either leg when MACD crosses a level."
            };
            legButton.Click += OnLegButtonClick;
            orderLists.Children.Add(legButton);
            orderLists.Children.Add(MakeOrderDivider());
            orderLists.Children.Add(MakeOrderSection("Completed", "Closed trades. W is a win and L is a loss. The last number is points (4 ticks = 1 point).", doneListPanel));
            System.Windows.Controls.Border orderListBorder = new System.Windows.Controls.Border
            {
                Child = orderLists,
                MinHeight = 40,
                Margin = new Thickness(2, 2, 2, 2),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xC5, 0xCB, 0xD3)),
                Background = Brushes.White
            };
            Place(orderListBorder, 11, 0, 2);
            ApplyOptionVisibility();

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

        private static UIElement MakeOrderSection(string title, string tip, System.Windows.Controls.StackPanel rows)
        {
            System.Windows.Controls.StackPanel section = new System.Windows.Controls.StackPanel();
            section.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = title,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)),
                Margin = new Thickness(4, 3, 4, 0),
                ToolTip = tip
            });
            section.Children.Add(new System.Windows.Controls.ScrollViewer
            {
                Content = rows,
                MinHeight = 18,
                MaxHeight = 72,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
                Background = Brushes.White
            });
            return section;
        }

        private static UIElement MakeOrderDivider()
        {
            return new System.Windows.Controls.Border
            {
                Height = 1,
                Margin = new Thickness(4, 1, 4, 1),
                Background = new SolidColorBrush(Color.FromRgb(0xC5, 0xCB, 0xD3))
            };
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
            if (legButton != null) legButton.Click -= OnLegButtonClick;
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
                kcCombo.DropDownClosed   -= OnKcComboClosed;
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

        // Cached on the UI thread. The cross fires on the data thread and must not touch the selector.
        private void RememberAtm()
        {
            if (!UseChartTraderAtm)
            {
                liveAtmKey = "";
                liveAtm = null;
                return;
            }
            NinjaTrader.NinjaScript.AtmStrategy atm = SelectedAtm();
            string key = atm == null ? "" : AtmTemplateKey(atm);
            liveAtm = key != null && key.Length > 0 ? atm : null;
            liveAtmKey = key ?? "";
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
            if (selectedKc != null && selectedKc.IsEmaCross)
            {
                ArmEmaCrossFromUi(action);
                UpdateReadout();
                return;
            }
            if (selectedKc != null && IsMacd(selectedKc))
            {
                ArmMacdFromUi(action);
                UpdateReadout();
                return;
            }

            double mid = lastMid;
            if (double.IsNaN(mid))    { SetStatusNow("Midband not available yet."); return; }

            Account acct = SelectedAccount();
            if (acct == null)         { SetStatusNow("Select an account in Chart Trader."); return; }

            NinjaTrader.NinjaScript.AtmStrategy atm = UseChartTraderAtm ? SelectedAtm() : null;
            int  qty   = ReadQty();
            bool track = trackAll;
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
            ApplyOptionVisibility();
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
            ApplyOptionVisibility();
            if (ChartControl != null) ChartControl.InvalidateVisual();
        }

        // Show zone hides the offset controls. Track line hides Track and Line. Both keep working while hidden.
        private void ApplyOptionVisibility()
        {
            if (offsetRow != null)
                offsetRow.Visibility = showZone ? Visibility.Visible : Visibility.Collapsed;
            bool showTrack = trackBox != null && trackBox.IsChecked == true;
            if (kcRow != null)
                kcRow.Visibility = showTrack ? Visibility.Visible : Visibility.Collapsed;
            if (lineRow != null)
                lineRow.Visibility = showTrack ? Visibility.Visible : Visibility.Collapsed;
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
            bool macdMode = selectedKc != null && IsMacd(selectedKc);
            string macdText = macdMode ? (macdConfigured ? FormatMacd(macdLevel) : "--") : null;
            if (mid.ApproxCompare(shownMid) == 0 && count == shownCount && shownMacdText == macdText) return;
            shownMid = mid;
            shownCount = count;
            shownMacdText = macdText;
            if (ChartControl == null) return;

            string text = double.IsNaN(mid) ? "--" : Instrument.MasterInstrument.FormatPrice(mid);
            if (macdText != null) text += "  MACD " + macdText;
            if (count > 0) text += string.Format("  [{0}]", count);
            ChartControl.Dispatcher.InvokeAsync(() => { if (midText != null) midText.Text = text; });
        }

        // Both sides of the operation zone. Hidden when Show zone is off, the offset is 0, or there is no mid.
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            if (!showZone || !kcFound || ChartPanel == null || chartScale == null || RenderTarget == null)
                return;
            if (selectedKc != null && selectedKc.IsEmaCross)
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

        // Filled list: the entry is fully filled, or a partial fill whose remainder was cancelled.
        // A working partial stays NF.
        private static bool IsListedFilled(TrackedOrder t)
        {
            if (t == null || t.Order == null || t.Order.Filled <= 0 || t.Completed) return false;
            if (IsEntryFullyFilled(t)) return true;
            return Order.IsTerminalState(t.Order.OrderState);
        }

        // Not filled:  Ord-af3658-Oct-10-26_18-35-17-560-BUY-1-NF
        // Filled:     Ord-af3658-Oct-10-26_18-35-17-560-BUY-1-F
        // Won:        Ord-af3658-Oct-10-26_18-35-17-560-BUY-1-F-W-5.25
        // Lost:       Ord-af3658-Oct-10-26_18-35-17-560-BUY-1-L-4.75
        // Points are price difference / tick size / 4. A flat close is F-W-0.
        private static string OrderListText(TrackedOrder t)
        {
            string side = IsBuyAction(t.Action) ? "BUY" : "SELL";
            string tail;
            if (t.Completed)
            {
                string pts = FormatPoints(t.Points);
                tail = t.Points < 0 ? ("L-" + pts) : ("F-W-" + pts);
            }
            else
                tail = IsListedFilled(t) ? "F" : "NF";
            return (t.Label ?? "") + "-" + side + "-" + t.Quantity + "-" + tail;
        }

        private static string FormatPoints(double points)
        {
            double abs = Math.Abs(points);
            if (abs == 0) return "0";
            return abs.ToString("0.##", CultureInfo.InvariantCulture);
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
            acct.ExecutionUpdate += OnAccountExecution;
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
                try { acct.ExecutionUpdate -= OnAccountExecution; }
                catch { }
            }
        }

        private void OnAccountExecution(object sender, ExecutionEventArgs e)
        {
            if (e == null || e.Execution == null) return;
            Execution ex = e.Execution;
            if (ex.Instrument == null || Instrument == null || ex.Instrument.FullName != Instrument.FullName) return;
            SyncOrderList();
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

            if (o.OrderState == OrderState.Cancelled || o.OrderState == OrderState.Rejected)
                OnProtectiveState(o);

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

            Account rejectAccount = null;
            lock (sync)
            {
                if (tracked.Any(t => ContainsOrder(t.StopOrders, o) || ContainsOrder(t.TargetOrders, o)))
                    return;

                TrackedOrder match = null;
                if (owner != null)
                {
                    foreach (TrackedOrder t in tracked)
                    {
                        RefreshAtmOwner(t);
                        if (t.AtmOwner == null || !ReferenceEquals(t.AtmOwner, owner)) continue;
                        // A removed leg still owns this strategy, even when the cancelled order kept the name.
                        if (RejectsChild(t, stop) || !HasChildNamed(t, name))
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
                        if (HasChildNamed(t, name) && !RejectsChild(t, stop)) continue;
                        if (match == null || t.Order.Time > match.Order.Time)
                            match = t;
                    }
                }
                if (match == null) return;
                if (match.AtmOwner == null && owner != null) match.AtmOwner = owner;
                if (RejectsChild(match, stop))
                    rejectAccount = match.Account;
                else
                {
                    if (stop) match.StopOrders.Add(o);
                    else match.TargetOrders.Add(o);
                    NoteFill(match);
                    return;
                }
            }

            if (rejectAccount == null || !IsChangeable(o)) return;
            try { rejectAccount.Cancel(new[] { o }); }
            catch (Exception ex) { Print("KcMidTrader leg cancel error: " + ex.Message); }
        }

        // A removed leg, or a target waiting on an EMA cross, must not pick up an ATM replacement.
        private static bool RejectsChild(TrackedOrder t, bool stop)
        {
            if (t == null) return false;
            if (stop) return t.StopSource == LegFollow.None || t.StopSource == LegFollow.Macd;
            return t.TargetSource == LegFollow.None || t.TargetSource == LegFollow.EmaCross || t.TargetSource == LegFollow.Macd;
        }

        private static void RefreshAtmOwner(TrackedOrder t)
        {
            if (t == null || t.AtmOwner != null || t.Order == null) return;
            try { t.AtmOwner = t.Order.GetOwnerStrategy(); }
            catch { t.AtmOwner = null; }
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
            RememberAtm();
        }

        private void OnAtmSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (System.Threading.Interlocked.CompareExchange(ref suppressAtmEvents, 0, 0) != 0) return;
            RememberAtm();
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
        // Checked filled orders get new prices only on legs that are still on the ATM.
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
                        // A market entry has no resting price. Re-submitting it would send a second order at 0.
                        if (same || t.Order.OrderType == OrderType.Market) continue;
                        double px = t.Order.OrderType == OrderType.Limit ? t.Order.LimitPrice : t.Order.StopPrice;
                        t.PendingReplace = true;
                        t.SwapAtm = true;
                        t.ReplaceAtmName = key;
                        t.ReplaceAtm = key.Length == 0 ? null : atm;
                        t.ReplacePrice = px;
                        t.FollowAtm = true;
                        toCancel.Add(t);
                    }
                    else if (t.Order.Filled > 0 && t.SelectedForAtm && !t.Completed && !same
                        && ((t.StopSource == LegFollow.Atm && HasWorkingChild(t.StopOrders))
                            || (t.TargetSource == LegFollow.Atm && HasWorkingChild(t.TargetOrders))))
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
                    changed += RepriceProtective(t, atm, notes,
                        t.StopSource == LegFollow.Atm, t.TargetSource == LegFollow.Atm);
                    t.AtmName = key;
                }
            }

            if (toCancel.Count == 0 && toReprice.Count == 0)
                return;

            string template = key.Length == 0 ? "no ATM" : key;
            string text = string.Format("{0} NF entr{1} set to {2}. {3} selected ATM stop/target order{4} changed.",
                toCancel.Count, toCancel.Count == 1 ? "y" : "ies", template,
                changed, changed == 1 ? "" : "s");
            if (atm == null && toReprice.Count > 0)
                text += " Placed stops and targets were left unchanged.";
            if (notes.Count > 0)
                text += " " + notes[0];
            SetStatusNow(text);
            SyncOrderList();
        }

        private int RepriceProtective(TrackedOrder t, NinjaTrader.NinjaScript.AtmStrategy atm, List<string> notes,
                                      bool includeStops, bool includeTargets)
        {
            if (!includeStops && !includeTargets) return 0;
            double fill = t.FillPrice;
            if (fill <= 0 && t.Order != null) fill = t.Order.AverageFillPrice;
            if (fill <= 0)
            {
                notes.Add((t.Label ?? "Order") + " has no fill price yet.");
                return 0;
            }

            bool isLong = t.Action == OrderAction.Buy || t.Action == OrderAction.BuyToCover;
            List<Order> stops = new List<Order>();
            List<Order> targets = new List<Order>();
            lock (sync)
            {
                if (includeStops) stops = t.StopOrders.ToList();
                if (includeTargets) targets = t.TargetOrders.ToList();
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

            return SetProtectivePrice(t, order, price, notes);
        }

        private bool SetProtectivePrice(TrackedOrder t, Order order, double price, List<string> notes)
        {
            if (t == null || t.Account == null || order == null || !IsChangeable(order)) return false;
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
                if (notes != null) notes.Add("Could not change " + (order.Name ?? "order") + ".");
                return false;
            }
        }

        private static bool HasWorkingChild(List<Order> orders)
        {
            if (orders == null) return false;
            for (int i = 0; i < orders.Count; i++)
            {
                Order o = orders[i];
                if (o != null && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
                    || o.OrderState == OrderState.PartFilled))
                    return true;
            }
            return false;
        }

        // Long stop and short target stay below the market. Short stop and long target stay above it.
        private bool ProtectiveOnRestingSide(bool isLong, bool isStop, double price)
        {
            double market = lastPrice;
            if (double.IsNaN(market) || market <= 0)
            {
                market = isLong ? lastBid : lastAsk;
                if (double.IsNaN(market) || market <= 0) market = lastPrice;
            }
            if (double.IsNaN(market) || market <= 0) return false;
            bool below = (isLong && isStop) || (!isLong && !isStop);
            int cmp = price.ApproxCompare(market);
            if (cmp == 0) return false;
            return below ? cmp < 0 : cmp > 0;
        }

        private void TrackProtective(int barIndex)
        {
            List<TrackedOrder> orders;
            lock (sync) orders = tracked.ToList();
            foreach (TrackedOrder t in orders)
            {
                if (t.Completed || t.Order == null || t.Order.Filled <= 0) continue;
                if (t.StopSource == LegFollow.Indicator)
                    ReportFollow(FollowLeg(t, true, barIndex));
                if (t.TargetSource == LegFollow.Indicator)
                    ReportFollow(FollowLeg(t, false, barIndex));
                // EMA cross is closed-bar only, in TrackEmaCrossNow. A forming bar must not open and close the same trade.
            }
        }

        private void ReportFollow(string note)
        {
            if (!string.IsNullOrEmpty(note)) SetStatus(note);
        }

        // null means the leg was moved, or it was already at the plot. A string is a pause or miss.
        private string FollowLeg(TrackedOrder t, bool isStop, int barIndex)
        {
            NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind;
            int plot;
            lock (sync)
            {
                if ((isStop ? t.StopSource : t.TargetSource) != LegFollow.Indicator) return null;
                ind = isStop ? t.StopIndicator : t.TargetIndicator;
                plot = isStop ? t.StopPlot : t.TargetPlot;
            }
            string which = isStop ? "Stop" : "Target";
            double raw;
            if (ind == null || !TryReadSeries(ind, plot, barIndex, out raw))
            {
                if (NoteLegFlag(t, isStop, false))
                    return which + " left in place: its indicator has no value.";
                return null;
            }

            double price = Instrument.MasterInstrument.RoundToTickSize(raw);
            bool isLong = t.Action == OrderAction.Buy || t.Action == OrderAction.BuyToCover;
            if (!ProtectiveOnRestingSide(isLong, isStop, price))
            {
                if (NoteLegFlag(t, isStop, true))
                    return which + " paused: " + Instrument.MasterInstrument.FormatPrice(price) + " is on the wrong side of the market.";
                return null;
            }

            ClearLegFlags(t, isStop);
            List<Order> children;
            lock (sync) children = (isStop ? t.StopOrders : t.TargetOrders).ToList();
            List<string> notes = new List<string>();
            foreach (Order child in children)
                SetProtectivePrice(t, child, price, notes);
            return notes.Count > 0 ? notes[0] : null;
        }

        private bool NoteLegFlag(TrackedOrder t, bool isStop, bool paused)
        {
            bool already;
            lock (sync)
            {
                already = paused
                    ? (isStop ? t.StopPaused : t.TargetPaused)
                    : (isStop ? t.StopMissing : t.TargetMissing);
                if (isStop)
                {
                    t.StopPaused = paused;
                    t.StopMissing = !paused;
                }
                else
                {
                    t.TargetPaused = paused;
                    t.TargetMissing = !paused;
                }
            }
            return !already;
        }

        private void ClearLegFlags(TrackedOrder t, bool isStop)
        {
            lock (sync)
            {
                if (isStop) { t.StopPaused = false; t.StopMissing = false; }
                else { t.TargetPaused = false; t.TargetMissing = false; }
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
            public bool   Completed;
            public bool   Won;
        }

        // One open order while its exit quantity and price are being matched.
        private sealed class ExitScan
        {
            public TrackedOrder Track;
            public int Need;
            public int Got;
            public double Notional;
            public bool EntryDone;
            public DateTime Since;
            public bool IsBuy;
            public NinjaTrader.NinjaScript.StrategyBase Owner;
            public List<Order> PricedChildren;
            public List<Order> AllChildren;
            public double EntryPx;
        }

        // Marks a tracked order complete once its filled quantity is closed.
        // Exit price comes from linked ATM stops/targets, then same-strategy exits, then a Close/Exit order.
        // Each exit quantity is given to only one order.
        private void SettleOutcomes()
        {
            List<ExitScan> scans = new List<ExitScan>();
            lock (sync)
            {
                foreach (TrackedOrder t in tracked)
                {
                    if (t.Completed || t.Order == null || t.Order.Filled <= 0) continue;
                    ExitScan s = new ExitScan();
                    s.Track = t;
                    s.Need = t.Order.Filled;
                    s.EntryDone = IsEntryFullyFilled(t) || Order.IsTerminalState(t.Order.OrderState);
                    s.Since = t.Order.Time;
                    s.IsBuy = IsBuyAction(t.Action);
                    s.Owner = t.AtmOwner;
                    s.EntryPx = t.FillPrice > 0 ? t.FillPrice : t.Order.AverageFillPrice;
                    s.AllChildren = new List<Order>();
                    s.AllChildren.AddRange(t.StopOrders);
                    s.AllChildren.AddRange(t.TargetOrders);
                    s.AllChildren.AddRange(t.CloseOrders);
                    s.PricedChildren = new List<Order>();
                    foreach (Order c in s.AllChildren)
                    {
                        if (c == null || c.Filled <= 0 || c.AverageFillPrice <= 0) continue;
                        s.Got += c.Filled;
                        s.Notional += c.AverageFillPrice * c.Filled;
                        s.PricedChildren.Add(c);
                    }
                    scans.Add(s);
                }
            }
            if (scans.Count == 0) return;

            scans.Sort(delegate (ExitScan a, ExitScan b) { return a.Since.CompareTo(b.Since); });

            Dictionary<Account, List<ExitScan>> byAccount = new Dictionary<Account, List<ExitScan>>();
            foreach (ExitScan s in scans)
            {
                if (s.Track.Account == null) continue;
                List<ExitScan> list;
                if (!byAccount.TryGetValue(s.Track.Account, out list))
                {
                    list = new List<ExitScan>();
                    byAccount[s.Track.Account] = list;
                }
                list.Add(s);
            }

            foreach (KeyValuePair<Account, List<ExitScan>> pair in byAccount)
            {
                Execution[] execs;
                try
                {
                    lock (pair.Key.Executions) execs = pair.Key.Executions.ToArray();
                }
                catch { continue; }

                Array.Sort(execs, delegate (Execution a, Execution b) { return a.Time.CompareTo(b.Time); });
                foreach (Execution ex in execs)
                    AssignExit(pair.Value, ex);
            }

            double tick = 0;
            try { tick = TickSize; }
            catch { tick = 0; }
            if (tick <= 0 && Instrument != null && Instrument.MasterInstrument != null)
                tick = Instrument.MasterInstrument.TickSize;
            if (tick <= 0) return;

            lock (sync)
            {
                foreach (ExitScan s in scans)
                {
                    if (s.Track.Completed) continue;
                    if (!s.EntryDone || s.Got < s.Need || s.Need <= 0 || s.EntryPx <= 0) continue;
                    double got = s.Got;
                    double notional = s.Notional;
                    if (got > s.Need)
                    {
                        notional = notional * s.Need / got;
                        got = s.Need;
                    }
                    if (got <= 0) continue;
                    double exitPx = notional / got;
                    double diff = s.IsBuy ? (exitPx - s.EntryPx) : (s.EntryPx - exitPx);
                    double points = Math.Round(diff / tick / 4.0, 2, MidpointRounding.AwayFromZero);
                    if (points == 0) points = 0;
                    s.Track.Completed = true;
                    s.Track.Points = points;
                }
            }
        }

        private void AssignExit(List<ExitScan> scans, Execution ex)
        {
            if (ex == null || ex.Quantity <= 0 || ex.Price <= 0) return;
            if (ex.Instrument == null || Instrument == null || ex.Instrument.FullName != Instrument.FullName) return;

            int left = ex.Quantity;
            for (int i = 0; i < scans.Count && left > 0; i++)
            {
                ExitScan s = scans[i];
                if (!s.EntryDone || s.Got >= s.Need) continue;
                if (ex.Time < s.Since) continue;
                if (s.Track.Order != null && ex.Order == s.Track.Order) continue;
                if (ContainsOrder(s.PricedChildren, ex.Order)) continue;

                bool ownChild = ContainsOrder(s.AllChildren, ex.Order);
                if (!ownChild && !BelongsToScan(s, ex)) continue;
                if (IsSellExecution(ex) != s.IsBuy) continue;

                int room = s.Need - s.Got;
                int take = left < room ? left : room;
                s.Got += take;
                s.Notional += ex.Price * take;
                left -= take;
            }
        }

        private static bool ContainsOrder(List<Order> orders, Order o)
        {
            if (o == null || orders == null) return false;
            for (int i = 0; i < orders.Count; i++)
                if (orders[i] == o) return true;
            return false;
        }

        private static bool BelongsToScan(ExitScan s, Execution ex)
        {
            NinjaTrader.NinjaScript.StrategyBase owner = null;
            try { owner = ex.Order == null ? null : ex.Order.GetOwnerStrategy(); }
            catch { owner = null; }
            if (s.Owner != null && owner != null)
                return ReferenceEquals(s.Owner, owner);
            return IsClosingOrderName(ex.Order);
        }

        private static bool IsSellExecution(Execution ex)
        {
            if (ex.Order != null)
            {
                OrderAction action = ex.Order.OrderAction;
                if (action == OrderAction.Sell || action == OrderAction.SellShort) return true;
                if (action == OrderAction.Buy || action == OrderAction.BuyToCover) return false;
            }
            return ex.MarketPosition == MarketPosition.Short;
        }

        private static bool IsClosingOrderName(Order o)
        {
            if (o == null || string.IsNullOrEmpty(o.Name)) return false;
            return o.Name.StartsWith("Close", StringComparison.OrdinalIgnoreCase)
                || o.Name.StartsWith("Exit", StringComparison.OrdinalIgnoreCase)
                || o.Name.StartsWith("Flatten", StringComparison.OrdinalIgnoreCase);
        }

        private void SyncOrderList()
        {
            SettleOutcomes();
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
            if (ChartControl == null) return;
            if (sig == orderListSig)
            {
                ChartControl.Dispatcher.InvokeAsync(UpdateLegButton);
                return;
            }
            orderListSig = sig;
            ChartControl.Dispatcher.InvokeAsync(RefreshOrderList);
        }

        private void RefreshOrderList()
        {
            if (nfListPanel == null || filledListPanel == null || doneListPanel == null) return;
            List<OrderRow> nf = new List<OrderRow>();
            List<OrderRow> filled = new List<OrderRow>();
            List<OrderRow> done = new List<OrderRow>();
            lock (sync)
            {
                foreach (TrackedOrder t in tracked)
                {
                    OrderRow row = new OrderRow();
                    row.Label = t.Label ?? "";
                    row.IsBuy = IsBuyAction(t.Action);
                    row.Text = OrderListText(t);
                    row.Completed = t.Completed;
                    row.Won = t.Points >= 0;
                    if (t.Completed)
                        done.Add(row);
                    else if (IsListedFilled(t))
                    {
                        row.Checkable = true;
                        row.Checked = t.SelectedForAtm;
                        filled.Add(row);
                    }
                    else
                        nf.Add(row);
                }
            }

            FillOrderSection(nfListPanel, nf, "No working orders");
            FillOrderSection(filledListPanel, filled, "No filled orders");
            FillOrderSection(doneListPanel, done, "No completed trades");
            UpdateLegButton();
        }

        private void FillOrderSection(System.Windows.Controls.StackPanel panel, List<OrderRow> rows, string empty)
        {
            panel.Children.Clear();
            if (rows.Count == 0)
            {
                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = empty,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)),
                    FontSize = 11,
                    Margin = new Thickness(4, 2, 4, 4)
                });
                return;
            }

            foreach (OrderRow row in rows)
            {
                Color color = row.Completed
                    ? (row.Won ? Color.FromRgb(0x1E, 0x7A, 0x3C) : Color.FromRgb(0xA3, 0x24, 0x24))
                    : OrderListColor(row.IsBuy);
                Brush rowBrush = new SolidColorBrush(color);
                if (rowBrush.CanFreeze) rowBrush.Freeze();
                if (!row.Checkable)
                {
                    panel.Children.Add(new System.Windows.Controls.TextBlock
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
                panel.Children.Add(box);
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
            UpdateLegButton();
        }

        private TrackedOrder SoleSelectedFilled()
        {
            TrackedOrder only = null;
            int n = 0;
            lock (sync)
            {
                foreach (TrackedOrder t in tracked)
                {
                    if (!t.SelectedForAtm || t.Completed || !IsListedFilled(t)) continue;
                    n++;
                    only = t;
                }
            }
            return n == 1 ? only : null;
        }

        private void UpdateLegButton()
        {
            if (legButton == null) return;
            TrackedOrder only = SoleSelectedFilled();
            if (only == null)
            {
                legButton.Visibility = Visibility.Collapsed;
                return;
            }
            legButton.Visibility = Visibility.Visible;
            legButton.IsEnabled = true;
            legButton.ToolTip = "Remove the stop or target, keep them on the ATM, follow an indicator, close the target when two EMAs cross, or close either leg when MACD crosses a level.";
        }

        private void OnLegButtonClick(object sender, RoutedEventArgs e)
        {
            DetectKeltner();
            TrackedOrder t = SoleSelectedFilled();
            UpdateLegButton();
            if (t == null || legButton == null || !legButton.IsEnabled) return;
            ShowLegDialog(t);
        }

        private bool ShowEmaCrossDialog()
        {
            List<KcSource> sources = CollectSources(false, null);
            List<KcSource> emas = new List<KcSource>();
            foreach (KcSource k in sources)
                if (IsEma(k)) emas.Add(k);
            if (emas.Count < 2)
            {
                SetStatusNow("EMA cross needs two EMAs.");
                return false;
            }

            bool accepted = false;
            Window win = new Window
            {
                Title = "EMA cross",
                Owner = chartWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Width = 460,
                Background = Brushes.White,
                ShowInTaskbar = false
            };

            System.Windows.Controls.StackPanel root = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Buy opens when EMA A crosses above EMA B. Sell opens when EMA A crosses below EMA B. A cross that is already true does not open or close.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            System.Windows.Controls.Grid grid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });

            System.Windows.Controls.ComboBox emaA = new System.Windows.Controls.ComboBox { MinWidth = 220, Margin = new Thickness(8, 2, 0, 2) };
            System.Windows.Controls.ComboBox emaB = new System.Windows.Controls.ComboBox { MinWidth = 220, Margin = new Thickness(8, 2, 0, 2) };
            bool emaBusy = false;
            System.Windows.Controls.SelectionChangedEventHandler onEma = delegate
            {
                if (emaBusy) return;
                emaBusy = true;
                try { SyncExclusiveEmas(emaA, emaB, emas, null, null); }
                finally { emaBusy = false; }
            };
            emaA.SelectionChanged += onEma;
            emaB.SelectionChanged += onEma;
            System.Windows.Controls.TextBlock aLabel = EmaLabel("EMA A");
            System.Windows.Controls.TextBlock bLabel = EmaLabel("EMA B");
            System.Windows.Controls.Grid.SetRow(aLabel, 0);
            System.Windows.Controls.Grid.SetRow(emaA, 0);
            System.Windows.Controls.Grid.SetColumn(emaA, 1);
            System.Windows.Controls.Grid.SetRow(bLabel, 1);
            System.Windows.Controls.Grid.SetRow(emaB, 1);
            System.Windows.Controls.Grid.SetColumn(emaB, 1);
            grid.Children.Add(aLabel);
            grid.Children.Add(emaA);
            grid.Children.Add(bLabel);
            grid.Children.Add(emaB);
            emaBusy = true;
            try { SyncExclusiveEmas(emaA, emaB, emas, crossEmaA, crossEmaB); }
            finally { emaBusy = false; }
            root.Children.Add(grid);

            System.Windows.Controls.TextBlock errorText = new System.Windows.Controls.TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0x24, 0x24)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12
            };
            root.Children.Add(errorText);

            System.Windows.Controls.StackPanel buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            System.Windows.Controls.Button confirm = new System.Windows.Controls.Button
            {
                Content = "Confirm", Width = 88, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true
            };
            System.Windows.Controls.Button cancel = new System.Windows.Controls.Button
            {
                Content = "Cancel", Width = 88, Height = 26, IsCancel = true
            };
            confirm.Click += delegate
            {
                FollowPick a = emaA.SelectedItem as FollowPick;
                FollowPick b = emaB.SelectedItem as FollowPick;
                if (a == null || b == null || a.Source == null || b.Source == null
                    || a.Source.Indicator == null || b.Source.Indicator == null)
                {
                    errorText.Text = "Choose the two EMAs.";
                    return;
                }
                if (ReferenceEquals(a.Source.Indicator, b.Source.Indicator))
                {
                    errorText.Text = "Choose two different EMAs.";
                    return;
                }
                crossEmaA = a.Source.Indicator;
                crossEmaB = b.Source.Indicator;
                crossEmaAName = a.Text;
                crossEmaBName = b.Text;
                crossConfigured = true;
                accepted = true;
                win.DialogResult = true;
            };
            buttons.Children.Add(confirm);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            win.Content = root;
            try { win.ShowDialog(); }
            catch (Exception ex) { Print("KcMidTrader EMA cross window error: " + ex.Message); }
            return accepted;
        }

        private void ShowLegDialog(TrackedOrder t)
        {
            if (t == null || t.Completed) return;

            Window win = new Window
            {
                Title = "Stop and target",
                Owner = chartWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                Width = 520,
                Background = Brushes.White,
                ShowInTaskbar = false
            };

            System.Windows.Controls.StackPanel root = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
            List<KcSource> sources = CollectSources(false, null);
            List<LegDialogRow> rows = new List<LegDialogRow>();
            rows.Add(BuildLegRow(win, root, "Stop", true, t, sources));
            rows.Add(BuildLegRow(win, root, "Target", false, t, sources));

            System.Windows.Controls.TextBlock errorText = new System.Windows.Controls.TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0x24, 0x24)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12
            };
            root.Children.Add(errorText);

            System.Windows.Controls.StackPanel buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            System.Windows.Controls.Button confirm = new System.Windows.Controls.Button
            {
                Content = "Confirm", Width = 88, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true
            };
            System.Windows.Controls.Button cancel = new System.Windows.Controls.Button
            {
                Content = "Cancel", Width = 88, Height = 26, IsCancel = true
            };
            confirm.Click += delegate
            {
                string error;
                try { error = ApplyLegDialog(t, rows); }
                catch (Exception ex)
                {
                    Print("KcMidTrader SL/TP apply error: " + ex.Message);
                    error = ex.Message;
                }
                if (error != null) { errorText.Text = error; return; }
                win.DialogResult = true;
            };
            buttons.Children.Add(confirm);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            win.Content = root;
            try { win.ShowDialog(); }
            catch (Exception ex) { Print("KcMidTrader SL/TP window error: " + ex.Message); }
        }

        private LegDialogRow BuildLegRow(Window owner, System.Windows.Controls.StackPanel root, string caption, bool isStop, TrackedOrder t, List<KcSource> sources)
        {
            System.Windows.Controls.Grid grid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            System.Windows.Controls.TextBlock label = new System.Windows.Controls.TextBlock
            {
                Text = caption,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)),
                FontWeight = FontWeights.Bold
            };
            System.Windows.Controls.ComboBox source = new System.Windows.Controls.ComboBox { MinWidth = 180, Margin = new Thickness(0, 0, 8, 0) };
            System.Windows.Controls.ComboBox band = new System.Windows.Controls.ComboBox { MinWidth = 110, Visibility = Visibility.Collapsed };
            System.Windows.Controls.Button macdButton = new System.Windows.Controls.Button
            {
                Content = "Value", MinWidth = 64, Height = 22, Margin = new Thickness(0, 0, 0, 0),
                Visibility = Visibility.Collapsed
            };
            source.MaxDropDownHeight = 320;
            source.Items.Add(new FollowPick { Text = "ATM strategy", Kind = FollowKind.Atm });
            source.Items.Add(new FollowPick { Text = "None", Kind = FollowKind.None });

            List<KcSource> emas = new List<KcSource>();
            if (!isStop && sources != null)
            {
                foreach (KcSource k in sources)
                    if (IsEma(k)) emas.Add(k);
                // Keep this above the indicator names so a long list cannot hide it.
                if (emas.Count >= 2)
                    source.Items.Add(new FollowPick { Text = "EMA cross", Kind = FollowKind.EmaCross });
            }
            if (sources != null)
                foreach (KcSource k in sources)
                    source.Items.Add(new FollowPick { Text = k.Label, Source = k, Kind = FollowKind.Indicator });

            System.Windows.Controls.Grid emaPanel = null;
            System.Windows.Controls.ComboBox emaA = null;
            System.Windows.Controls.ComboBox emaB = null;
            bool emaBusy = false;
            if (emas.Count >= 2)
            {
                emaPanel = new System.Windows.Controls.Grid { Margin = new Thickness(64, 0, 0, 4), Visibility = Visibility.Collapsed };
                emaPanel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                emaPanel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                emaPanel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                emaPanel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                emaA = new System.Windows.Controls.ComboBox { MinWidth = 180, Margin = new Thickness(8, 2, 0, 2) };
                emaB = new System.Windows.Controls.ComboBox { MinWidth = 180, Margin = new Thickness(8, 2, 0, 2) };
                System.Windows.Controls.SelectionChangedEventHandler onEma = delegate
                {
                    if (emaBusy) return;
                    emaBusy = true;
                    try { SyncExclusiveEmas(emaA, emaB, emas, null, null); }
                    finally { emaBusy = false; }
                };
                emaA.SelectionChanged += onEma;
                emaB.SelectionChanged += onEma;
                System.Windows.Controls.TextBlock aLabel = EmaLabel("EMA A");
                System.Windows.Controls.TextBlock bLabel = EmaLabel("EMA B");
                System.Windows.Controls.Grid.SetRow(aLabel, 0);
                System.Windows.Controls.Grid.SetRow(emaA, 0);
                System.Windows.Controls.Grid.SetColumn(emaA, 1);
                System.Windows.Controls.Grid.SetRow(bLabel, 1);
                System.Windows.Controls.Grid.SetRow(emaB, 1);
                System.Windows.Controls.Grid.SetColumn(emaB, 1);
                emaPanel.Children.Add(aLabel);
                emaPanel.Children.Add(emaA);
                emaPanel.Children.Add(bLabel);
                emaPanel.Children.Add(emaB);
            }

            int savedPlot;
            NinjaTrader.Gui.NinjaScript.IndicatorRenderBase savedInd, savedA, savedB;
            LegFollow mode;
            bool savedMacd;
            double savedMacdLevel;
            lock (sync)
            {
                mode = isStop ? t.StopSource : t.TargetSource;
                savedInd = isStop ? t.StopIndicator : t.TargetIndicator;
                savedPlot = isStop ? t.StopPlot : t.TargetPlot;
                savedA = t.TargetEmaA;
                savedB = t.TargetEmaB;
                savedMacd = isStop ? t.StopMacdSet : t.TargetMacdSet;
                savedMacdLevel = isStop ? t.StopMacdLevel : t.TargetMacdLevel;
            }
            LegDialogRow row = new LegDialogRow
            {
                IsStop = isStop,
                SourceBox = source,
                BandBox = band,
                EmaABox = emaA,
                EmaBBox = emaB,
                EmaPanel = emaPanel,
                MacdButton = macdButton
            };
            if (mode == LegFollow.Macd && savedMacd && savedInd != null)
            {
                row.MacdSet = true;
                row.MacdLevel = savedMacdLevel;
                row.MacdIndicator = savedInd;
            }
            source.SelectionChanged += delegate
            {
                if (suppressLegDialog) return;
                FollowPick pick = source.SelectedItem as FollowPick;
                if (pick != null && pick.Kind == FollowKind.Indicator && IsMacd(pick.Source))
                {
                    if (row.MacdPromptOpen) return;
                    int picked = source.SelectedIndex;
                    row.MacdPromptOpen = true;
                    source.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (source.SelectedIndex != picked) return;
                            FollowPick now = source.SelectedItem as FollowPick;
                            if (now == null || now.Kind != FollowKind.Indicator || !IsMacd(now.Source)) return;
                            double chosen;
                            if (!PromptLegMacd(owner, t, row, now, savedInd, savedMacd, savedMacdLevel, mode, out chosen))
                            {
                                suppressLegDialog = true;
                                try { source.SelectedIndex = row.PriorIndex; }
                                finally { suppressLegDialog = false; }
                                ShowLegChoice(source, band, emaPanel, savedPlot, macdButton, row.MacdSet, row.MacdLevel);
                                return;
                            }
                            row.MacdSet = true;
                            row.MacdLevel = chosen;
                            row.MacdIndicator = now.Source.Indicator;
                            row.PriorIndex = source.SelectedIndex;
                            ShowLegChoice(source, band, emaPanel, savedPlot, macdButton, row.MacdSet, row.MacdLevel);
                        }
                        finally { row.MacdPromptOpen = false; }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                    return;
                }
                row.PriorIndex = source.SelectedIndex;
                ShowLegChoice(source, band, emaPanel, savedPlot, macdButton, row.MacdSet, row.MacdLevel);
            };
            macdButton.Click += delegate
            {
                FollowPick pick = source.SelectedItem as FollowPick;
                if (pick == null || pick.Kind != FollowKind.Indicator || !IsMacd(pick.Source)) return;
                double chosen;
                if (!PromptLegMacd(owner, t, row, pick, savedInd, savedMacd, savedMacdLevel, mode, out chosen)) return;
                row.MacdSet = true;
                row.MacdLevel = chosen;
                row.MacdIndicator = pick.Source.Indicator;
                ShowLegChoice(source, band, emaPanel, savedPlot, macdButton, row.MacdSet, row.MacdLevel);
            };

            int sel = 0;
            if (mode == LegFollow.None)
                sel = 1;
            else if (!isStop && mode == LegFollow.EmaCross)
                sel = IndexOfKind(source, FollowKind.EmaCross);
            else if ((mode == LegFollow.Indicator || mode == LegFollow.Macd) && savedInd != null)
                sel = IndexOfIndicator(source, savedInd);
            if (sel < 0) sel = 0;

            suppressLegDialog = true;
            try
            {
                source.SelectedIndex = sel;
                row.PriorIndex = sel;
                ShowLegChoice(source, band, emaPanel, savedPlot, macdButton, row.MacdSet, row.MacdLevel);
                if (emaA != null)
                {
                    emaBusy = true;
                    try { SyncExclusiveEmas(emaA, emaB, emas, savedA, savedB); }
                    finally { emaBusy = false; }
                }
            }
            finally { suppressLegDialog = false; }

            System.Windows.Controls.StackPanel side = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };
            side.Children.Add(band);
            side.Children.Add(macdButton);
            System.Windows.Controls.Grid.SetColumn(label, 0);
            System.Windows.Controls.Grid.SetColumn(source, 1);
            System.Windows.Controls.Grid.SetColumn(side, 2);
            grid.Children.Add(label);
            grid.Children.Add(source);
            grid.Children.Add(side);
            root.Children.Add(grid);
            if (emaPanel != null) root.Children.Add(emaPanel);
            return row;
        }

        // Stored level when this leg already follows this MACD. Otherwise the suggestion for this side.
        private bool PromptLegMacd(Window owner, TrackedOrder t, LegDialogRow row, FollowPick pick,
                                   NinjaTrader.Gui.NinjaScript.IndicatorRenderBase savedInd, bool savedMacd, double savedLevel,
                                   LegFollow mode, out double level)
        {
            level = 0;
            if (t == null || pick == null || pick.Source == null || pick.Source.Indicator == null) return false;
            bool sameSession = row.MacdSet && row.MacdIndicator != null
                && ReferenceEquals(row.MacdIndicator, pick.Source.Indicator);
            bool sameSaved = mode == LegFollow.Macd && savedMacd && savedInd != null
                && ReferenceEquals(savedInd, pick.Source.Indicator);
            double suggested = 0;
            string reason = null;
            bool got = false;
            bool isLong = IsBuyAction(t.Action);
            try { got = TrySuggestLegMacd(pick.Source.Indicator, isLong, row.IsStop, out suggested, out reason); }
            catch (Exception ex)
            {
                Print("KcMidTrader MACD suggestion error: " + ex.Message);
                got = false;
            }
            string suggestion = got
                ? "Suggested: " + FormatMacd(suggested) + ". " + reason + "."
                : "MACD has no closed value yet. Type a level.";
            string prefill;
            if (sameSession) prefill = FormatMacd(row.MacdLevel);
            else if (sameSaved) prefill = FormatMacd(savedLevel);
            else prefill = got ? FormatMacd(suggested) : "";
            string intro = MacdHitSign(isLong, row.IsStop) < 0
                ? "This leg closes when the MACD line crosses down through the level. A level that is already crossed does not close until the line leaves it and crosses again."
                : "This leg closes when the MACD line crosses up through the level. A level that is already crossed does not close until the line leaves it and crosses again.";
            return ShowMacdLevelDialog(owner, row.IsStop ? "MACD stop" : "MACD target", intro, prefill, suggestion, out level);
        }

        private static System.Windows.Controls.TextBlock EmaLabel(string text)
        {
            return new System.Windows.Controls.TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)),
                Margin = new Thickness(0, 2, 0, 2)
            };
        }

        private static int IndexOfKind(System.Windows.Controls.ComboBox box, FollowKind kind)
        {
            for (int i = 0; i < box.Items.Count; i++)
            {
                FollowPick pick = box.Items[i] as FollowPick;
                if (pick != null && pick.Kind == kind) return i;
            }
            return -1;
        }

        // EMA A hides the EMA chosen in B, and EMA B hides the EMA chosen in A.
        private static void SyncExclusiveEmas(System.Windows.Controls.ComboBox emaA, System.Windows.Controls.ComboBox emaB,
                                               List<KcSource> emas,
                                               NinjaTrader.Gui.NinjaScript.IndicatorRenderBase preferA,
                                               NinjaTrader.Gui.NinjaScript.IndicatorRenderBase preferB)
        {
            if (preferA == null) preferA = SelectedEma(emaA);
            if (preferB == null) preferB = SelectedEma(emaB);
            if (preferA != null && ReferenceEquals(preferA, preferB)) preferB = null;
            FillEmaBox(emaA, emas, preferB, preferA);
            FillEmaBox(emaB, emas, SelectedEma(emaA), preferB);
        }

        private static NinjaTrader.Gui.NinjaScript.IndicatorRenderBase SelectedEma(System.Windows.Controls.ComboBox box)
        {
            FollowPick pick = box == null ? null : box.SelectedItem as FollowPick;
            return pick != null && pick.Source != null ? pick.Source.Indicator : null;
        }

        private static void FillEmaBox(System.Windows.Controls.ComboBox box, List<KcSource> emas,
                                        NinjaTrader.Gui.NinjaScript.IndicatorRenderBase exclude,
                                        NinjaTrader.Gui.NinjaScript.IndicatorRenderBase prefer)
        {
            if (box == null) return;
            box.Items.Clear();
            if (emas != null)
            {
                foreach (KcSource k in emas)
                {
                    if (k == null || k.Indicator == null) continue;
                    if (exclude != null && ReferenceEquals(k.Indicator, exclude)) continue;
                    box.Items.Add(new FollowPick { Text = k.Label, Source = k, Kind = FollowKind.Indicator });
                }
            }
            int sel = IndexOfIndicator(box, prefer);
            box.SelectedIndex = sel >= 0 ? sel : (box.Items.Count > 0 ? 0 : -1);
        }

        private static int IndexOfIndicator(System.Windows.Controls.ComboBox box, NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind)
        {
            if (box == null || ind == null) return -1;
            for (int i = 0; i < box.Items.Count; i++)
            {
                FollowPick pick = box.Items[i] as FollowPick;
                if (pick != null && pick.Source != null && ReferenceEquals(pick.Source.Indicator, ind))
                    return i;
            }
            return -1;
        }

        private static void ShowLegChoice(System.Windows.Controls.ComboBox source, System.Windows.Controls.ComboBox band,
                                           System.Windows.Controls.Panel emaPanel, int savedPlot,
                                           System.Windows.Controls.Button macdButton, bool macdSet, double macdLevel)
        {
            FollowPick pick = source.SelectedItem as FollowPick;
            bool ema = pick != null && pick.Kind == FollowKind.EmaCross;
            bool macd = pick != null && pick.Kind == FollowKind.Indicator && IsMacd(pick.Source);
            if (emaPanel != null)
                emaPanel.Visibility = ema ? Visibility.Visible : Visibility.Collapsed;
            if (macdButton != null)
            {
                macdButton.Visibility = macd ? Visibility.Visible : Visibility.Collapsed;
                macdButton.Content = macd && macdSet ? FormatMacd(macdLevel) : "Value";
            }
            if (ema || macd)
            {
                band.Visibility = Visibility.Collapsed;
                return;
            }
            if (macdButton != null) macdButton.Visibility = Visibility.Collapsed;
            FillBandBox(band, pick != null && pick.Kind == FollowKind.Indicator ? pick.Source : null, savedPlot);
        }

        // The indicator itself is an EMA. A Keltner or other study that only uses an EMA input is not.
        private static bool IsEma(KcSource src)
        {
            if (src == null || src.Indicator == null) return false;
            NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind = src.Indicator;
            if (IsEmaIdentifier(ind.GetType().Name) || IsEmaIdentifier(ind.Name)) return true;
            return IsEmaIdentifier(SafeDisplayName(ind));
        }

        private static bool IsEmaIdentifier(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string head = text.Trim();
            int paren = head.IndexOf('(');
            if (paren >= 0) head = head.Substring(0, paren).Trim();
            if (head.IndexOf("exponential", StringComparison.OrdinalIgnoreCase) >= 0) return true;

            int i = 0;
            while (i < head.Length)
            {
                while (i < head.Length && !char.IsLetter(head[i])) i++;
                int start = i;
                while (i < head.Length && char.IsLetter(head[i])) i++;
                if (start == i) break;
                string token = head.Substring(start, i - start);
                if (token.Equals("EMA", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("DEMA", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("TEMA", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("ZLEMA", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // The indicator itself is a MACD, including MACDVariation. A study that only takes MACD as an input is not.
        private static bool IsMacd(KcSource src)
        {
            if (src == null || src.IsEmaCross || src.Indicator == null) return false;
            NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind = src.Indicator;
            if (IsMacdIdentifier(ind.GetType().Name) || IsMacdIdentifier(ind.Name)) return true;
            return IsMacdIdentifier(SafeDisplayName(ind));
        }

        private static bool IsMacdIdentifier(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string head = text.Trim();
            int paren = head.IndexOf('(');
            if (paren >= 0) head = head.Substring(0, paren).Trim();
            int i = 0;
            while (i < head.Length)
            {
                while (i < head.Length && !char.IsLetter(head[i])) i++;
                int start = i;
                while (i < head.Length && char.IsLetter(head[i])) i++;
                if (start == i) break;
                string token = head.Substring(start, i - start);
                // MACDVariation and the stock MACD both count. A parameter named MACD does not,
                // because the text after the first "(" was already removed.
                if (token.Equals("MACD", StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith("MACD", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Plot named MACD. Avg is the signal and Diff is the histogram, so neither is the level.
        private static int FindMacdPlot(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind)
        {
            if (ind == null) return -1;
            int plots = ind.Plots == null ? 0 : ind.Plots.Length;
            for (int i = 0; i < plots; i++)
            {
                string pn = ind.Plots[i] != null ? ind.Plots[i].Name : null;
                if (!string.IsNullOrEmpty(pn) && pn.Equals("MACD", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            int n = ind.Values == null ? 0 : ind.Values.Length;
            return (n > 0 || plots > 0) ? 0 : -1;
        }

        private static string FormatMacd(double value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        // Oscillator values are not prices: zero and negatives are valid.
        private bool TryReadOscillator(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind, int plot, int bar, out double value)
        {
            value = double.NaN;
            if (ind == null || plot < 0 || bar < 0) return false;
            try
            {
                if (ind.Values == null || plot >= ind.Values.Length) return false;
                Series<double> ser = ind.Values[plot];
                if (bar >= ser.Count || !ser.IsValidDataPointAt(bar)) return false;
                value = ser.GetValueAt(bar);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch { return false; }
        }

        private int ClosedBar()
        {
            return CurrentBar > 0 ? CurrentBar - 1 : -1;
        }

        // +1 above the level, -1 below, 0 equal.
        private bool TryMacdSign(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind, double level, int bar, out int sign)
        {
            sign = 0;
            double value;
            int plot = FindMacdPlot(ind);
            if (!TryReadOscillator(ind, plot, bar, out value)) return false;
            sign = value.ApproxCompare(level);
            return true;
        }

        // Long stop and short target are hit from above. Long target and short stop are hit from below.
        private static int MacdHitSign(bool isLong, bool isStop)
        {
            bool below = (isLong && isStop) || (!isLong && !isStop);
            return below ? -1 : 1;
        }

        // Closed bars only, oldest first. At most 78 values.
        private bool ReadMacdHistory(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind, out List<double> vals)
        {
            vals = null;
            int plot = FindMacdPlot(ind);
            int end = ClosedBar();
            if (plot < 0 || end < 0) return false;
            List<double> list = new List<double>();
            for (int b = end; b >= 0 && list.Count < 78; b--)
            {
                double v;
                if (!TryReadOscillator(ind, plot, b, out v)) break;
                list.Add(v);
            }
            if (list.Count == 0) return false;
            list.Reverse();
            vals = list;
            return true;
        }

        // Track entry. Rising line: nearest swing high above it. Falling line: nearest swing low below it.
        // Then zero when zero is on that side, then half the lookback range.
        private bool TrySuggestMacdLevel(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind,
                                          out double level, out string reason)
        {
            level = 0;
            reason = null;
            List<double> vals;
            if (!ReadMacdHistory(ind, out vals)) return false;

            int last = vals.Count - 1;
            double current = vals[last];
            bool wantAbove = last >= 1 && current >= vals[last - 1];
            if (TryNearestSwing(vals, current, wantAbove, false, out level))
            {
                reason = wantAbove
                    ? "Nearest MACD swing high above the line"
                    : "Nearest MACD swing low below the line";
                return true;
            }

            int zeroSide = 0d.ApproxCompare(current);
            if ((wantAbove && zeroSide > 0) || (!wantAbove && zeroSide < 0))
            {
                level = 0;
                reason = "Zero line";
                return true;
            }

            double range = MacdRange(vals);
            if (range > 0)
            {
                level = wantAbove ? current + range / 2.0 : current - range / 2.0;
                reason = "Half the recent MACD range";
                return true;
            }

            level = current;
            reason = "Current MACD";
            return true;
        }

        // Filled stop or target. The side of zero decides whether zero itself is the degradation.
        private bool TrySuggestLegMacd(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind, bool isLong, bool isStop,
                                       out double level, out string reason)
        {
            level = 0;
            reason = null;
            List<double> vals;
            if (!ReadMacdHistory(ind, out vals)) return false;
            double current = vals[vals.Count - 1];
            int vsZero = current.ApproxCompare(0);

            bool wantHigh;
            bool betweenZero;
            bool fallbackZero;
            if (vsZero == 0)
            {
                bool lowSide = (isLong && isStop) || (!isLong && !isStop);
                wantHigh = !lowSide;
                betweenZero = false;
                fallbackZero = false;
            }
            else if (isLong && vsZero > 0)
            {
                wantHigh = !isStop;
                betweenZero = isStop;
                fallbackZero = isStop;
            }
            else if (isLong)
            {
                wantHigh = !isStop;
                betweenZero = !isStop;
                fallbackZero = !isStop;
            }
            else if (vsZero < 0)
            {
                wantHigh = isStop;
                betweenZero = isStop;
                fallbackZero = isStop;
            }
            else
            {
                wantHigh = isStop;
                betweenZero = !isStop;
                fallbackZero = !isStop;
            }

            if (TryNearestSwing(vals, current, wantHigh, betweenZero, out level))
            {
                reason = wantHigh
                    ? "Nearest MACD swing high above the line"
                    : "Nearest MACD swing low below the line";
                return true;
            }
            if (fallbackZero)
            {
                level = 0;
                reason = "Zero line";
                return true;
            }
            double range = MacdRange(vals);
            if (range <= 0) return false;
            level = wantHigh ? current + range / 2.0 : current - range / 2.0;
            reason = "Half the recent MACD range";
            return true;
        }

        private static double MacdRange(List<double> vals)
        {
            double hi = vals[0], lo = vals[0];
            for (int i = 1; i < vals.Count; i++)
            {
                if (vals[i] > hi) hi = vals[i];
                if (vals[i] < lo) lo = vals[i];
            }
            return hi - lo;
        }

        // betweenZero keeps only swings that sit between the line and zero, including zero.
        private static bool TryNearestSwing(List<double> vals, double current, bool wantHigh, bool betweenZero, out double level)
        {
            level = 0;
            int last = vals.Count - 1;
            double best = 0;
            int bestBar = -1;
            bool found = false;
            for (int i = 2; i <= last - 2; i++)
            {
                double v = vals[i];
                bool high = v > vals[i - 1] && v > vals[i - 2] && v > vals[i + 1] && v > vals[i + 2];
                bool low = v < vals[i - 1] && v < vals[i - 2] && v < vals[i + 1] && v < vals[i + 2];
                if (wantHigh && !high) continue;
                if (!wantHigh && !low) continue;
                if (wantHigh ? v.ApproxCompare(current) <= 0 : v.ApproxCompare(current) >= 0) continue;
                if (betweenZero)
                {
                    if (wantHigh && v.ApproxCompare(0) > 0) continue;
                    if (!wantHigh && v.ApproxCompare(0) < 0) continue;
                }
                double dist = Math.Abs(v - current);
                if (!found || dist < Math.Abs(best - current) || (dist == Math.Abs(best - current) && i > bestBar))
                {
                    found = true;
                    best = v;
                    bestBar = i;
                }
            }
            if (!found) return false;
            level = best;
            return true;
        }

        private bool ShowMacdLevelDialog(Window owner, string title, string intro, string prefill, string suggestion, out double level)
        {
            level = 0;
            bool accepted = false;
            double chosen = 0;
            Window win = null;
            try
            {
            win = new Window
            {
                Title = title,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Height,
                Width = 360,
                MaxWidth = 360,
                Background = Brushes.White,
                ShowInTaskbar = false
            };
            if (owner != null) win.Owner = owner;

            System.Windows.Controls.StackPanel root = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = intro,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 336,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)),
                Margin = new Thickness(0, 0, 0, 8)
            });

            System.Windows.Controls.TextBox box = new System.Windows.Controls.TextBox
            {
                Text = prefill ?? "",
                MinWidth = 220,
                Margin = new Thickness(0, 0, 0, 6)
            };
            root.Children.Add(box);
            root.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = suggestion ?? "",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x42, 0x50)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4)
            });

            System.Windows.Controls.TextBlock errorText = new System.Windows.Controls.TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0x24, 0x24)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12
            };
            root.Children.Add(errorText);

            System.Windows.Controls.StackPanel buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            System.Windows.Controls.Button confirm = new System.Windows.Controls.Button
            {
                Content = "Confirm", Width = 88, Height = 26, Margin = new Thickness(0, 0, 8, 0), IsDefault = true
            };
            System.Windows.Controls.Button cancel = new System.Windows.Controls.Button
            {
                Content = "Cancel", Width = 88, Height = 26, IsCancel = true
            };
            confirm.Click += delegate
            {
                string text = (box.Text ?? "").Trim();
                double parsed;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                    && !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed))
                {
                    errorText.Text = "Enter a MACD level.";
                    return;
                }
                if (double.IsNaN(parsed) || double.IsInfinity(parsed))
                {
                    errorText.Text = "Enter a MACD level.";
                    return;
                }
                chosen = parsed;
                accepted = true;
                win.DialogResult = true;
            };
            buttons.Children.Add(confirm);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            win.Content = root;
            bool rendered = false;
            win.ContentRendered += delegate { rendered = true; };
            win.Loaded += delegate
            {
                win.Activate();
                box.Focus();
                box.SelectAll();
            };
            Mouse.Capture(null);
            win.ShowDialog();
            // The Track popup can close the first window before it paints. One more open stays up.
            if (!rendered && !accepted)
            {
                Mouse.Capture(null);
                win.Content = null;
                Window again = new Window
                {
                    Title = title,
                    WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                    ResizeMode = ResizeMode.NoResize,
                    SizeToContent = SizeToContent.Height,
                    Width = 360,
                    MaxWidth = 360,
                    Background = Brushes.White,
                    ShowInTaskbar = false,
                    Content = root
                };
                if (owner != null) again.Owner = owner;
                again.Loaded += delegate
                {
                    again.Activate();
                    box.Focus();
                    box.SelectAll();
                };
                win = again;
                win.ShowDialog();
            }
            }
            catch (Exception ex)
            {
                Print("KcMidTrader MACD window error: " + ex.Message);
                SetStatusNow("MACD window could not open. " + ex.Message);
                return false;
            }
            if (!accepted) return false;
            level = chosen;
            return true;
        }

        private bool PromptTrackMacd(KcSource pick, out double level)
        {
            level = 0;
            if (pick == null || pick.Indicator == null) return false;
            double suggested = 0;
            string reason = null;
            bool got = false;
            try { got = TrySuggestMacdLevel(pick.Indicator, out suggested, out reason); }
            catch (Exception ex)
            {
                Print("KcMidTrader MACD suggestion error: " + ex.Message);
                got = false;
            }
            string prefill = macdConfigured && !double.IsNaN(macdLevel)
                ? FormatMacd(macdLevel)
                : (got ? FormatMacd(suggested) : "");
            string line = got
                ? "Suggested: " + FormatMacd(suggested) + ". " + reason + "."
                : "MACD has no closed value yet. Type a level.";
            return ShowMacdLevelDialog(
                chartWindow,
                "MACD level",
                "The order price is the Keltner midline.\n"
                + "Buy is sent when the MACD line crosses above this level.\n"
                + "Sell is sent when it crosses below.\n"
                + "A cross that is already true does not send the order.\n"
                + "A buy above the market and a sell below it wait for the same cross, then follow the midline.",
                prefill, line, out level);
        }

        private static void FillBandBox(System.Windows.Controls.ComboBox band, KcSource src, int prefer)
        {
            band.Items.Clear();
            if (src == null)
            {
                band.Visibility = Visibility.Collapsed;
                return;
            }
            List<int> plots = new List<int>();
            List<string> names = new List<string>();
            CollectPlots(src, plots, names);
            if (names.Count == 0)
            {
                band.Visibility = Visibility.Collapsed;
                return;
            }
            int sel = 0;
            for (int i = 0; i < names.Count; i++)
            {
                band.Items.Add(new BandPick { Text = names[i], Plot = plots[i] });
                if (plots[i] == prefer) sel = i;
            }
            band.SelectedIndex = sel;
            band.Visibility = names.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private string ApplyLegDialog(TrackedOrder t, List<LegDialogRow> rows)
        {
            if (t == null || t.Completed) return "That order is already closed.";
            LegDialogRow stopRow = null;
            LegDialogRow targetRow = null;
            foreach (LegDialogRow row in rows)
            {
                if (row.IsStop) stopRow = row;
                else targetRow = row;
            }
            if (stopRow == null || targetRow == null) return "Choose a stop and a target.";

            FollowPick stopPick = stopRow.SourceBox.SelectedItem as FollowPick;
            FollowPick targetPick = targetRow.SourceBox.SelectedItem as FollowPick;
            if (stopPick == null || targetPick == null) return "Choose a stop and a target.";
            if (stopPick.Kind == FollowKind.EmaCross) return "EMA cross applies to the target.";

            string error = ValidateLegChoice(t, stopRow, stopPick);
            if (error != null) return error;
            error = ValidateLegChoice(t, targetRow, targetPick);
            if (error != null) return error;

            bool stopMacd = IsMacdFollow(stopPick);
            bool targetMacd = IsMacdFollow(targetPick);
            bool cancelStop = stopPick.Kind == FollowKind.None || stopMacd;
            bool cancelTarget = targetPick.Kind == FollowKind.None || targetPick.Kind == FollowKind.EmaCross || targetMacd;
            AttachWorkingLegs(t, true);
            AttachWorkingLegs(t, false);
            lock (sync)
            {
                if (cancelStop) t.Relink.RemoveAll(h => h.IsStop);
                if (cancelTarget) t.Relink.RemoveAll(h => !h.IsStop);
            }
            if (cancelStop && !cancelTarget) ArmRelink(t, false);
            if (cancelTarget && !cancelStop) ArmRelink(t, true);

            LegFollow prevStop, prevTarget;
            lock (sync)
            {
                prevStop = t.StopSource;
                prevTarget = t.TargetSource;
            }
            // Mark the leg removed before the cancel so an ATM replacement is not adopted.
            if (cancelStop) SetRejectSource(t, true);
            if (cancelTarget) SetRejectSource(t, false);

            int stopCount = 0;
            int targetCount = 0;
            if (cancelStop)
            {
                string err;
                stopCount = CancelResolvedLegs(t, true, out err);
                if (err != null)
                {
                    RestoreLegSource(t, true, prevStop);
                    if (cancelTarget) RestoreLegSource(t, false, prevTarget);
                    return err;
                }
            }
            if (cancelTarget)
            {
                string err;
                targetCount = CancelResolvedLegs(t, false, out err);
                if (err != null)
                {
                    RestoreLegSource(t, false, prevTarget);
                    if (cancelStop) SetLegNone(t, true);
                    return err;
                }
            }

            List<string> parts = new List<string>();
            AddRemovedStatus(parts,
                stopPick.Kind == FollowKind.None,
                targetPick.Kind == FollowKind.None,
                stopCount, targetCount);

            error = ApplyKeptLeg(t, stopRow, stopPick, parts);
            if (error != null) return error;
            error = ApplyKeptLeg(t, targetRow, targetPick, parts);
            if (error != null) return error;
            if (parts.Count > 0) SetStatusNow(string.Join(" ", parts));
            return null;
        }

        private static void AddRemovedStatus(List<string> parts, bool stopNone, bool targetNone, int stops, int targets)
        {
            if (!stopNone && !targetNone) return;
            if (stopNone && targetNone)
            {
                if (stops > 0 && targets > 0)
                    parts.Add("Stop and target removed. The position stays open.");
                else if (stops > 0)
                    parts.Add("Stop removed. No working target was found. The position stays open.");
                else if (targets > 0)
                    parts.Add("No working stop was found. Target removed. The position stays open.");
                else
                    parts.Add("No working stop or target was found. The position stays open.");
                return;
            }
            if (stopNone)
                parts.Add(stops > 0
                    ? "Stop removed. The position stays open."
                    : "No working stop was found. The position stays open.");
            if (targetNone)
                parts.Add(targets > 0
                    ? "Target removed. The position stays open."
                    : "No working target was found. The position stays open.");
        }

        private string ValidateLegChoice(TrackedOrder t, LegDialogRow row, FollowPick pick)
        {
            if (pick.Kind == FollowKind.None) return null;
            if (pick.Kind == FollowKind.EmaCross)
            {
                FollowPick a = row.EmaABox == null ? null : row.EmaABox.SelectedItem as FollowPick;
                FollowPick b = row.EmaBBox == null ? null : row.EmaBBox.SelectedItem as FollowPick;
                if (a == null || b == null || a.Source == null || b.Source == null || a.Source.Indicator == null || b.Source.Indicator == null)
                    return "Choose the two EMAs.";
                if (ReferenceEquals(a.Source.Indicator, b.Source.Indicator))
                    return "Choose two different EMAs.";
                return null;
            }
            if (pick.Kind == FollowKind.Indicator && IsMacd(pick.Source))
            {
                if (!row.MacdSet) return "Enter a MACD level for the " + (row.IsStop ? "stop" : "target") + ".";
                return null;
            }
            if (pick.Kind == FollowKind.Indicator)
            {
                if (pick.Source == null || (row.BandBox.SelectedItem as BandPick) == null)
                    return "Choose a band for " + pick.Text + ".";
                bool working;
                lock (sync) working = HasWorkingChild(row.IsStop ? t.StopOrders : t.TargetOrders);
                if (!working)
                {
                    string placed = PreviewProtective(t, row.IsStop, pick.Source, (row.BandBox.SelectedItem as BandPick).Plot);
                    if (placed != null) return placed;
                }
                return null;
            }

            NinjaTrader.NinjaScript.AtmStrategy selected = SelectedAtm();
            string selectedKey = selected == null ? null : AtmTemplateKey(selected);
            if (selected == null || string.IsNullOrEmpty(selectedKey))
                return "Select an ATM strategy in Chart Trader to use a fixed stop or target.";
            bool live;
            lock (sync) live = HasWorkingChild(row.IsStop ? t.StopOrders : t.TargetOrders);
            if (!live)
            {
                string placed = PreviewAtmLeg(t, row.IsStop, selected);
                if (placed != null) return placed;
            }
            return null;
        }

        private string ApplyKeptLeg(TrackedOrder t, LegDialogRow row, FollowPick pick, List<string> parts)
        {
            string which = row.IsStop ? "Stop" : "Target";
            if (pick.Kind == FollowKind.None)
            {
                SetLegNone(t, row.IsStop);
                return null;
            }
            if (pick.Kind == FollowKind.EmaCross)
            {
                FollowPick a = row.EmaABox.SelectedItem as FollowPick;
                FollowPick b = row.EmaBBox.SelectedItem as FollowPick;
                lock (sync)
                {
                    t.TargetSource = LegFollow.EmaCross;
                    t.TargetIndicator = null;
                    t.TargetEmaA = a.Source.Indicator;
                    t.TargetEmaB = b.Source.Indicator;
                    t.TargetEmaSign = 0;
                    t.TargetCrossSent = false;
                    t.TargetCrossPending = false;
                    t.TargetPaused = false;
                    t.TargetMissing = false;
                    t.TargetMacdSet = false;
                }
                parts.Add("Target will close when " + a.Text + " crosses " + b.Text + ". The position stays open until then.");
                return null;
            }
            if (IsMacdFollow(pick))
            {
                if (!row.MacdSet || pick.Source == null || pick.Source.Indicator == null)
                    return "Enter a MACD level for the " + which.ToLower() + ".";
                bool isLong = IsBuyAction(t.Action);
                int hit = MacdHitSign(isLong, row.IsStop);
                int sign = 0;
                int bar = ClosedBar();
                bool known = bar >= 0 && TryMacdSign(pick.Source.Indicator, row.MacdLevel, bar, out sign);
                int stored = int.MinValue;
                if (known)
                {
                    bool already = hit < 0 ? sign <= 0 : sign >= 0;
                    stored = already ? hit : sign;
                }
                lock (sync) { AssignMacdLeg(t, row.IsStop, pick.Source.Indicator, row.MacdLevel, stored); }
                parts.Add(which + " will close when MACD crosses " + FormatMacd(row.MacdLevel) + ". The position stays open until then.");
                return null;
            }
            if (pick.Kind == FollowKind.Atm)
            {
                bool working;
                lock (sync) working = HasWorkingChild(row.IsStop ? t.StopOrders : t.TargetOrders);
                if (working)
                {
                    NinjaTrader.NinjaScript.AtmStrategy atm = SelectedAtm();
                    string key = atm == null ? null : AtmTemplateKey(atm);
                    if (atm == null || string.IsNullOrEmpty(key))
                        return "Select an ATM strategy in Chart Trader to use a fixed stop or target.";
                    List<string> notes = new List<string>();
                    RepriceProtective(t, atm, notes, row.IsStop, !row.IsStop);
                    if (notes.Count > 0) return notes[0];
                    lock (sync)
                    {
                        AssignAtmLeg(t, row.IsStop, key, atm);
                    }
                    parts.Add(which + " left on the ATM strategy.");
                    return null;
                }
                string error = PlaceAtmLeg(t, row.IsStop);
                if (error != null) return error;
                parts.Add(which + " placed from the ATM strategy.");
                return null;
            }

            BandPick band = row.BandBox.SelectedItem as BandPick;
            if (band == null || pick.Source == null) return "Choose a band for " + pick.Text + ".";
            bool live;
            lock (sync) live = HasWorkingChild(row.IsStop ? t.StopOrders : t.TargetOrders);
            if (!live)
            {
                string error = PlaceIndicatorLeg(t, row.IsStop, pick.Source, band.Plot);
                if (error != null) return error;
            }
            lock (sync) { AssignIndicatorLeg(t, row.IsStop, pick.Source.Indicator, band.Plot); }
            if (live)
            {
                string note = FollowLeg(t, row.IsStop, CurrentBar);
                if (!string.IsNullOrEmpty(note)) parts.Add(note);
                else
                {
                    string bandName = row.BandBox.Visibility == Visibility.Visible ? (" " + band.Text) : "";
                    parts.Add(which + " follows " + pick.Text + bandName + ".");
                }
            }
            else
            {
                string bandName = row.BandBox.Visibility == Visibility.Visible ? (" " + band.Text) : "";
                parts.Add(which + " placed on " + pick.Text + bandName + ".");
            }
            return null;
        }

        private void AssignAtmLeg(TrackedOrder t, bool isStop, string key, NinjaTrader.NinjaScript.AtmStrategy atm)
        {
                if (isStop)
                {
                    t.StopSource = LegFollow.Atm;
                    t.StopIndicator = null;
                    t.StopPaused = false;
                    t.StopMissing = false;
                    t.StopMacdSet = false;
                }
                else
                {
                    t.TargetSource = LegFollow.Atm;
                    t.TargetIndicator = null;
                    t.TargetEmaA = null;
                    t.TargetEmaB = null;
                    t.TargetEmaSign = 0;
                    t.TargetCrossSent = false;
                    t.TargetCrossPending = false;
                    t.TargetPaused = false;
                    t.TargetMissing = false;
                    t.TargetMacdSet = false;
                }
            t.AtmName = key;
            t.AtmTemplate = atm;
        }

        private void AssignIndicatorLeg(TrackedOrder t, bool isStop, NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind, int plot)
        {
            if (isStop)
            {
                t.StopSource = LegFollow.Indicator;
                t.StopIndicator = ind;
                t.StopPlot = plot;
                t.StopPaused = false;
                t.StopMissing = false;
                t.StopMacdSet = false;
            }
            else
            {
                t.TargetSource = LegFollow.Indicator;
                t.TargetIndicator = ind;
                t.TargetPlot = plot;
                t.TargetEmaA = null;
                t.TargetEmaB = null;
                t.TargetEmaSign = 0;
                t.TargetCrossSent = false;
                t.TargetCrossPending = false;
                t.TargetPaused = false;
                t.TargetMissing = false;
                t.TargetMacdSet = false;
            }
        }

        private static bool IsMacdFollow(FollowPick pick)
        {
            return pick != null && pick.Kind == FollowKind.Indicator && IsMacd(pick.Source);
        }

        private static void AssignMacdLeg(TrackedOrder t, bool isStop, NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind,
                                          double level, int sign)
        {
            int plot = FindMacdPlot(ind);
            if (isStop)
            {
                t.StopSource = LegFollow.Macd;
                t.StopIndicator = ind;
                t.StopPlot = plot;
                t.StopMacdSet = true;
                t.StopMacdLevel = level;
                t.StopMacdSign = sign;
                t.StopPaused = false;
                t.StopMissing = false;
            }
            else
            {
                t.TargetSource = LegFollow.Macd;
                t.TargetIndicator = ind;
                t.TargetPlot = plot;
                t.TargetMacdSet = true;
                t.TargetMacdLevel = level;
                t.TargetMacdSign = sign;
                t.TargetEmaA = null;
                t.TargetEmaB = null;
                t.TargetEmaSign = 0;
                t.TargetCrossSent = false;
                t.TargetCrossPending = false;
                t.TargetPaused = false;
                t.TargetMissing = false;
            }
            t.MacdCloseSent = false;
            t.MacdClosePending = false;
        }

        private void SetRejectSource(TrackedOrder t, bool isStop)
        {
            lock (sync)
            {
                if (isStop) t.StopSource = LegFollow.None;
                else t.TargetSource = LegFollow.None;
            }
        }

        private void RestoreLegSource(TrackedOrder t, bool isStop, LegFollow source)
        {
            lock (sync)
            {
                if (isStop) t.StopSource = source;
                else t.TargetSource = source;
            }
        }

        private void SetLegNone(TrackedOrder t, bool isStop)
        {
            lock (sync)
            {
                if (isStop)
                {
                    t.StopSource = LegFollow.None;
                    t.StopIndicator = null;
                    t.StopPaused = false;
                    t.StopMissing = false;
                    t.StopMacdSet = false;
                    t.Relink.RemoveAll(h => h.IsStop);
                }
                else
                {
                    t.TargetSource = LegFollow.None;
                    t.TargetIndicator = null;
                    t.TargetEmaA = null;
                    t.TargetEmaB = null;
                    t.TargetEmaSign = 0;
                    t.TargetCrossSent = false;
                    t.TargetCrossPending = false;
                    t.TargetPaused = false;
                    t.TargetMissing = false;
                    t.TargetMacdSet = false;
                    t.Relink.RemoveAll(h => !h.IsStop);
                }
            }
        }

        private string PreviewAtmLeg(TrackedOrder t, bool isStop, NinjaTrader.NinjaScript.AtmStrategy atm)
        {
            double fill, price;
            int qty;
            string error;
            if (!TryAtmProtectivePrice(t, isStop, atm, out fill, out qty, out price, out error))
                return error;
            if (fill <= 0 || qty <= 0 || price <= 0) return "ATM price was not usable.";
            return null;
        }

        private string PlaceAtmLeg(TrackedOrder t, bool isStop)
        {
            NinjaTrader.NinjaScript.AtmStrategy atm = SelectedAtm();
            string key = atm == null ? null : AtmTemplateKey(atm);
            if (atm == null || string.IsNullOrEmpty(key))
                return "Select an ATM strategy in Chart Trader to use a fixed stop or target.";
            double fill, price;
            int qty;
            string error;
            if (!TryAtmProtectivePrice(t, isStop, atm, out fill, out qty, out price, out error))
                return error;
            error = PlaceProtective(t, isStop, price, qty);
            if (error != null) return error;
            lock (sync) { AssignAtmLeg(t, isStop, key, atm); }
            return null;
        }

        private bool TryAtmProtectivePrice(TrackedOrder t, bool isStop, NinjaTrader.NinjaScript.AtmStrategy atm,
                                            out double fill, out int qty, out double price, out string error)
        {
            fill = 0;
            qty = 0;
            price = 0;
            error = null;
            lock (sync)
            {
                fill = t.FillPrice > 0 ? t.FillPrice : (t.Order != null ? t.Order.AverageFillPrice : 0);
                qty = OpenQuantity(t);
            }
            if (fill <= 0) { error = "That order has no fill price yet."; return false; }
            if (qty <= 0) { error = "That order has no quantity left open."; return false; }
            double stopVal, targetVal;
            CalculationMode mode;
            if (!TryReadBracket(atm, 0, out stopVal, out targetVal, out mode, out error))
            {
                if (string.IsNullOrEmpty(error)) error = "Could not read the ATM template.";
                return false;
            }
            double value = isStop ? stopVal : targetVal;
            if (!isStop && value.ApproxCompare(0) == 0)
            {
                error = "The ATM target is a runner, so no target was placed.";
                return false;
            }
            if (!TryProtectivePrice(IsBuyAction(t.Action), isStop, fill, value, mode, out price, out error))
            {
                if (string.IsNullOrEmpty(error)) error = "ATM price was not usable.";
                return false;
            }
            return SideError(t, isStop, price, out error);
        }

        private string PreviewProtective(TrackedOrder t, bool isStop, KcSource src, int plot)
        {
            double price;
            int qty;
            string error;
            if (!TryPlotPrice(t, isStop, src, plot, out price, out qty, out error))
                return error;
            if (qty <= 0 || price <= 0) return "That indicator has no usable price.";
            return null;
        }

        private string PlaceIndicatorLeg(TrackedOrder t, bool isStop, KcSource src, int plot)
        {
            double price;
            int qty;
            string error;
            if (!TryPlotPrice(t, isStop, src, plot, out price, out qty, out error))
                return error;
            return PlaceProtective(t, isStop, price, qty);
        }

        private bool TryPlotPrice(TrackedOrder t, bool isStop, KcSource src, int plot, out double price, out int qty, out string error)
        {
            price = 0;
            qty = 0;
            error = null;
            double raw;
            string which = isStop ? "Stop" : "Target";
            if (src == null || src.Indicator == null || !TryReadSeries(src.Indicator, plot, CurrentBar, out raw))
            {
                error = which + " was not placed: its indicator has no value.";
                return false;
            }
            price = Instrument.MasterInstrument.RoundToTickSize(raw);
            lock (sync) qty = OpenQuantity(t);
            if (qty <= 0) { error = "That order has no quantity left open."; return false; }
            return SideError(t, isStop, price, out error);
        }

        private bool SideError(TrackedOrder t, bool isStop, double price, out string error)
        {
            error = null;
            if (ProtectiveOnRestingSide(IsBuyAction(t.Action), isStop, price)) return true;
            error = (isStop ? "Stop" : "Target") + " was not placed: "
                + Instrument.MasterInstrument.FormatPrice(price) + " is on the wrong side of the market.";
            return false;
        }

        private void ArmRelink(TrackedOrder t, bool stops)
        {
            lock (sync)
            {
                t.Relink.RemoveAll(h => h.IsStop == stops && !h.Replaced);
                List<Order> orders = stops ? t.StopOrders : t.TargetOrders;
                foreach (Order o in orders)
                {
                    if (!IsChangeable(o)) continue;
                    int qty = o.Quantity - o.Filled;
                    if (qty <= 0) continue;
                    double price = o.OrderType == OrderType.Limit ? o.LimitPrice : o.StopPrice;
                    if (price <= 0) continue;
                    t.Relink.Add(new HeldLeg { IsStop = stops, Original = o, Price = price, Qty = qty });
                }
            }
        }

        private bool CancelWorkingLegs(TrackedOrder t, bool stops)
        {
            string error;
            CancelResolvedLegs(t, stops, out error);
            return error == null;
        }

        // Working orders already stored on the leg, plus ATM orders on this entry that were never linked.
        // The filled entry is never included. Orders owned by another tracked trade are left alone.
        private int CancelResolvedLegs(TrackedOrder t, bool stops, out string error)
        {
            error = null;
            if (t == null) return 0;
            AttachWorkingLegs(t, stops);
            List<Order> orders = new List<Order>();
            lock (sync)
            {
                foreach (Order o in (stops ? t.StopOrders : t.TargetOrders))
                {
                    if (o == null || o == t.Order || !IsChangeable(o) || ContainsOrder(orders, o)) continue;
                    orders.Add(o);
                }
            }
            if (orders.Count == 0) return 0;
            if (t.Account == null)
            {
                error = CouldNotCancel(stops);
                return 0;
            }
            try
            {
                t.Account.Cancel(orders.ToArray());
                return orders.Count;
            }
            catch (Exception ex)
            {
                Print("KcMidTrader leg cancel error: " + ex.Message);
                error = CouldNotCancel(stops);
                return 0;
            }
        }

        private static string CouldNotCancel(bool stops)
        {
            return "Could not cancel the " + (stops ? "stop" : "target") + ".";
        }

        private void AttachWorkingLegs(TrackedOrder t, bool stops)
        {
            if (t == null || t.Account == null) return;
            NinjaTrader.NinjaScript.StrategyBase owner;
            lock (sync)
            {
                RefreshAtmOwner(t);
                owner = t.AtmOwner;
            }
            if (owner == null) return;

            Order[] live;
            try { lock (t.Account.Orders) live = t.Account.Orders.ToArray(); }
            catch { return; }

            foreach (Order o in live)
            {
                if (!BelongsToEntryLeg(t, o, stops, owner)) continue;
                lock (sync)
                {
                    if (OwnedByOther(t, o)) continue;
                    List<Order> stored = stops ? t.StopOrders : t.TargetOrders;
                    if (!ContainsOrder(stored, o)) stored.Add(o);
                }
            }
        }

        private bool BelongsToEntryLeg(TrackedOrder t, Order o, bool stops, NinjaTrader.NinjaScript.StrategyBase owner)
        {
            if (o == null || t == null || owner == null || o == t.Order || !IsChangeable(o)) return false;
            if (o.Instrument == null || Instrument == null || o.Instrument.FullName != Instrument.FullName) return false;
            string name = o.Name ?? "";
            if (stops)
            {
                if (!name.StartsWith("Stop", StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (!name.StartsWith("Target", StringComparison.OrdinalIgnoreCase)) return false;

            NinjaTrader.NinjaScript.StrategyBase child = null;
            try { child = o.GetOwnerStrategy(); }
            catch { child = null; }
            return child != null && ReferenceEquals(child, owner);
        }

        private bool OwnedByOther(TrackedOrder t, Order o)
        {
            foreach (TrackedOrder other in tracked)
            {
                if (other == t) continue;
                if (other.Order == o || ContainsOrder(other.StopOrders, o) || ContainsOrder(other.TargetOrders, o))
                    return true;
            }
            return false;
        }

        private string PlaceProtective(TrackedOrder t, bool isStop, double price, int qty)
        {
            if (t == null || t.Account == null || qty <= 0 || price <= 0)
                return "Could not place the protective order.";
            bool isLong = IsBuyAction(t.Action);
            OrderAction action = isLong ? OrderAction.Sell : OrderAction.BuyToCover;
            OrderType type = isStop ? OrderType.StopMarket : OrderType.Limit;
            double lmt = type == OrderType.Limit ? price : 0;
            double stp = type == OrderType.StopMarket ? price : 0;
            try
            {
                Order o = t.Account.CreateOrder(Instrument, action, type, OrderEntry.Manual, OrderTif, qty,
                    lmt, stp, string.Empty, isStop ? "Stop1" : "Target1", NinjaTrader.Core.Globals.MaxDate, null);
                t.Account.Submit(new[] { o });
                lock (sync)
                {
                    if (isStop) t.StopOrders.Add(o);
                    else t.TargetOrders.Add(o);
                }
                HookAccount(t.Account);
                return null;
            }
            catch (Exception ex)
            {
                Print("KcMidTrader protective order error: " + ex.Message);
                return "Could not place the " + (isStop ? "stop" : "target") + ".";
            }
        }

        private void OnProtectiveState(Order o)
        {
            if (o == null) return;
            TrackedOrder owner = null;
            HeldLeg held = null;
            bool finishCross = false;
            bool finishMacd = false;
            lock (sync)
            {
                foreach (TrackedOrder t in tracked)
                {
                    if (t.StopOrders.Contains(o)) { owner = t; break; }
                    if (t.TargetOrders.Contains(o)) { owner = t; break; }
                }
                if (owner == null) return;
                bool isStop = owner.StopOrders.Contains(o);
                if (owner.TargetCrossPending && isStop)
                {
                    if (!HasLiveChild(owner.StopOrders)) finishCross = true;
                }
                else if (owner.MacdClosePending && isStop == owner.MacdCloseCancelsStop)
                {
                    List<Order> side = isStop ? owner.StopOrders : owner.TargetOrders;
                    if (!HasLiveChild(side)) finishMacd = true;
                }
                else
                {
                    foreach (HeldLeg h in owner.Relink)
                    {
                        if (h.Replaced || h.Original != o) continue;
                        h.Replaced = true;
                        held = h;
                        break;
                    }
                }
            }
            if (finishCross) FinishEmaClose(owner);
            else if (finishMacd) FinishMacdClose(owner);
            else if (held != null) RestoreHeld(owner, held, o);
        }

        private void RestoreHeld(TrackedOrder t, HeldLeg held, Order original)
        {
            int qty = original == null ? held.Qty : original.Quantity - original.Filled;
            if (qty <= 0) qty = held.Qty;
            if (qty <= 0 || held.Price <= 0) return;
            string error = PlaceProtective(t, held.IsStop, held.Price, qty);
            if (error != null) SetStatus(error);
            else SetStatus((held.IsStop ? "Stop" : "Target") + " kept after the other leg was removed.");
        }

        private void TrackMacdNow()
        {
            if (State != State.Realtime || !IsFirstTickOfBar) return;
            int bar = CurrentBar - 1;
            if (bar < 0) return;
            ReleaseMacdArms(bar);

            List<TrackedOrder> orders;
            lock (sync) orders = tracked.ToList();
            foreach (TrackedOrder t in orders)
            {
                if (t.Completed || t.Order == null || t.Order.Filled <= 0 || t.MacdCloseSent) continue;
                if (t.StopSource == LegFollow.Macd)
                    ReportFollow(WatchMacdLeg(t, true, bar));
                if (t.MacdCloseSent) continue;
                if (t.TargetSource == LegFollow.Macd)
                    ReportFollow(WatchMacdLeg(t, false, bar));
            }
        }

        private void ArmMacdFromUi(OrderAction action)
        {
            KcSource src = selectedKc;
            if (src == null || !IsMacd(src) || src.Indicator == null)
            {
                SetStatusNow("Select a MACD indicator in Track.");
                return;
            }
            RememberKeltner();
            KcSource price = keltnerPrice;
            if (price == null || price.Indicator == null)
            {
                SetStatusNow("MACD needs a Keltner Channel on this chart. The order price is that midline.");
                return;
            }

            // The level was chosen when MACD was selected in Track. Buy and Sell reuse it.
            double level;
            if (macdConfigured && !double.IsNaN(macdLevel))
                level = macdLevel;
            else
            {
                if (!PromptTrackMacd(src, out level))
                    return;
                macdLevel = level;
                macdConfigured = true;
                shownMacdText = null;
            }

            Account acct = SelectedAccount();
            if (acct == null) { SetStatusNow("Select an account in Chart Trader."); return; }

            RememberAtm();
            int bar = ClosedBar();
            int sign = 0;
            if (bar < 0 || !TryMacdSign(src.Indicator, level, bar, out sign))
            {
                SetStatusNow("MACD has no value yet.");
                return;
            }

            bool isBuy = action == OrderAction.Buy;
            int qty = ReadQty();
            string label = NewOrderLabel();
            string levelText = FormatMacd(level);
            string lineName = string.IsNullOrWhiteSpace(selectedLineName) ? "Midline" : selectedLineName;
            int plot = ActivePlot(price);
            lock (sync)
            {
                TrackedOrder t = new TrackedOrder();
                t.Account = acct;
                t.Action = action;
                t.Quantity = qty;
                t.Label = label;
                t.Track = trackAll;
                t.FollowThrough = true;
                t.WaitMacd = true;
                t.EntryMacd = src.Indicator;
                t.EntryMacdLevel = level;
                t.EntryMacdSign = sign;
                t.EntryMacdBar = bar;
                t.EntryPriceInd = price.Indicator;
                t.EntryPricePlot = plot;
                t.StopSource = LegFollow.Atm;
                tracked.Add(t);
            }

            string wait = isBuy ? "MACD to cross above " + levelText : "MACD to cross below " + levelText;
            SetStatusNow((isBuy ? "Buy" : "Sell") + " armed at the Keltner " + lineName + ". Waiting for " + wait + ".");
        }

        private void ReleaseMacdArms(int bar)
        {
            List<TrackedOrder> arms;
            lock (sync) arms = tracked.Where(x => x.WaitMacd && x.Order == null).ToList();
            foreach (TrackedOrder t in arms)
            {
                if (bar <= t.EntryMacdBar) continue;
                int sign = 0;
                if (t.EntryMacd == null || !TryMacdSign(t.EntryMacd, t.EntryMacdLevel, bar, out sign))
                {
                    if (!t.EntryMacdNoted)
                    {
                        t.EntryMacdNoted = true;
                        SetStatus("MACD paused: the indicator has no value.");
                    }
                    continue;
                }
                t.EntryMacdNoted = false;
                if (sign == 0 || sign == t.EntryMacdSign) continue;
                int wanted = IsBuyAction(t.Action) ? 1 : -1;
                if (sign != wanted)
                {
                    lock (sync) { t.EntryMacdSign = sign; t.EntryMacdBar = bar; }
                    continue;
                }
                string msg = SubmitArmedMacd(t, bar, sign);
                if (!string.IsNullOrEmpty(msg)) SetStatus(msg);
            }
        }

        private string SubmitArmedMacd(TrackedOrder t, int bar, int sign)
        {
            if (t == null || t.Account == null || t.Quantity <= 0) return "Could not place the MACD entry.";
            bool isBuy = IsBuyAction(t.Action);

            int priceBar = CurrentBar;
            if (TrackMode == NinjaTrader.NinjaScript.KcMidTraderTypes.KcTrackMode.OnBarClose)
                priceBar = CurrentBar - 1;
            if (priceBar < 0) priceBar = bar;

            NinjaTrader.Gui.NinjaScript.IndicatorRenderBase priceInd = t.EntryPriceInd;
            int pricePlot = t.EntryPricePlot;
            KcSource livePrice = keltnerPrice;
            if (livePrice != null && livePrice.Indicator != null
                && (priceInd == null || ReferenceEquals(livePrice.Indicator, priceInd)))
            {
                priceInd = livePrice.Indicator;
                pricePlot = ActivePlot(livePrice);
            }

            double raw;
            if (priceInd == null || !TryReadSeries(priceInd, pricePlot, priceBar, out raw))
            {
                if (!t.EntryMacdNoted)
                {
                    t.EntryMacdNoted = true;
                    return (t.Label ?? "Order") + "  waiting: the Keltner line has no price yet.";
                }
                return null;
            }
            t.EntryMacdNoted = false;

            double mid = Instrument.MasterInstrument.RoundToTickSize(raw);
            double price = OffsetPrice(isBuy, mid);
            double refPx = RefPrice(isBuy);
            if (double.IsNaN(refPx))
                return null;

            // Same price rules as a Keltner order. The stop side is sent too: the MACD cross is the trigger,
            // and FollowThrough keeps that order on the line afterwards.
            OrderType? type = DesiredType(isBuy, price, refPx);
            if (type == null)
                return null;

            double lmt = type.Value == OrderType.Limit ? price : 0;
            double stp = type.Value == OrderType.StopMarket ? price : 0;

            string chosenAtm = "";
            NinjaTrader.NinjaScript.AtmStrategy atm = null;
            if (UseChartTraderAtm)
            {
                chosenAtm = liveAtmKey ?? "";
                atm = chosenAtm.Length > 0 ? liveAtm : null;
            }

            OrderAction action = isBuy ? OrderAction.Buy : OrderAction.Sell;
            string orderName = chosenAtm.Length > 0 ? "Entry" : OrderNamePrefix + (isBuy ? "Buy" : "Sell");
            Order o;
            try
            {
                o = t.Account.CreateOrder(Instrument, action, type.Value, OrderEntry.Manual, OrderTif, t.Quantity,
                    lmt, stp, string.Empty, orderName, NinjaTrader.Core.Globals.MaxDate, null);
            }
            catch (Exception ex)
            {
                Print("KcMidTrader MACD entry error: " + ex.Message);
                return (t.Label ?? "Order") + "  Error: " + ex.Message;
            }

            HookAccount(t.Account);
            lock (sync)
            {
                t.Order = o;
                t.FollowThrough = true;
                t.AtmTemplate = atm;
                t.AtmName = chosenAtm;
                t.FollowAtm = chosenAtm.Length > 0;
                t.StopSource = LegFollow.Atm;
                t.TargetSource = LegFollow.Atm;
            }

            try
            {
                if (chosenAtm.Length > 0)
                {
                    System.Threading.Interlocked.Increment(ref suppressAtmEvents);
                    try { NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(chosenAtm, o); }
                    finally { System.Threading.Interlocked.Decrement(ref suppressAtmEvents); }
                }
                else
                    t.Account.Submit(new[] { o });
            }
            catch (Exception ex)
            {
                Print("KcMidTrader MACD entry error: " + ex.Message);
                lock (sync)
                {
                    bool filled = o != null && o.Filled > 0;
                    if (!filled)
                    {
                        t.Order = null;
                        t.WaitMacd = true;
                    }
                    else
                    {
                        t.WaitMacd = false;
                        t.EntryMacdSign = sign;
                        t.EntryMacdBar = bar;
                    }
                }
                return (t.Label ?? "Order") + "  Error: " + ex.Message;
            }

            lock (sync)
            {
                t.WaitMacd = false;
                t.EntryMacdSign = sign;
                t.EntryMacdBar = bar;
            }
            RefreshAtmOwner(t);

            string kind = type.Value == OrderType.Limit ? "LMT" : "STP";
            string msg = string.Format("{0}  {1} {2} {3} @ {4} when MACD crossed {5}",
                t.Label, isBuy ? "BUY" : "SELL", t.Quantity, kind,
                Instrument.MasterInstrument.FormatPrice(price), FormatMacd(t.EntryMacdLevel));
            if (t.Track) msg += " (tracking the Keltner line)";
            msg += chosenAtm.Length == 0
                ? ". No ATM strategy was selected."
                : ". Stop and target stay on the ATM.";
            return msg;
        }

        private string WatchMacdLeg(TrackedOrder t, bool isStop, int barIndex)
        {
            if (t == null) return null;
            NinjaTrader.Gui.NinjaScript.IndicatorRenderBase ind;
            double level;
            int prev;
            lock (sync)
            {
                if (t.MacdCloseSent || t.MacdClosePending || t.TargetCrossPending) return null;
                if ((isStop ? t.StopSource : t.TargetSource) != LegFollow.Macd) return null;
                ind = isStop ? t.StopIndicator : t.TargetIndicator;
                level = isStop ? t.StopMacdLevel : t.TargetMacdLevel;
                prev = isStop ? t.StopMacdSign : t.TargetMacdSign;
            }
            string which = isStop ? "Stop" : "Target";
            int sign;
            if (ind == null || !TryMacdSign(ind, level, barIndex, out sign))
            {
                if (NoteLegFlag(t, isStop, false))
                    return which + " paused: MACD has no value.";
                return null;
            }

            bool isLong = IsBuyAction(t.Action);
            int hit = MacdHitSign(isLong, isStop);
            bool crossed = false;
            lock (sync)
            {
                if (t.MacdCloseSent || (isStop ? t.StopSource : t.TargetSource) != LegFollow.Macd) return null;
                prev = isStop ? t.StopMacdSign : t.TargetMacdSign;
                if (prev == int.MinValue)
                {
                    bool already = hit < 0 ? sign <= 0 : sign >= 0;
                    int stored = already ? hit : sign;
                    if (isStop) t.StopMacdSign = stored;
                    else t.TargetMacdSign = stored;
                    return null;
                }
                crossed = hit < 0 ? (prev > 0 && sign <= 0) : (prev < 0 && sign >= 0);
                if (!crossed && sign != 0)
                {
                    if (isStop) t.StopMacdSign = sign;
                    else t.TargetMacdSign = sign;
                }
            }
            if (!crossed) return null;
            BeginMacdClose(t, isStop);
            return which + " closing on MACD.";
        }

        private void BeginMacdClose(TrackedOrder t, bool isStop)
        {
            bool changeable, live;
            bool cancelStops = !isStop;
            lock (sync)
            {
                if (t.MacdCloseSent || t.TargetCrossSent) return;
                t.MacdCloseSent = true;
                t.MacdClosePending = true;
                t.MacdCloseCancelsStop = cancelStops;
                t.Relink.RemoveAll(h => h.IsStop == cancelStops);
                changeable = HasWorkingChild(cancelStops ? t.StopOrders : t.TargetOrders);
                live = HasLiveChild(cancelStops ? t.StopOrders : t.TargetOrders);
            }
            if (changeable)
            {
                if (!CancelWorkingLegs(t, cancelStops))
                {
                    lock (sync)
                    {
                        t.MacdCloseSent = false;
                        t.MacdClosePending = false;
                    }
                }
            }
            else if (!live) FinishMacdClose(t);
        }

        private void FinishMacdClose(TrackedOrder t)
        {
            bool go = false;
            int qty = 0;
            lock (sync)
            {
                go = t.MacdClosePending && !HasLiveChild(t.MacdCloseCancelsStop ? t.StopOrders : t.TargetOrders);
                if (!go) return;
                t.MacdClosePending = false;
                qty = OpenQuantity(t);
            }
            if (qty <= 0 || t.Account == null) return;
            bool isLong = IsBuyAction(t.Action);
            OrderAction action = isLong ? OrderAction.Sell : OrderAction.BuyToCover;
            try
            {
                Order o = t.Account.CreateOrder(Instrument, action, OrderType.Market, OrderEntry.Manual, OrderTif, qty,
                    0, 0, string.Empty, "Close", NinjaTrader.Core.Globals.MaxDate, null);
                t.Account.Submit(new[] { o });
                lock (sync) t.CloseOrders.Add(o);
                HookAccount(t.Account);
                SetStatus("Closed on MACD.");
            }
            catch (Exception ex)
            {
                Print("KcMidTrader MACD close error: " + ex.Message);
                lock (sync)
                {
                    t.MacdCloseSent = false;
                    t.MacdClosePending = false;
                }
                SetStatus("MACD could not close the position.");
            }
        }

        private void ArmEmaCrossFromUi(OrderAction action)
        {
            if (!crossConfigured || crossEmaA == null || crossEmaB == null || ReferenceEquals(crossEmaA, crossEmaB))
            {
                SetStatusNow("Configure EMA cross before using it.");
                return;
            }
            if (emaOnChart < 2)
            {
                SetStatusNow("EMA cross needs two EMAs.");
                return;
            }
            Account acct = SelectedAccount();
            if (acct == null) { SetStatusNow("Select an account in Chart Trader."); return; }

            RememberAtm();
            int bar = CurrentBar - 1;
            int sign;
            if (bar < 0 || !TryEmaSign(crossEmaA, crossEmaB, bar, out sign))
            {
                SetStatusNow("EMA cross has no value yet.");
                return;
            }

            bool isBuy = action == OrderAction.Buy;
            int qty = ReadQty();
            string label = NewOrderLabel();
            lock (sync)
            {
                TrackedOrder t = new TrackedOrder();
                t.Account = acct;
                t.Action = action;
                t.Quantity = qty;
                t.Label = label;
                t.Track = false;
                t.WaitCross = true;
                t.ArmSign = sign;
                t.ArmBar = bar;
                t.EntryCrossBar = -1;
                t.TargetEmaA = crossEmaA;
                t.TargetEmaB = crossEmaB;
                t.StopSource = LegFollow.Atm;
                tracked.Add(t);
            }

            string wait = isBuy
                ? crossEmaAName + " to cross above " + crossEmaBName
                : crossEmaAName + " to cross below " + crossEmaBName;
            SetStatusNow((isBuy ? "Buy" : "Sell") + " armed. Waiting for " + wait + ".");
        }

        private bool TryEmaSign(NinjaTrader.Gui.NinjaScript.IndicatorRenderBase a,
                                NinjaTrader.Gui.NinjaScript.IndicatorRenderBase b, int bar, out int sign)
        {
            sign = 0;
            double va, vb;
            if (a == null || b == null || bar < 0) return false;
            if (!TryReadSeries(a, 0, bar, out va) || !TryReadSeries(b, 0, bar, out vb)) return false;
            sign = va.ApproxCompare(vb);
            return true;
        }

        // Closed bar only. The side stored on the arm is not an entry, and the entry bar is not an exit.
        private void TrackEmaCrossNow()
        {
            if (State != State.Realtime || !IsFirstTickOfBar) return;
            int bar = CurrentBar - 1;
            if (bar < 0) return;
            ReleaseEmaCrossArms(bar);

            List<TrackedOrder> orders;
            lock (sync) orders = tracked.ToList();
            foreach (TrackedOrder t in orders)
            {
                if (t.Completed || t.Order == null || t.Order.Filled <= 0) continue;
                if (t.TargetSource != LegFollow.EmaCross) continue;
                ReportFollow(WatchEmaCross(t, bar));
            }
        }

        private void ReleaseEmaCrossArms(int bar)
        {
            if (emaOnChart < 2)
            {
                bool waiting;
                lock (sync) waiting = tracked.Any(x => x.WaitCross && x.Order == null);
                if (waiting && !crossPausedNoted)
                {
                    crossPausedNoted = true;
                    SetStatus("EMA cross paused: an EMA was removed.");
                }
                return;
            }
            crossPausedNoted = false;

            List<TrackedOrder> arms;
            lock (sync) arms = tracked.Where(x => x.WaitCross && x.Order == null).ToList();
            foreach (TrackedOrder t in arms)
            {
                int sign;
                if (!TryEmaSign(t.TargetEmaA, t.TargetEmaB, bar, out sign))
                {
                    if (!t.ArmNoted)
                    {
                        t.ArmNoted = true;
                        SetStatus("EMA cross paused: an EMA has no value.");
                    }
                    continue;
                }
                t.ArmNoted = false;
                if (sign == 0 || sign == t.ArmSign) continue;
                int wanted = IsBuyAction(t.Action) ? 1 : -1;
                if (sign != wanted)
                {
                    lock (sync) { t.ArmSign = sign; t.ArmBar = bar; }
                    continue;
                }
                string msg = SubmitArmedCross(t, bar, sign);
                if (!string.IsNullOrEmpty(msg)) SetStatus(msg);
            }
        }

        private string SubmitArmedCross(TrackedOrder t, int bar, int sign)
        {
            if (t == null || t.Account == null || t.Quantity <= 0) return "Could not place the EMA cross entry.";
            bool isBuy = IsBuyAction(t.Action);
            string chosenAtm = "";
            NinjaTrader.NinjaScript.AtmStrategy atm = null;
            if (UseChartTraderAtm)
            {
                chosenAtm = liveAtmKey ?? "";
                atm = chosenAtm.Length > 0 ? liveAtm : null;
            }

            OrderAction action = isBuy ? OrderAction.Buy : OrderAction.Sell;
            string orderName = chosenAtm.Length > 0 ? "Entry" : OrderNamePrefix + (isBuy ? "Buy" : "Sell");
            Order o;
            try
            {
                o = t.Account.CreateOrder(Instrument, action, OrderType.Market, OrderEntry.Manual, OrderTif, t.Quantity,
                    0, 0, string.Empty, orderName, NinjaTrader.Core.Globals.MaxDate, null);
            }
            catch (Exception ex)
            {
                Print("KcMidTrader EMA cross entry error: " + ex.Message);
                return (t.Label ?? "Order") + "  Error: " + ex.Message;
            }

            // Arm the exit before the ATM builds its brackets, so the target is cancelled and the stop stays.
            HookAccount(t.Account);
            lock (sync)
            {
                t.Order = o;
                t.Track = false;
                t.AtmTemplate = atm;
                t.AtmName = chosenAtm;
                t.FollowAtm = chosenAtm.Length > 0;
                t.StopSource = LegFollow.Atm;
                t.TargetSource = LegFollow.EmaCross;
                t.TargetIndicator = null;
                t.TargetEmaSign = sign;
                t.EntryCrossBar = bar;
                t.TargetCrossSent = false;
                t.TargetCrossPending = false;
                t.TargetPaused = false;
                t.TargetMissing = false;
            }

            try
            {
                if (chosenAtm.Length > 0)
                {
                    System.Threading.Interlocked.Increment(ref suppressAtmEvents);
                    try { NinjaTrader.NinjaScript.AtmStrategy.StartAtmStrategy(chosenAtm, o); }
                    finally { System.Threading.Interlocked.Decrement(ref suppressAtmEvents); }
                }
                else
                    t.Account.Submit(new[] { o });
            }
            catch (Exception ex)
            {
                Print("KcMidTrader EMA cross entry error: " + ex.Message);
                lock (sync)
                {
                    bool filled = o != null && o.Filled > 0;
                    if (!filled)
                    {
                        t.Order = null;
                        t.WaitCross = true;
                        t.TargetSource = LegFollow.Atm;
                        t.TargetEmaSign = 0;
                        t.EntryCrossBar = -1;
                        t.ArmSign = sign;
                        t.ArmBar = bar;
                    }
                    else
                        t.WaitCross = false;
                }
                return (t.Label ?? "Order") + "  Error: " + ex.Message;
            }

            lock (sync)
            {
                t.WaitCross = false;
                t.ArmSign = sign;
                t.ArmBar = bar;
            }
            RefreshAtmOwner(t);

            string msg = string.Format("{0}  {1} {2} MKT on EMA cross",
                t.Label, isBuy ? "BUY" : "SELL", t.Quantity);
            if (chosenAtm.Length == 0)
                msg += ". Stop was not placed.";
            else
                msg += ". Stop stays on the ATM. Target waits for the next EMA cross.";
            return msg;
        }

        private string WatchEmaCross(TrackedOrder t, int barIndex)
        {
            if (t == null) return null;
            NinjaTrader.Gui.NinjaScript.IndicatorRenderBase a, b;
            lock (sync)
            {
                if (t.TargetSource != LegFollow.EmaCross || t.TargetCrossSent || t.TargetCrossPending) return null;
                if (t.EntryCrossBar >= 0 && barIndex <= t.EntryCrossBar) return null;
                a = t.TargetEmaA;
                b = t.TargetEmaB;
            }
            double va, vb;
            if (a == null || b == null || !TryReadSeries(a, 0, barIndex, out va) || !TryReadSeries(b, 0, barIndex, out vb))
            {
                if (NoteLegFlag(t, false, false))
                    return "EMA cross paused: an EMA has no value.";
                return null;
            }
            int sign = va.ApproxCompare(vb);
            if (sign == 0) return null;
            lock (sync)
            {
                if (t.TargetSource != LegFollow.EmaCross || t.TargetCrossSent || t.TargetCrossPending) return null;
                if (t.EntryCrossBar >= 0 && barIndex <= t.EntryCrossBar) return null;
                int prev = t.TargetEmaSign;
                if (prev == 0)
                {
                    t.TargetEmaSign = sign;
                    t.TargetMissing = false;
                    t.TargetPaused = false;
                    return null;
                }
                if (sign == prev) return null;
            }
            BeginEmaClose(t);
            return "Target closing on EMA cross.";
        }

        private void BeginEmaClose(TrackedOrder t)
        {
            bool changeable, live;
            lock (sync)
            {
                if (t.TargetCrossSent || t.MacdCloseSent) return;
                t.TargetCrossSent = true;
                t.TargetCrossPending = true;
                t.Relink.RemoveAll(h => h.IsStop);
                changeable = HasWorkingChild(t.StopOrders);
                live = HasLiveChild(t.StopOrders);
            }
            if (changeable)
            {
                if (!CancelWorkingLegs(t, true))
                {
                    lock (sync)
                    {
                        t.TargetCrossSent = false;
                        t.TargetCrossPending = false;
                    }
                }
            }
            else if (!live) FinishEmaClose(t);
        }

        private void FinishEmaClose(TrackedOrder t)
        {
            bool go;
            int qty;
            lock (sync)
            {
                go = t.TargetCrossPending && !HasLiveChild(t.StopOrders);
                if (!go) return;
                t.TargetCrossPending = false;
                qty = OpenQuantity(t);
            }
            if (qty <= 0 || t.Account == null) return;
            bool isLong = IsBuyAction(t.Action);
            OrderAction action = isLong ? OrderAction.Sell : OrderAction.BuyToCover;
            try
            {
                Order o = t.Account.CreateOrder(Instrument, action, OrderType.Market, OrderEntry.Manual, OrderTif, qty,
                    0, 0, string.Empty, "Close", NinjaTrader.Core.Globals.MaxDate, null);
                t.Account.Submit(new[] { o });
                lock (sync) t.CloseOrders.Add(o);
                HookAccount(t.Account);
                SetStatus("Closed on EMA cross.");
            }
            catch (Exception ex)
            {
                Print("KcMidTrader EMA cross close error: " + ex.Message);
                lock (sync)
                {
                    t.TargetCrossSent = false;
                    t.TargetCrossPending = false;
                }
                SetStatus("EMA cross could not close the position.");
            }
        }

        private static bool HasLiveChild(List<Order> orders)
        {
            if (orders == null) return false;
            for (int i = 0; i < orders.Count; i++)
            {
                Order o = orders[i];
                if (o != null && !Order.IsTerminalState(o.OrderState)) return true;
            }
            return false;
        }

        private static int FilledQty(List<Order> orders)
        {
            int n = 0;
            if (orders == null) return 0;
            foreach (Order o in orders)
                if (o != null && o.Filled > 0) n += o.Filled;
            return n;
        }

        private static int OpenQuantity(TrackedOrder t)
        {
            if (t == null || t.Order == null) return 0;
            int left = t.Order.Filled - FilledQty(t.StopOrders) - FilledQty(t.TargetOrders) - FilledQty(t.CloseOrders);
            return left > 0 ? left : 0;
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
