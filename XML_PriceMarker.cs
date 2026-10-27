#region Using declarations
// Core C# and data management namespaces
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

// Windows Presentation Foundation (WPF) namespaces used for standard UI colors
using System.Windows;
using System.Windows.Media;

// NinjaTrader specific namespaces for charting, drawing, and core logic
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// ============================================================================
// XML_PriceMarker v2.1  (v1.0 price levels + Forex Factory news events)
//
// WHAT IT DOES
//   1) PRICE LEVELS (unchanged from v1.0)
//      Reads a custom XML file containing price levels and draws them on
//      the chart, with screen-locked labels rendered in OnRender.
//
//   2) NEWS EVENTS (new in v2.0)
//      Downloads the Forex Factory "this week" calendar feed (JSON):
//        https://nfs.faireconomy.media/ff_calendar_thisweek.json?version=...
//      and shows the events ON the chart in two ways:
//        a) Vertical time markers at each event's time (colour = impact,
//           dashed, with a small "HH:mm CUR" tag at the top of the chart).
//           Events that share the same time are grouped into ONE marker.
//        b) An "Upcoming News" panel in the top-right corner listing the
//           next events (time, currency, impact, title, forecast/previous
//           and a live countdown to the next one in the header).
//
// NOTES FOR THE TRADER
//   - The download happens on a background thread so the chart never freezes.
//     It re-downloads automatically every News Refresh (minutes) while bars
//     are updating. Set Enable News = false and you have exact v1.0 behaviour.
//   - Feed dates arrive with a timezone offset (e.g. -04:00) and are
//     converted to THIS PC's local time, which is what your chart bars use.
//   - A vertical marker can only appear once price bars exist at/after that
//     time (a chart has no timeline for the future yet). The panel is what
//     shows you what is still coming; the markers appear as the week unfolds
//     and stay visible when you scroll back over the week.
//   - Default filter is USD only, High + Medium impact - the events that
//     move ES/NQ. Set News Currencies blank for all currencies, or e.g.
//     "USD,CAD" for both.
//   - No external DLLs are needed: the JSON is parsed with a small built-in
//     parser, so this compiles in a stock NinjaTrader 8 install.
//   v2.1 fixes the two v2.0 compile errors from Jason's 2026-10-27 grid:
//   the NewsImpactFilter enum moved to global scope (CS0246 in the
//   MarketAnalyzer/Strategies wrappers) and the visible-range times are
//   read from Bars.GetTime instead of ChartBars.GetTime (CS1061).
//   - INSTALL: this file has the SAME class name as v1.0. Replace the old
//     XML_PriceMarker.cs in Documents\NinjaTrader 8\bin\Custom\Indicators
//     (do not keep both copies), then compile (F5) in the NinjaScript Editor.
// ============================================================================

// NOTE (v2.1 compile fix): this enum deliberately lives in the GLOBAL
// namespace, outside NinjaTrader.NinjaScript.Indicators. The generated
// wrapper code in the MarketAnalyzer / Strategies namespaces could not
// resolve it when it was nested inside the Indicators namespace (CS0246).
// At global scope the plain name resolves from every generated section.
public enum NewsImpactFilter
{
    HighOnly,
    HighAndMedium,
    All
}

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// XML_PriceMarker
    /// Reads a custom XML file containing price levels and draws them on the chart,
    /// and (v2.0) plots Forex Factory news events from the faireconomy JSON feed.
    /// </summary>
    public class XML_PriceMarker : Indicator
    {
        //=====================================================================
        // PRIVATE FIELDS & DATA STRUCTURES
        //=====================================================================

        /// <summary>Master list holding all valid price levels parsed from the XML.</summary>
        private List<LevelData> priceLevels = new List<LevelData>();

        [XmlIgnore]
        [Browsable(false)]
        public List<LevelData> PublicPriceLevels => priceLevels;

        /// <summary>Dictionary for fast lookup of standard WPF color names to Brush objects.</summary>
        private Dictionary<string, Brush> brushMap;

        /// <summary>Internal variable storing the file path, updated via the UI property.</summary>
        private string xmlFilePath = @"G:\My Drive\price_levels.xml";

        /// <summary>Tracks whether the static horizontal lines have already been drawn.</summary>
        private bool linesDrawn = false;

        /// <summary>
        /// Data structure representing a single XML node.
        /// </summary>
        public class LevelData
        {
            public string Type;
            public double Value;
            [XmlIgnore]
            public Brush LineBrush;
            public string Style;
            public string Label;
            public double Thickness;
        }

        //=====================================================================
        // NEWS - DATA STRUCTURES & STATE
        //=====================================================================

        /// <summary>One calendar event parsed from the JSON feed.</summary>
        public class NewsEvent
        {
            public string   Title;
            public string   Country;        // currency code, e.g. "USD"
            public DateTime EventTime;      // converted to this PC's local time
            public string   Impact;         // "High" / "Medium" / "Low" / "Holiday"
            public int      ImpactRank;     // High=3, Medium=2, Low=1, Holiday=0
            public string   Forecast;
            public string   Previous;
        }

        /// <summary>All events from the last successful download (unfiltered).</summary>
        private List<NewsEvent> newsEvents = new List<NewsEvent>();

        /// <summary>Lock guarding newsEvents - the download runs on a background thread while OnRender reads.</summary>
        private readonly object newsLock = new object();

        /// <summary>True while a background download is in flight (stops overlapping fetches).</summary>
        private bool newsFetchInProgress = false;

        /// <summary>When the feed was last fetched (local time). MinValue = never.</summary>
        private DateTime lastNewsFetch = DateTime.MinValue;

        /// <summary>Short status shown in the panel while loading / after an error.</summary>
        private string newsStatus = "not loaded yet";

        /// <summary>Upper-case currency codes to keep; empty set = keep all currencies.</summary>
        private HashSet<string> currencyFilter = new HashSet<string>();

        //=====================================================================
        // LIFECYCLE MANAGEMENT
        //=====================================================================

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = @"Plots price levels from XML file with screen-locked text, plus Forex Factory news events on the chart timeline and in an upcoming-events panel.";
                Name                        = "XML_PriceMarker";
                Calculate                   = Calculate.OnBarClose;
                IsOverlay                   = true;
                DisplayInDataBox            = false;
                DrawOnPricePanel            = true;

                // CRITICAL UI OVERRIDES
                PaintPriceMarkers           = false;
                ScaleJustification          = ScaleJustification.Right;
                IsSuspendedWhileInactive    = true;
                IsAutoScale                 = false;

                // Default User UI Settings - price levels (unchanged from v1.0)
                XmlFilePath                 = @"G:\My Drive\price_levels.xml";
                ShowLabels                  = true;
                FontSize                    = 10;

                // Default User UI Settings - news (new in v2.0)
                EnableNews                  = true;
                NewsFeedUrl                 = @"https://nfs.faireconomy.media/ff_calendar_thisweek.json?version=43f1a61280443dd978ecbdea79afbd67";
                NewsCurrencies              = "USD";
                NewsMinImpact               = NewsImpactFilter.HighAndMedium;
                ShowNewsLines               = true;
                ShowNewsPanel               = true;
                NewsRefreshMinutes          = 60;
                NewsPanelMaxEvents          = 5;
            }
            else if (State == State.DataLoaded)
            {
                xmlFilePath = XmlFilePath;
                linesDrawn  = false;

                brushMap = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase)
                {
                    {"Red", Brushes.Red}, {"Lime", Brushes.Lime}, {"LimeGreen", Brushes.LimeGreen},
                    {"Orange", Brushes.Orange}, {"DarkOrange", Brushes.DarkOrange}, {"Blue", Brushes.Blue},
                    {"Gold", Brushes.Gold}, {"Green", Brushes.Green}, {"DarkGreen", Brushes.DarkGreen},
                    {"White", Brushes.White}, {"Yellow", Brushes.Yellow}, {"Cyan", Brushes.Cyan},
                    {"Magenta", Brushes.Magenta}, {"Purple", Brushes.Purple}, {"Gray", Brushes.Gray},
                    {"Silver", Brushes.Silver}
                };

                LoadPriceLevels();

                // Build the currency filter set once, then kick off the first
                // background download. Nothing here blocks the chart thread.
                BuildCurrencyFilter();
                if (EnableNews)
                    FetchNewsAsync();
            }
        }

        //=====================================================================
        // XML PARSER
        //=====================================================================

        private void LoadPriceLevels()
        {
            priceLevels.Clear();

            try
            {
                if (File.Exists(xmlFilePath))
                {
                    XmlDocument xmlDoc = new XmlDocument();
                    xmlDoc.Load(xmlFilePath);

                    foreach (XmlNode node in xmlDoc.SelectNodes("//Level"))
                    {
                        LevelData lvl = new LevelData();

                        lvl.Type = node.Attributes["type"]?.Value ?? "general";

                        if (double.TryParse(node.Attributes["value"]?.Value, out double val))
                            lvl.Value = val;
                        else
                            continue;

                        string colorAttribute = node.Attributes["color"]?.Value ?? "Green";
                        lvl.LineBrush = GetBrushFromString(colorAttribute);

                        lvl.Style = node.Attributes["style"]?.Value ?? "Solid";
                        lvl.Label = node.Attributes["label"]?.Value ?? "Level";

                        if (double.TryParse(node.Attributes["thickness"]?.Value, out double thickness))
                            lvl.Thickness = thickness;
                        else
                            lvl.Thickness = 1;

                        priceLevels.Add(lvl);
                    }
                    Print($"XML_PriceMarker: Successfully loaded {priceLevels.Count} price levels from: {xmlFilePath}");
                }
                else
                {
                    Print($"XML_PriceMarker Error: XML file not found at path: {xmlFilePath}");
                }
            }
            catch (Exception ex)
            {
                Print("XML_PriceMarker Error parsing XML: " + ex.Message);
            }
        }

        //=====================================================================
        // NEWS - DOWNLOAD (background thread, never blocks the chart)
        //=====================================================================

        private void BuildCurrencyFilter()
        {
            currencyFilter.Clear();
            if (string.IsNullOrWhiteSpace(NewsCurrencies))
                return; // empty = all currencies

            foreach (string part in NewsCurrencies.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                currencyFilter.Add(part.Trim().ToUpperInvariant());
        }

        private void FetchNewsAsync()
        {
            if (newsFetchInProgress || string.IsNullOrWhiteSpace(NewsFeedUrl))
                return;

            newsFetchInProgress = true;
            newsStatus = "loading...";
            string url = NewsFeedUrl;

            Task.Run(() =>
            {
                try
                {
                    // The feed is HTTPS; make sure TLS 1.2 is on (NT8 default can be older).
                    try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                    string json;
                    using (WebClient wc = new WebClient())
                    {
                        // Some CDNs reject requests with no user-agent.
                        wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NinjaTrader/8";
                        json = wc.DownloadString(url);
                    }

                    List<NewsEvent> parsed = ParseNewsFeed(json);

                    lock (newsLock)
                    {
                        newsEvents = parsed;
                    }
                    lastNewsFetch = DateTime.Now;
                    newsStatus = "updated " + lastNewsFetch.ToString("HH:mm");
                    Print($"XML_PriceMarker News: loaded {parsed.Count} events from the calendar feed.");
                }
                catch (Exception ex)
                {
                    // Keep any previously loaded events; just note the failure.
                    newsStatus = "feed error - keeping last data";
                    Print("XML_PriceMarker News Error: " + ex.Message);
                }
                finally
                {
                    newsFetchInProgress = false;
                }
            });
        }

        //=====================================================================
        // NEWS - LIGHTWEIGHT JSON PARSER
        // The feed is a flat array of objects:
        //   {"title":"...","country":"USD","date":"2026-10-28T14:00:00-04:00",
        //    "impact":"High","forecast":"4.00%","previous":"4.00%"}
        // Parsed by hand so no external JSON DLL reference is needed.
        //=====================================================================

        private List<NewsEvent> ParseNewsFeed(string json)
        {
            List<NewsEvent> result = new List<NewsEvent>();
            if (string.IsNullOrWhiteSpace(json))
                return result;

            // Flat objects only - no nesting in this feed.
            foreach (Match obj in Regex.Matches(json, @"\{[^{}]*\}"))
            {
                string body = obj.Value;

                string dateText = ExtractJsonString(body, "date");
                if (string.IsNullOrEmpty(dateText))
                    continue;

                // The offset in the string (e.g. -04:00) is honoured, then the
                // instant is converted to this PC's local time for the chart.
                if (!DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
                    continue;

                string impact = ExtractJsonString(body, "impact") ?? "";

                NewsEvent ev = new NewsEvent
                {
                    Title      = ExtractJsonString(body, "title") ?? "",
                    Country    = (ExtractJsonString(body, "country") ?? "").ToUpperInvariant(),
                    EventTime  = dto.ToLocalTime().DateTime,
                    Impact     = impact,
                    ImpactRank = ImpactToRank(impact),
                    Forecast   = ExtractJsonString(body, "forecast") ?? "",
                    Previous   = ExtractJsonString(body, "previous") ?? ""
                };
                result.Add(ev);
            }

            result.Sort((a, b) => a.EventTime.CompareTo(b.EventTime));
            return result;
        }

        /// <summary>Pulls one "key":"value" string field out of a flat JSON object, unescaping it.</summary>
        private string ExtractJsonString(string body, string key)
        {
            Match m = Regex.Match(body, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
            if (!m.Success)
                return null;
            return UnescapeJson(m.Groups[1].Value);
        }

        private string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0)
                return s;

            s = Regex.Replace(s, @"\\u([0-9a-fA-F]{4})",
                m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            return s.Replace("\\/", "/")
                    .Replace("\\\"", "\"")
                    .Replace("\\n", " ")
                    .Replace("\\r", " ")
                    .Replace("\\t", " ")
                    .Replace("\\\\", "\\");
        }

        private int ImpactToRank(string impact)
        {
            if (string.Equals(impact, "High", StringComparison.OrdinalIgnoreCase))    return 3;
            if (string.Equals(impact, "Medium", StringComparison.OrdinalIgnoreCase))  return 2;
            if (string.Equals(impact, "Low", StringComparison.OrdinalIgnoreCase))     return 1;
            return 0; // Holiday / anything else
        }

        private int MinImpactRank()
        {
            switch (NewsMinImpact)
            {
                case NewsImpactFilter.HighOnly:      return 3;
                case NewsImpactFilter.HighAndMedium: return 2;
                default:                             return 0; // All
            }
        }

        private Brush ImpactBrush(int rank)
        {
            switch (rank)
            {
                case 3:  return Brushes.Red;
                case 2:  return Brushes.Orange;
                case 1:  return Brushes.Yellow;
                default: return Brushes.Gray;
            }
        }

        /// <summary>Snapshot of the events that pass the currency + impact filters, oldest first.</summary>
        private List<NewsEvent> GetFilteredNews()
        {
            int minRank = MinImpactRank();
            lock (newsLock)
            {
                return newsEvents
                    .Where(e => e.ImpactRank >= minRank
                             && (currencyFilter.Count == 0 || currencyFilter.Contains(e.Country)))
                    .ToList();
            }
        }

        private string FormatCountdown(DateTime eventTime)
        {
            TimeSpan span = eventTime - DateTime.Now;
            if (span.TotalMinutes >= -1 && span.TotalMinutes < 1)
                return "NOW";
            if (span.TotalMinutes < 0)
            {
                TimeSpan ago = span.Duration();
                return ago.TotalHours >= 1 ? $"{(int)ago.TotalHours}h {ago.Minutes:00}m ago" : $"{ago.Minutes}m ago";
            }
            if (span.TotalDays >= 1)
                return $"in {(int)span.TotalDays}d {span.Hours}h {span.Minutes:00}m";
            if (span.TotalHours >= 1)
                return $"in {(int)span.TotalHours}h {span.Minutes:00}m";
            return $"in {span.Minutes}m";
        }

        //=====================================================================
        // MAIN DRAWING LOGIC (DRAWN ONCE)
        //=====================================================================

        protected override void OnBarUpdate()
        {
            // News auto-refresh: checked on bar close, so a 1-minute chart
            // re-checks every minute and fetches when the interval has passed.
            if (EnableNews && !newsFetchInProgress
                && (DateTime.Now - lastNewsFetch).TotalMinutes >= Math.Max(1, NewsRefreshMinutes))
            {
                FetchNewsAsync();
            }

            // Exit immediately if lines are already drawn, or if we don't have at least 2 bars yet
            if (linesDrawn || CurrentBar < 1 || priceLevels.Count == 0)
                return;

            foreach (var lvl in priceLevels)
            {
                DashStyleHelper dashStyle = GetDashStyle(lvl.Style);
                string uniqueTag = lvl.Label + "_" + lvl.Value;

                // Anchor using CurrentBar - 1 and CurrentBar on this single pass.
                // NinjaTrader converts these to static bar/time anchors internally so they stay forever.
                ExtendedLine hLine = Draw.ExtendedLine(this, uniqueTag, false, 1, lvl.Value, 0, lvl.Value, lvl.LineBrush, dashStyle, (int)lvl.Thickness);
                hLine.IsAutoScale = false;
            }

            // Lock the flag so OnBarUpdate never redraws these static lines again
            linesDrawn = true;
        }

        //=====================================================================
        // ONRENDER (OPTIMIZED DIRECT-TO-SCREEN GRAPHICS)
        //=====================================================================

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (RenderTarget == null || ChartPanel == null)
                return;

            // 1) News vertical time markers (under the labels / panel)
            if (EnableNews && ShowNewsLines)
                RenderNewsLines(chartControl);

            // 2) Price level labels (v1.0 behaviour, unchanged)
            if (ShowLabels && priceLevels.Count > 0)
                RenderPriceLabels(chartScale);

            // 3) Upcoming news panel, top-right
            if (EnableNews && ShowNewsPanel)
                RenderNewsPanel();
        }

        private void RenderPriceLabels(ChartScale chartScale)
        {
            using (SharpDX.DirectWrite.TextFormat textFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                FontSize))
            {
                float x = ChartPanel.X + 10;

                foreach (var lvl in priceLevels)
                {
                    // Viewport Culling: Skip creating Direct2D objects if the price level is off-screen
                    if (lvl.Value < chartScale.MinValue || lvl.Value > chartScale.MaxValue)
                        continue;

                    float y = chartScale.GetYByValue(lvl.Value) - (FontSize + 2);

                    using (SharpDX.DirectWrite.TextLayout textLayout = new SharpDX.DirectWrite.TextLayout(
                        NinjaTrader.Core.Globals.DirectWriteFactory,
                        lvl.Label,
                        textFormat,
                        ChartPanel.W,
                        textFormat.FontSize))
                    using (SharpDX.Direct2D1.Brush dxBrush = lvl.LineBrush.ToDxBrush(RenderTarget))
                    {
                        RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), textLayout, dxBrush);
                    }
                }
            }
        }

        //=====================================================================
        // NEWS - VERTICAL TIME MARKERS
        // Drawn in OnRender (not Draw.VerticalLine) so they appear as soon as
        // the download lands, follow scrolling/zoom, and need no bar to exist
        // at the exact event minute beyond the visible range check below.
        //=====================================================================

        private void RenderNewsLines(ChartControl chartControl)
        {
            if (ChartBars == null || Bars == null || CurrentBar < 1)
                return;

            List<NewsEvent> filtered = GetFilteredNews();
            if (filtered.Count == 0)
                return;

            DateTime visibleStart, visibleEnd;
            try
            {
                visibleStart = Bars.GetTime(ChartBars.FromIndex);
                visibleEnd   = Bars.GetTime(ChartBars.ToIndex);
            }
            catch { return; }
            if (visibleEnd < visibleStart)
            {
                DateTime tmp = visibleStart; visibleStart = visibleEnd; visibleEnd = tmp;
            }

            float top    = (float)ChartPanel.Y;
            float bottom = (float)(ChartPanel.Y + ChartPanel.H);
            float lastLabelX = -1000f;
            int   labelRow   = 0;

            using (SharpDX.DirectWrite.TextFormat tagFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                Math.Max(8, FontSize - 1)))
            using (SharpDX.Direct2D1.StrokeStyle dashStyle = new SharpDX.Direct2D1.StrokeStyle(
                RenderTarget.Factory,
                new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash }))
            {
                // Group events sharing the same time into a single marker -
                // e.g. the 08:30 USD cluster becomes one line, tagged (3).
                foreach (var group in filtered.GroupBy(e => e.EventTime).OrderBy(g => g.Key))
                {
                    DateTime t = group.Key;
                    if (t < visibleStart || t > visibleEnd)
                        continue;

                    int barIdx = Bars.GetBar(t);
                    if (barIdx < 0 || barIdx > CurrentBar)
                        continue;

                    // Interpolate inside the bar so e.g. a :30 event lands
                    // mid-bar on a 5-minute chart instead of on its left edge.
                    float x = chartControl.GetXByBarIndex(ChartBars, barIdx);
                    DateTime barTime = Bars.GetTime(barIdx);
                    if (barIdx + 1 < Bars.Count)
                    {
                        DateTime nextTime = Bars.GetTime(barIdx + 1);
                        double spanSeconds = (nextTime - barTime).TotalSeconds;
                        if (spanSeconds > 0)
                        {
                            double frac = (t - barTime).TotalSeconds / spanSeconds;
                            if (frac > 0 && frac < 1)
                                x += (float)(frac * chartControl.GetBarPaintWidth(ChartBars));
                        }
                    }

                    int topRank = group.Max(e => e.ImpactRank);
                    string country = group.First().Country;
                    int count = group.Count();

                    using (SharpDX.Direct2D1.Brush dxBrush = ImpactBrush(topRank).ToDxBrush(RenderTarget))
                    {
                        dxBrush.Opacity = topRank >= 3 ? 0.85f : 0.55f;
                        RenderTarget.DrawLine(
                            new SharpDX.Vector2(x, top),
                            new SharpDX.Vector2(x, bottom),
                            dxBrush,
                            topRank >= 3 ? 1.5f : 1.0f,
                            dashStyle);

                        // Tag at the top; stagger vertically when markers crowd.
                        if (x - lastLabelX < 80f) labelRow = (labelRow + 1) % 3; else labelRow = 0;
                        lastLabelX = x;
                        string tag = t.ToString("HH:mm") + " " + country + (count > 1 ? " (" + count + ")" : "");
                        float tagY = top + 2 + labelRow * (tagFormat.FontSize + 3);
                        using (SharpDX.DirectWrite.TextLayout tagLayout = new SharpDX.DirectWrite.TextLayout(
                            NinjaTrader.Core.Globals.DirectWriteFactory, tag, tagFormat, 120f, tagFormat.FontSize + 2))
                        {
                            RenderTarget.DrawTextLayout(new SharpDX.Vector2(x + 3, tagY), tagLayout, dxBrush);
                        }
                    }
                }
            }
        }

        //=====================================================================
        // NEWS - UPCOMING EVENTS PANEL (top-right)
        //=====================================================================

        private void RenderNewsPanel()
        {
            DateTime now = DateTime.Now;
            // Keep events from the last 15 minutes visible (shown as "Xm ago"),
            // everything older drops off the panel.
            List<NewsEvent> upcoming = GetFilteredNews()
                .Where(e => e.EventTime >= now.AddMinutes(-15))
                .Take(Math.Max(1, NewsPanelMaxEvents))
                .ToList();

            float rowH   = FontSize + 7f;
            float panelW = Math.Min(440f, (float)ChartPanel.W - 20f);
            if (panelW < 220f)
                return;
            int rows = Math.Max(1, upcoming.Count);
            float panelH = rowH + rows * rowH + 8f;
            float px = (float)(ChartPanel.X + ChartPanel.W) - panelW - 10f;
            float py = (float)ChartPanel.Y + 10f;

            using (SharpDX.DirectWrite.TextFormat fmt = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Normal,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                FontSize))
            using (SharpDX.DirectWrite.TextFormat fmtBold = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                FontSize))
            using (SharpDX.Direct2D1.SolidColorBrush bgBrush = new SharpDX.Direct2D1.SolidColorBrush(
                RenderTarget, new SharpDX.Color4(0f, 0f, 0f, 0.80f)))
            using (SharpDX.Direct2D1.Brush whiteBrush = Brushes.White.ToDxBrush(RenderTarget))
            using (SharpDX.Direct2D1.Brush grayBrush  = Brushes.Silver.ToDxBrush(RenderTarget))
            {
                RenderTarget.FillRectangle(new SharpDX.RectangleF(px, py, panelW, panelH), bgBrush);

                // Header: filter summary + countdown to the next listed event.
                string currenciesLabel = currencyFilter.Count == 0 ? "ALL" : string.Join("/", currencyFilter.OrderBy(c => c));
                string header = "NEWS " + currenciesLabel + " - " + newsStatus;
                if (upcoming.Count > 0)
                    header += "  |  Next: " + upcoming[0].Title + " " + FormatCountdown(upcoming[0].EventTime);
                DrawPanelText(header, fmtBold, whiteBrush, px + 8, py + 4, panelW - 16);

                if (upcoming.Count == 0)
                {
                    string empty = lastNewsFetch == DateTime.MinValue
                        ? "Waiting for the calendar feed..."
                        : "No matching events left this week.";
                    DrawPanelText(empty, fmt, grayBrush, px + 8, py + 4 + rowH, panelW - 16);
                    return;
                }

                for (int i = 0; i < upcoming.Count; i++)
                {
                    NewsEvent ev = upcoming[i];
                    float ry = py + 4 + rowH * (i + 1);

                    // Impact dot in the event's colour.
                    using (SharpDX.Direct2D1.Brush dotBrush = ImpactBrush(ev.ImpactRank).ToDxBrush(RenderTarget))
                    {
                        RenderTarget.FillRectangle(new SharpDX.RectangleF(px + 8, ry + 3, 8, 8), dotBrush);
                    }

                    string row = ev.EventTime.ToString("ddd HH:mm") + "  " + ev.Country + "  " + ev.Title;
                    if (!string.IsNullOrEmpty(ev.Forecast) || !string.IsNullOrEmpty(ev.Previous))
                        row += "   [F: " + ev.Forecast + "  P: " + ev.Previous + "]";
                    row += "  - " + FormatCountdown(ev.EventTime);

                    DrawPanelText(row, fmt, whiteBrush, px + 22, ry, panelW - 30);
                }
            }
        }

        private void DrawPanelText(string text, SharpDX.DirectWrite.TextFormat format,
                                   SharpDX.Direct2D1.Brush brush, float x, float y, float maxWidth)
        {
            // Rough character budget so single-line rows never spill out of the panel.
            int maxChars = Math.Max(10, (int)(maxWidth / (format.FontSize * 0.58)));
            if (text.Length > maxChars)
                text = text.Substring(0, maxChars - 1) + "…";

            using (SharpDX.DirectWrite.TextLayout layout = new SharpDX.DirectWrite.TextLayout(
                NinjaTrader.Core.Globals.DirectWriteFactory, text, format, maxWidth, format.FontSize + 4))
            {
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), layout, brush);
            }
        }

        //=====================================================================
        // HELPER METHODS (COLOR & STYLE CONVERSION)
        //=====================================================================

        private Brush GetBrushFromString(string colorStr)
        {
            if (string.IsNullOrWhiteSpace(colorStr)) return Brushes.Green;

            if (colorStr.StartsWith("#"))
            {
                try
                {
                    Color customColor = (Color)ColorConverter.ConvertFromString(colorStr);
                    SolidColorBrush customBrush = new SolidColorBrush(customColor);
                    customBrush.Freeze();
                    return customBrush;
                }
                catch { return Brushes.Green; }
            }

            if (brushMap.TryGetValue(colorStr, out Brush mappedBrush))
                return mappedBrush;

            return Brushes.Green;
        }

        private DashStyleHelper GetDashStyle(string style)
        {
            if (string.IsNullOrEmpty(style)) return DashStyleHelper.Solid;

            switch (style.ToLower())
            {
                case "dash": case "dashed": return DashStyleHelper.Dash;
                case "dot": case "dotted": return DashStyleHelper.Dot;
                case "dashdot": case "dash-dot": return DashStyleHelper.DashDot;
                case "dashdotdot": case "dash-dot-dot": return DashStyleHelper.DashDotDot;
                case "solid": default: return DashStyleHelper.Solid;
            }
        }

        //=====================================================================
        // USER-CONFIGURABLE PROPERTIES (UI SETTINGS)
        //=====================================================================
        #region Properties

        [NinjaScriptProperty]
        [Display(Name = "XML File Path", Description = "Full path to the XML file containing price levels", Order = 1, GroupName = "File Settings")]
        public string XmlFilePath { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show Labels", Description = "Show/hide text labels on price levels", Order = 2, GroupName = "Display Settings")]
        public bool ShowLabels { get; set; }

        [NinjaScriptProperty]
        [Range(6, 24)]
        [Display(Name = "Font Size", Description = "Size of the text labels and the news panel", Order = 3, GroupName = "Display Settings")]
        public int FontSize { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable News", Description = "Download and display Forex Factory news events", Order = 1, GroupName = "News Feed")]
        public bool EnableNews { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "News Feed URL", Description = "Forex Factory calendar JSON feed (faireconomy)", Order = 2, GroupName = "News Feed")]
        public string NewsFeedUrl { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "News Currencies", Description = "Comma-separated currency codes to show, e.g. USD or USD,CAD. Leave blank for all", Order = 3, GroupName = "News Feed")]
        public string NewsCurrencies { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "News Min Impact", Description = "Smallest news impact to display", Order = 4, GroupName = "News Feed")]
        public NewsImpactFilter NewsMinImpact { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show News Lines", Description = "Draw a vertical line on the chart at each news event time", Order = 5, GroupName = "News Feed")]
        public bool ShowNewsLines { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Show News Panel", Description = "Show the upcoming-events panel in the top-right of the chart", Order = 6, GroupName = "News Feed")]
        public bool ShowNewsPanel { get; set; }

        [NinjaScriptProperty]
        [Range(5, 1440)]
        [Display(Name = "News Refresh (minutes)", Description = "How often to re-download the calendar feed", Order = 7, GroupName = "News Feed")]
        public int NewsRefreshMinutes { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "News Panel Max Events", Description = "How many upcoming events the panel lists", Order = 8, GroupName = "News Feed")]
        public int NewsPanelMaxEvents { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private XML_PriceMarker[] cacheXML_PriceMarker;
		public XML_PriceMarker XML_PriceMarker(string xmlFilePath, bool showLabels, int fontSize, bool enableNews, string newsFeedUrl, string newsCurrencies, NewsImpactFilter newsMinImpact, bool showNewsLines, bool showNewsPanel, int newsRefreshMinutes, int newsPanelMaxEvents)
		{
			return XML_PriceMarker(Input, xmlFilePath, showLabels, fontSize, enableNews, newsFeedUrl, newsCurrencies, newsMinImpact, showNewsLines, showNewsPanel, newsRefreshMinutes, newsPanelMaxEvents);
		}

		public XML_PriceMarker XML_PriceMarker(ISeries<double> input, string xmlFilePath, bool showLabels, int fontSize, bool enableNews, string newsFeedUrl, string newsCurrencies, NewsImpactFilter newsMinImpact, bool showNewsLines, bool showNewsPanel, int newsRefreshMinutes, int newsPanelMaxEvents)
		{
			if (cacheXML_PriceMarker != null)
				for (int idx = 0; idx < cacheXML_PriceMarker.Length; idx++)
					if (cacheXML_PriceMarker[idx] != null && cacheXML_PriceMarker[idx].XmlFilePath == xmlFilePath && cacheXML_PriceMarker[idx].ShowLabels == showLabels && cacheXML_PriceMarker[idx].FontSize == fontSize && cacheXML_PriceMarker[idx].EnableNews == enableNews && cacheXML_PriceMarker[idx].NewsFeedUrl == newsFeedUrl && cacheXML_PriceMarker[idx].NewsCurrencies == newsCurrencies && cacheXML_PriceMarker[idx].NewsMinImpact == newsMinImpact && cacheXML_PriceMarker[idx].ShowNewsLines == showNewsLines && cacheXML_PriceMarker[idx].ShowNewsPanel == showNewsPanel && cacheXML_PriceMarker[idx].NewsRefreshMinutes == newsRefreshMinutes && cacheXML_PriceMarker[idx].NewsPanelMaxEvents == newsPanelMaxEvents && cacheXML_PriceMarker[idx].EqualsInput(input))
						return cacheXML_PriceMarker[idx];
			return CacheIndicator<XML_PriceMarker>(new XML_PriceMarker(){ XmlFilePath = xmlFilePath, ShowLabels = showLabels, FontSize = fontSize, EnableNews = enableNews, NewsFeedUrl = newsFeedUrl, NewsCurrencies = newsCurrencies, NewsMinImpact = newsMinImpact, ShowNewsLines = showNewsLines, ShowNewsPanel = showNewsPanel, NewsRefreshMinutes = newsRefreshMinutes, NewsPanelMaxEvents = newsPanelMaxEvents }, input, ref cacheXML_PriceMarker);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.XML_PriceMarker XML_PriceMarker(string xmlFilePath, bool showLabels, int fontSize, bool enableNews, string newsFeedUrl, string newsCurrencies, NewsImpactFilter newsMinImpact, bool showNewsLines, bool showNewsPanel, int newsRefreshMinutes, int newsPanelMaxEvents)
		{
			return indicator.XML_PriceMarker(Input, xmlFilePath, showLabels, fontSize, enableNews, newsFeedUrl, newsCurrencies, newsMinImpact, showNewsLines, showNewsPanel, newsRefreshMinutes, newsPanelMaxEvents);
		}

		public Indicators.XML_PriceMarker XML_PriceMarker(ISeries<double> input , string xmlFilePath, bool showLabels, int fontSize, bool enableNews, string newsFeedUrl, string newsCurrencies, NewsImpactFilter newsMinImpact, bool showNewsLines, bool showNewsPanel, int newsRefreshMinutes, int newsPanelMaxEvents)
		{
			return indicator.XML_PriceMarker(input, xmlFilePath, showLabels, fontSize, enableNews, newsFeedUrl, newsCurrencies, newsMinImpact, showNewsLines, showNewsPanel, newsRefreshMinutes, newsPanelMaxEvents);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.XML_PriceMarker XML_PriceMarker(string xmlFilePath, bool showLabels, int fontSize, bool enableNews, string newsFeedUrl, string newsCurrencies, NewsImpactFilter newsMinImpact, bool showNewsLines, bool showNewsPanel, int newsRefreshMinutes, int newsPanelMaxEvents)
		{
			return indicator.XML_PriceMarker(Input, xmlFilePath, showLabels, fontSize, enableNews, newsFeedUrl, newsCurrencies, newsMinImpact, showNewsLines, showNewsPanel, newsRefreshMinutes, newsPanelMaxEvents);
		}

		public Indicators.XML_PriceMarker XML_PriceMarker(ISeries<double> input , string xmlFilePath, bool showLabels, int fontSize, bool enableNews, string newsFeedUrl, string newsCurrencies, NewsImpactFilter newsMinImpact, bool showNewsLines, bool showNewsPanel, int newsRefreshMinutes, int newsPanelMaxEvents)
		{
			return indicator.XML_PriceMarker(input, xmlFilePath, showLabels, fontSize, enableNews, newsFeedUrl, newsCurrencies, newsMinImpact, showNewsLines, showNewsPanel, newsRefreshMinutes, newsPanelMaxEvents);
		}
	}
}

#endregion
