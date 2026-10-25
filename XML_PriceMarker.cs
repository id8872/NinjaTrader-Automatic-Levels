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
        // LIFECYCLE MANAGEMENT
        //=====================================================================
        
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description                 = @"Plots price levels from XML file with screen-locked text.";
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
                
                // Default User UI Settings
                XmlFilePath                 = @"G:\My Drive\price_levels.xml";
                ShowLabels                  = true;
                FontSize                    = 10;
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
        // MAIN DRAWING LOGIC (DRAWN ONCE)
        //=====================================================================
        
        protected override void OnBarUpdate()
        {
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

            if (!ShowLabels || priceLevels.Count == 0 || RenderTarget == null || ChartPanel == null)
                return;

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
