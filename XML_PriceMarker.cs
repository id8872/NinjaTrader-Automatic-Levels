#region Using declarations
// System namespaces for basic functionality
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Xml;

// Windows namespaces for UI elements
using System.Windows;           // Required for TextAlignment enum
using System.Windows.Media;     // Required for Brushes and colors

// NinjaTrader namespaces for trading platform integration
using NinjaTrader.Gui;          // Required for DashStyleHelper enum
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;  // Required for drawing methods
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    /// <summary>
    /// XML_PriceMarker - Loads price levels from an XML file and draws them on the chart.
    /// Supports custom colors, line styles, thickness, and intelligent text positioning.
    /// </summary>
    public class XML_PriceMarker : Indicator
    {
        //=====================================================================
        // PRIVATE FIELDS
        //=====================================================================
        /// <summary>List to hold all price levels loaded from XML</summary>
        private List<LevelData> priceLevels = new List<LevelData>();

        /// <summary>Maps color names to actual Brush objects for quick lookup</summary>
        private Dictionary<string, Brush> brushMap;

        /// <summary>Path to the XML file containing price levels</summary>
        private string xmlFilePath = @"d:\futures_xml\Examples.xml";

        //=====================================================================
        // NESTED CLASS: LevelData
        //=====================================================================
        /// <summary>
        /// Represents a single price level from the XML file.
        /// Stores all properties needed to draw the line and label.
        /// </summary>
        public class LevelData
        {
            public string Type;      // Level type (support, resistance, etc.)
            public double Value;     // Price value of the level
            public string Color;     // Color name from XML
            public string Style;     // Line style (Solid, Dash, Dot, etc.)
            public string Label;     // Text label to display
            public double Thickness; // Line thickness in pixels
        }

        //=====================================================================
        // STATE MANAGEMENT
        //=====================================================================
        /// <summary>
        /// Called when indicator state changes. Sets up defaults, loads data, and initializes resources.
        /// </summary>
        protected override void OnStateChange()
        {
            //---------------------------------------------------------
            // SetDefaults: Configure indicator identity and defaults
            //---------------------------------------------------------
            if (State == State.SetDefaults)
            {
                Description = @"Plots price levels from XML file with full style support.";
                Name = "XML_PriceMarker";
                Calculate = Calculate.OnBarClose;  // Only calculate on bar close for performance
                IsOverlay = true;                  // Draw on price panel, not separate panel
                DisplayInDataBox = true;
                DrawOnPricePanel = true;
                PaintPriceMarkers = true;
                ScaleJustification = ScaleJustification.Right;
                IsSuspendedWhileInactive = true;

                // FIX: Prevent indicator from affecting chart auto-scale
                // This ensures the "F" (Fit) button ignores these lines
                IsAutoScale = false;

                // Default user-configurable properties
                XmlFilePath = @"d:\futures_xml\PriceLevels.xml";
                TextOffsetTicks = 20;
                ShowLabels = true;
                FontSize = 10;
            }

            //---------------------------------------------------------
            // Configure: Apply user settings before data load
            //---------------------------------------------------------
            else if (State == State.Configure)
            {
                xmlFilePath = XmlFilePath;  // Sync private field with property
            }

            //---------------------------------------------------------
            // DataLoaded: Initialize resources after data is loaded
            //---------------------------------------------------------
            else if (State == State.DataLoaded)
            {
                // Initialize color mapping dictionary (case-insensitive)
                brushMap = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase)
                {
                    {"Red", Brushes.Red},
                    {"Lime", Brushes.Lime},
                    {"LimeGreen", Brushes.LimeGreen},
                    {"Orange", Brushes.Orange},
                    {"DarkOrange", Brushes.DarkOrange},
                    {"Blue", Brushes.Blue},
                    {"Gold", Brushes.Gold},
                    {"Green", Brushes.Green},
                    {"DarkGreen", Brushes.DarkGreen},
                    {"White", Brushes.White},
                    {"Yellow", Brushes.Yellow},
                    {"Cyan", Brushes.Cyan},
                    {"Magenta", Brushes.Magenta},
                    {"Purple", Brushes.Purple},
                    {"Gray", Brushes.Gray},
                    {"Silver", Brushes.Silver}
                };

                // Load and parse the XML file
                LoadPriceLevels();
            }
        }

        //=====================================================================
        // XML LOADING
        //=====================================================================
        /// <summary>
        /// Loads price levels from the XML file specified in xmlFilePath.
        /// Parses each Level node and populates the priceLevels list.
        /// </summary>
        private void LoadPriceLevels()
        {
            priceLevels.Clear();  // Clear existing levels before reloading

            try
            {
                if (File.Exists(xmlFilePath))
                {
                    XmlDocument xmlDoc = new XmlDocument();
                    xmlDoc.Load(xmlFilePath);

                    // Select all Level nodes anywhere in the document
                    foreach (XmlNode node in xmlDoc.SelectNodes("//Level"))
                    {
                        LevelData lvl = new LevelData();

                        // Parse type attribute (default: "general")
                        lvl.Type = node.Attributes["type"]?.Value ?? "general";

                        // Parse value attribute (price level) - skip if invalid
                        if (double.TryParse(node.Attributes["value"]?.Value, out double val))
                            lvl.Value = val;
                        else
                            continue;  // Skip levels with invalid price values

                        // Parse optional attributes with defaults
                        lvl.Color = node.Attributes["color"]?.Value ?? "Green";
                        lvl.Style = node.Attributes["style"]?.Value ?? "Solid";
                        lvl.Label = node.Attributes["label"]?.Value ?? "Level";

                        // Parse thickness with fallback to 1
                        if (double.TryParse(node.Attributes["thickness"]?.Value, out double thickness))
                            lvl.Thickness = thickness;
                        else
                            lvl.Thickness = 1;

                        priceLevels.Add(lvl);
                    }

                    Print($"Loaded {priceLevels.Count} price levels from: {xmlFilePath}");
                }
                else
                {
                    Print($"XML file not found: {xmlFilePath}");
                }
            }
            catch (Exception ex)
            {
                Print("Error loading XML Levels: " + ex.Message);
            }
        }

        //=====================================================================
        // MAIN DRAWING LOGIC
        //=====================================================================
        /// <summary>
        /// Called on each bar update. Draws all price level lines and labels.
        /// </summary>
        protected override void OnBarUpdate()
        {
            // Skip if no levels to draw
            if (priceLevels.Count == 0)
                return;

            // Draw each price level
            foreach (var lvl in priceLevels)
            {
                // Get the brush color for this level (default to Green if not found)
                brushMap.TryGetValue(lvl.Color, out Brush lineColor);
                lineColor = lineColor ?? Brushes.Green;

                // Convert style string to DashStyleHelper enum
                DashStyleHelper dashStyle = GetDashStyle(lvl.Style);

                // Create a unique tag for this drawing object
                string uniqueTag = lvl.Label + "_" + lvl.Value;

                // Draw the horizontal line
                // FIX: isAutoScale = false (7th parameter) prevents "F" button from zooming to this line
                HorizontalLine hLine = Draw.HorizontalLine(this, uniqueTag, lvl.Value, lineColor, dashStyle, (int)lvl.Thickness, false);

                // EXTRA SAFETY: Explicitly disable auto-scale on the drawing object
                hLine.IsAutoScale = false;

                // Calculate text offset based on level type
                double yOffset = CalculateTextOffset(lvl);

                // Draw text label if enabled
                if (ShowLabels)
                {
                    Text textLabel = Draw.Text(this, uniqueTag + "_Text", true, lvl.Label, 0, lvl.Value + yOffset, 0,
                        lineColor, new SimpleFont("Arial", FontSize), TextAlignment.Left,
                        Brushes.Transparent, Brushes.Black, 100);

                    // EXTRA SAFETY: Disable auto-scale on text labels too
                    textLabel.IsAutoScale = false;
                }
            }
        }

        //=====================================================================
        // STYLE CONVERSION
        //=====================================================================
        /// <summary>
        /// Converts a style string from XML to the appropriate DashStyleHelper enum value.
        /// Supports multiple common names for each style.
        /// </summary>
        /// <param name="style">Style string from XML (e.g., "dash", "dotted")</param>
        /// <returns>DashStyleHelper enum value</returns>
        // Returns NinjaTrader.Gui.DashStyleHelper enum
        private DashStyleHelper GetDashStyle(string style)
        {
            if (string.IsNullOrEmpty(style))
                return DashStyleHelper.Solid;

            switch (style.ToLower())
            {
                case "dash":
                case "dashed":
                    return DashStyleHelper.Dash;
                case "dot":
                case "dotted":
                    return DashStyleHelper.Dot;
                case "dashdot":
                case "dash-dot":
                    return DashStyleHelper.DashDot;
                case "dashdotdot":
                case "dash-dot-dot":
                    return DashStyleHelper.DashDotDot;
                case "solid":
                default:
                    return DashStyleHelper.Solid;
            }
        }

        //=====================================================================
        // TEXT POSITIONING LOGIC
        //=====================================================================
        /// <summary>
        /// Calculates the vertical offset for text labels based on level type.
        /// Places support labels below, resistance above, and others at different positions.
        /// </summary>
        /// <param name="lvl">The level data containing type and label information</param>
        /// <returns>Offset in price units (not ticks)</returns>
        private double CalculateTextOffset(LevelData lvl)
        {
            string typeLower = lvl.Type.ToLower();
            string labelLower = lvl.Label.ToLower();

            // Rules for text positioning:
            // - Support/Floor: Below line (negative offset)
            // - Resistance/Target/High/Breakout: Above line (positive offset)
            // - Current/Alert/Pivot: Slightly above (half offset)
            // - Others: On the line (no offset)

            if (typeLower.Contains("support") || labelLower.Contains("support") || labelLower.Contains("floor"))
                return -TickSize * TextOffsetTicks;  // Below line

            else if (typeLower.Contains("resistance") || labelLower.Contains("resistance") ||
                     typeLower.Contains("target") || labelLower.Contains("target") ||
                     labelLower.Contains("high") || labelLower.Contains("breakout"))
                return TickSize * TextOffsetTicks;  // Above line

            else if (typeLower.Contains("current") || labelLower.Contains("current") ||
                     typeLower.Contains("alert") || typeLower.Contains("pivot"))
                return TickSize * (TextOffsetTicks / 2);  // Slightly above

            else
                return 0;  // On the line
        }

        //=====================================================================
        // USER-CONFIGURABLE PROPERTIES
        //=====================================================================
        #region Properties

        /// <summary>
        /// Full path to the XML file containing price levels.
        /// Must be accessible by NinjaTrader (check file permissions).
        /// </summary>
        [NinjaScriptProperty]
        [Display(Name = "XML File Path", Description = "Full path to the XML file containing price levels", Order = 1, GroupName = "File Settings")]
        public string XmlFilePath { get; set; }

        /// <summary>
        /// Number of ticks to offset text labels above or below the line.
        /// Larger values move text further away.
        /// </summary>
        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Text Offset (Ticks)", Description = "Number of ticks to offset text above/below the line", Order = 2, GroupName = "Display Settings")]
        public int TextOffsetTicks { get; set; }

        /// <summary>
        /// Toggle to show or hide text labels on price levels.
        /// </summary>
        [NinjaScriptProperty]
        [Display(Name = "Show Labels", Description = "Show/hide text labels on price levels", Order = 3, GroupName = "Display Settings")]
        public bool ShowLabels { get; set; }

        /// <summary>
        /// Font size for text labels (6-24 range).
        /// </summary>
        [NinjaScriptProperty]
        [Range(6, 24)]
        [Display(Name = "Font Size", Description = "Size of the text labels", Order = 4, GroupName = "Display Settings")]
        public int FontSize { get; set; }

        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.
// This region is automatically generated by NinjaTrader. Do not modify.

namespace NinjaTrader.NinjaScript.Indicators
{
    public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
    {
        private XML_PriceMarker[] cacheXML_PriceMarker;

        /// <summary>Overloaded method for creating the indicator</summary>
        public XML_PriceMarker XML_PriceMarker(string xmlFilePath, int textOffsetTicks, bool showLabels, int fontSize)
        {
            return XML_PriceMarker(Input, xmlFilePath, textOffsetTicks, showLabels, fontSize);
        }

        /// <summary>Overloaded method with input series</summary>
        public XML_PriceMarker XML_PriceMarker(ISeries<double> input, string xmlFilePath, int textOffsetTicks, bool showLabels, int fontSize)
        {
            if (cacheXML_PriceMarker != null)
                for (int idx = 0; idx < cacheXML_PriceMarker.Length; idx++)
                    if (cacheXML_PriceMarker[idx] != null && cacheXML_PriceMarker[idx].XmlFilePath == xmlFilePath && cacheXML_PriceMarker[idx].TextOffsetTicks == textOffsetTicks && cacheXML_PriceMarker[idx].ShowLabels == showLabels && cacheXML_PriceMarker[idx].FontSize == fontSize && cacheXML_PriceMarker[idx].EqualsInput(input))
                        return cacheXML_PriceMarker[idx];
            return CacheIndicator<XML_PriceMarker>(new XML_PriceMarker() { XmlFilePath = xmlFilePath, TextOffsetTicks = textOffsetTicks, ShowLabels = showLabels, FontSize = fontSize }, input, ref cacheXML_PriceMarker);
        }
    }
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
    public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
    {
        public Indicators.XML_PriceMarker XML_PriceMarker(string xmlFilePath, int textOffsetTicks, bool showLabels, int fontSize)
        {
            return indicator.XML_PriceMarker(Input, xmlFilePath, textOffsetTicks, showLabels, fontSize);
        }

        public Indicators.XML_PriceMarker XML_PriceMarker(ISeries<double> input, string xmlFilePath, int textOffsetTicks, bool showLabels, int fontSize)
        {
            return indicator.XML_PriceMarker(input, xmlFilePath, textOffsetTicks, showLabels, fontSize);
        }
    }
}

namespace NinjaTrader.NinjaScript.Strategies
{
    public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
    {
        public Indicators.XML_PriceMarker XML_PriceMarker(string xmlFilePath, int textOffsetTicks, bool showLabels, int fontSize)
        {
            return indicator.XML_PriceMarker(Input, xmlFilePath, textOffsetTicks, showLabels, fontSize);
        }

        public Indicators.XML_PriceMarker XML_PriceMarker(ISeries<double> input, string xmlFilePath, int textOffsetTicks, bool showLabels, int fontSize)
        {
            return indicator.XML_PriceMarker(input, xmlFilePath, textOffsetTicks, showLabels, fontSize);
        }
    }
}

#endregion