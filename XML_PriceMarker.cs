#region Using declarations
// Core C# and data management namespaces
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
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

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// XML_PriceMarker
    /// Reads a custom XML file containing price levels and draws them on the chart.
    /// Features:
    /// - Infinite Extended Lines (bypasses default Y-Axis price markers).
    /// - Screen-locked text labels using Direct2D (text stays pinned to the left of the screen, immune to scrolling).
    /// - Supports standard color names and custom Hex codes.
    /// </summary>
    public class XML_PriceMarker : Indicator
    {
        //=====================================================================
        // PRIVATE FIELDS & DATA STRUCTURES
        //=====================================================================
        
        /// <summary>Master list holding all valid price levels parsed from the XML.</summary>
        [Browsable(false)]
		[XmlIgnore]
		public List<LevelData> priceLevels = new List<LevelData>();
        
        /// <summary>Dictionary for fast lookup of standard WPF color names to Brush objects.</summary>
        private Dictionary<string, Brush> brushMap;
        
        /// <summary>Internal variable storing the file path, updated via the UI property.</summary>
        private string xmlFilePath = @"d:\futures_xml\PriceLevels.xml";

        /// <summary>
        /// Data structure representing a single XML node.
        /// Stores all formatting and positioning data needed for rendering.
        /// </summary>
        public class LevelData
        {
            public string Type;             // Categorization (e.g., support, resistance)
            public double Value;            // The exact Y-axis price
            public Brush LineBrush;         // Pre-compiled WPF Brush for optimal rendering performance
            public string Style;            // Line styling (Solid, Dash, Dot, etc.)
            public string Label;            // The text to display on the screen
            public double Thickness;        // Line thickness in pixels
        }

        //=====================================================================
        // LIFECYCLE MANAGEMENT
        //=====================================================================
        
        /// <summary>
        /// Handles the setup, configuration, and data-loading phases of the indicator.
        /// </summary>
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                // Basic indicator identity and behavior
                Description                 = @"Plots price levels from XML file with screen-locked text.";
                Name                        = "XML_PriceMarker";
                Calculate                   = Calculate.OnBarClose;  // Calculate on close saves CPU overhead
                IsOverlay                   = true;                  // Draw directly over the price candles
                DisplayInDataBox            = true;
                DrawOnPricePanel            = true;
                
                // CRITICAL UI OVERRIDES
                PaintPriceMarkers           = false;                 // Hides the automatic Y-axis highlighted price tag
                ScaleJustification          = ScaleJustification.Right;
                IsSuspendedWhileInactive    = true;
                IsAutoScale                 = false;                 // Prevents the "F" (Fit) button from zooming out to include these lines
                
                // Default User UI Settings
                XmlFilePath                 = @"d:\futures_xml\PriceLevels.xml";
                ShowLabels                  = true;
                FontSize                    = 10;
            }
            else if (State == State.Configure)
            {
                // Sync the user-defined UI property with the internal field before loading
                xmlFilePath = XmlFilePath;  
            }
            else if (State == State.DataLoaded)
            {
                // Initialize the color dictionary. StringComparer.OrdinalIgnoreCase allows users 
                // to type "red", "Red", or "RED" in the XML without breaking the parser.
                brushMap = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase)
                {
                    {"Red", Brushes.Red}, {"Lime", Brushes.Lime}, {"LimeGreen", Brushes.LimeGreen},
                    {"Orange", Brushes.Orange}, {"DarkOrange", Brushes.DarkOrange}, {"Blue", Brushes.Blue},
                    {"Gold", Brushes.Gold}, {"Green", Brushes.Green}, {"DarkGreen", Brushes.DarkGreen},
                    {"White", Brushes.White}, {"Yellow", Brushes.Yellow}, {"Cyan", Brushes.Cyan},
                    {"Magenta", Brushes.Magenta}, {"Purple", Brushes.Purple}, {"Gray", Brushes.Gray},
                    {"Silver", Brushes.Silver}
                };
                
                // Trigger the XML parsing routine
                LoadPriceLevels();
            }
        }

        //=====================================================================
        // XML PARSER
        //=====================================================================
        
        /// <summary>
        /// Reads the external XML file, extracts attributes, and populates the priceLevels list.
        /// </summary>
        private void LoadPriceLevels()
        {
            priceLevels.Clear();  // Always clear the list before loading to prevent duplicates
            
            try
            {
                if (File.Exists(xmlFilePath))
                {
                    XmlDocument xmlDoc = new XmlDocument();
                    xmlDoc.Load(xmlFilePath);
                    
                    // Iterate through every <Level> node in the document
                    foreach (XmlNode node in xmlDoc.SelectNodes("//Level"))
                    {
                        LevelData lvl = new LevelData();
                        
                        // Parse attributes with safe fallbacks (?? operator) if the attribute is missing
                        lvl.Type = node.Attributes["type"]?.Value ?? "general";
                        
                        // Price Value is mandatory. If it fails to parse as a double, skip this entire level.
                        if (double.TryParse(node.Attributes["value"]?.Value, out double val))
                            lvl.Value = val;
                        else
                            continue;  
                        
                        // Process the color string into a usable Brush object immediately
                        string colorAttribute = node.Attributes["color"]?.Value ?? "Green";
                        lvl.LineBrush = GetBrushFromString(colorAttribute);

                        // Parse string attributes
                        lvl.Style = node.Attributes["style"]?.Value ?? "Solid";
                        lvl.Label = node.Attributes["label"]?.Value ?? "Level";
                        
                        // Parse thickness, default to 1 pixel if missing or invalid
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
                // Catch any malformed XML errors (like unescaped ampersands) and log them to the Output window
                Print("XML_PriceMarker Error parsing XML: " + ex.Message);
            }
        }

        //=====================================================================
        // MAIN DRAWING LOGIC (LINES ONLY)
        //=====================================================================
        
        /// <summary>
        /// Standard NinjaTrader drawing method. Used here ONLY to draw the horizontal lines.
        /// Text is handled separately in OnRender.
        /// </summary>
        protected override void OnBarUpdate()
        {
            // Safety check: ExtendedLines require at least 1 historical bar to calculate trajectory
            if (CurrentBar < 1 || priceLevels.Count == 0)
                return;

            foreach (var lvl in priceLevels)
            {
                DashStyleHelper dashStyle = GetDashStyle(lvl.Style);
                string uniqueTag = lvl.Label + "_" + lvl.Value;
                
                // Use ExtendedLine instead of HorizontalLine. 
                // Why? ExtendedLines bypass NinjaTrader's forced Y-Axis price marker behavior.
                // The '1' and '0' represent barsAgo anchors to establish the horizontal trajectory.
                ExtendedLine hLine = Draw.ExtendedLine(this, uniqueTag, false, 1, lvl.Value, 0, lvl.Value, lvl.LineBrush, dashStyle, (int)lvl.Thickness);
                
                // Explicitly tell the drawing object not to ruin the user's chart scaling
                hLine.IsAutoScale = false;
            }
        }

        //=====================================================================
        // ONRENDER (ADVANCED DIRECT-TO-SCREEN GRAPHICS)
        //=====================================================================
        
        /// <summary>
        /// Bypasses the standard charting timeline and draws graphics directly to the screen pixels.
        /// This is required to make text "sticky" to the left edge of the monitor regardless of scrolling.
        /// </summary>
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            // Always call the base method first, otherwise standard chart candles/lines won't draw
            base.OnRender(chartControl, chartScale);

            // Abort if the user turned off labels in UI or if there is no data
            if (!ShowLabels || priceLevels.Count == 0)
                return;

            // 1. Configure the DirectWrite Font Settings
            SharpDX.DirectWrite.TextFormat textFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory,
                "Arial",
                SharpDX.DirectWrite.FontWeight.Bold,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal,
                FontSize);

            // Loop through each level and paint the text
            foreach (var lvl in priceLevels)
            {
                // Y-AXIS POSITIONING
                // Convert the exact price value into a physical screen pixel coordinate
                float y = chartScale.GetYByValue(lvl.Value);

                // Shift the text UP (negative Y is up in screen coords) so it sits directly above the line.
                // Adding +2 pixels gives it a tiny breathing room so the text doesn't touch the line.
                y -= (FontSize + 2);

                // X-AXIS POSITIONING
                // Lock the text to the absolute left edge of the charting window (ChartPanel.X).
                // Add 10 pixels of padding so it isn't completely flush against the border.
                float x = ChartPanel.X + 10;

                // 2. Build the Text Layout engine for this specific string
                SharpDX.DirectWrite.TextLayout textLayout = new SharpDX.DirectWrite.TextLayout(
                    NinjaTrader.Core.Globals.DirectWriteFactory,
                    lvl.Label,
                    textFormat,
                    ChartPanel.W,         // Allow the text layout box to span the whole chart width
                    textFormat.FontSize);

                // 3. Convert our standard WPF LineBrush into a high-performance Direct2D Brush
                SharpDX.Direct2D1.Brush dxBrush = lvl.LineBrush.ToDxBrush(RenderTarget);

                // 4. Paint the text to the glass of the screen
                RenderTarget.DrawTextLayout(new SharpDX.Vector2(x, y), textLayout, dxBrush);

                // 5. Memory Management: Direct2D objects must be manually disposed to prevent memory leaks
                dxBrush.Dispose();
                textLayout.Dispose();
            }

            // Dispose of the master font formatter once the loop is finished
            textFormat.Dispose();
        }

        //=====================================================================
        // HELPER METHODS (COLOR & STYLE CONVERSION)
        //=====================================================================
        
        /// <summary>
        /// Safely converts a color string or Hex code into a usable WPF Brush.
        /// </summary>
        private Brush GetBrushFromString(string colorStr)
        {
            if (string.IsNullOrWhiteSpace(colorStr)) return Brushes.Green;

            // Check if it's a Hex code (e.g., "#FF5733" or "#00FFAA")
            if (colorStr.StartsWith("#"))
            {
                try
                {
                    Color customColor = (Color)ColorConverter.ConvertFromString(colorStr);
                    SolidColorBrush customBrush = new SolidColorBrush(customColor);
                    
                    // CRITICAL: Custom brushes in NT8 must be frozen to prevent cross-threading crashes
                    customBrush.Freeze(); 
                    return customBrush;
                }
                catch { return Brushes.Green; } // Fallback to green if Hex is malformed
            }

            // If not Hex, check our standard dictionary
            if (brushMap.TryGetValue(colorStr, out Brush mappedBrush))
                return mappedBrush;

            // Ultimate fallback if the user types a typo like "Gren"
            return Brushes.Green;
        }

        /// <summary>
        /// Translates string definitions from the XML into NinjaTrader DashStyle enums.
        /// </summary>
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
        [Display(Name = "Font Size", Description = "Size of the text labels", Order = 3, GroupName = "Display Settings")]
        public int FontSize { get; set; }
        
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private XML_PriceMarker[] cacheXML_PriceMarker;
		public XML_PriceMarker XML_PriceMarker(string xmlFilePath, bool showLabels, int fontSize)
		{
			return XML_PriceMarker(Input, xmlFilePath, showLabels, fontSize);
		}

		public XML_PriceMarker XML_PriceMarker(ISeries<double> input, string xmlFilePath, bool showLabels, int fontSize)
		{
			if (cacheXML_PriceMarker != null)
				for (int idx = 0; idx < cacheXML_PriceMarker.Length; idx++)
					if (cacheXML_PriceMarker[idx] != null && cacheXML_PriceMarker[idx].XmlFilePath == xmlFilePath && cacheXML_PriceMarker[idx].ShowLabels == showLabels && cacheXML_PriceMarker[idx].FontSize == fontSize && cacheXML_PriceMarker[idx].EqualsInput(input))
						return cacheXML_PriceMarker[idx];
			return CacheIndicator<XML_PriceMarker>(new XML_PriceMarker(){ XmlFilePath = xmlFilePath, ShowLabels = showLabels, FontSize = fontSize }, input, ref cacheXML_PriceMarker);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.XML_PriceMarker XML_PriceMarker(string xmlFilePath, bool showLabels, int fontSize)
		{
			return indicator.XML_PriceMarker(Input, xmlFilePath, showLabels, fontSize);
		}

		public Indicators.XML_PriceMarker XML_PriceMarker(ISeries<double> input , string xmlFilePath, bool showLabels, int fontSize)
		{
			return indicator.XML_PriceMarker(input, xmlFilePath, showLabels, fontSize);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.XML_PriceMarker XML_PriceMarker(string xmlFilePath, bool showLabels, int fontSize)
		{
			return indicator.XML_PriceMarker(Input, xmlFilePath, showLabels, fontSize);
		}

		public Indicators.XML_PriceMarker XML_PriceMarker(ISeries<double> input , string xmlFilePath, bool showLabels, int fontSize)
		{
			return indicator.XML_PriceMarker(input, xmlFilePath, showLabels, fontSize);
		}
	}
}

#endregion
