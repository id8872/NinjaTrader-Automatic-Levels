# XML Price Marker for NinjaTrader 8

**Loads unlimited styled price levels from XML and plots them on your chart without affecting auto-scaling.**

## Features

  * **XML-Driven:** Maintain price levels in an external XML file; no code changes needed.
  * **Full Customization:** Per-level control of color, line style (`Solid`, `Dash`, `Dot`, `DashDot`, `DashDotDot`), thickness, and labels.
  * **Smart Positioning:** Labels auto-position based on level type:
      * *Support* = Below
      * *Resistance* = Above
      * *Pivot* = Centered
  * **Zero Chart Interference:** Press **"F"** to fit chart and lines are ignored for perfect price scaling.
  * **Performance:** Efficient single-load design with minimal chart impact.

## Requirements

  * NinjaTrader 8
  * .NET Framework 4.5+
  * Read access to XML file location

## Installation

1.  Copy `XML_PriceMarker.cs` to:
    `Documents\NinjaTrader 8\bin\Custom\Indicators\`
2.  Open NinjaTrader and navigate to **Tools** → **Edit NinjaScript** → **Indicator**.
3.  Select **"XML\_PriceMarker"** and click **Compile**.
4.  Add the indicator to your chart.

## XML File Format

Create a file (e.g., `PriceLevels.xml`) with the following structure:

```xml
<Levels>
  <Level type="support" value="4500.00" color="Green" style="Solid" thickness="2" label="Daily Support"/>
  <Level type="floor" value="4485.50" color="LimeGreen" style="Dash" thickness="1" label="Weekly Floor"/>

  <Level type="resistance" value="4550.00" color="Red" style="Solid" thickness="2" label="R1"/>
  <Level type="target" value="4565.00" color="Orange" style="Dash" thickness="1" label="Target 1"/>

  <Level type="pivot" value="4525.00" color="Blue" style="Dot" thickness="1" label="Daily Pivot"/>
</Levels>
```

## Attributes

| Attribute | Required | Default | Description |
| :--- | :---: | :--- | :--- |
| **value** | Yes | - | Price level (must be a valid number) |
| **type** | No | "general" | Used for smart text positioning |
| **color** | No | "Green" | Color name (see supported list below) |
| **style** | No | "Solid" | Line style: `Solid`, `Dash`, `Dot`, `DashDot`, `DashDotDot` |
| **thickness** | No | 1 | Line thickness in pixels |
| **label** | No | "Level" | Text label to display |

> **Supported Colors:**
> Red, Lime, LimeGreen, Orange, DarkOrange, Blue, Gold, Green, DarkGreen, White, Yellow, Cyan, Magenta, Purple, Gray, Silver

## Configuration

Right-click chart → **Indicators** → Select **"XML\_PriceMarker"** → **Configure**:

  * **XML File Path:** Full path to your XML file (default: `d:\futures_xml\PriceLevels.xml`)
  * **Text Offset (Ticks):** Distance above/below line for labels (0-100)
  * **Show Labels:** Toggle labels on/off
  * **Font Size:** Label font size (6-24pt)

## Usage Tips

  * **Reload Levels:** Press **F5** to reload the chart after editing the XML file.
  * **Auto-Fit:** Press **F** to fit the chart; lines will be ignored for perfect scaling.
  * **Text Position:** Use `type="support"` or `type="resistance"` for automatic above/below positioning.
  * **File Permissions:** Ensure NinjaTrader has read access to the XML file location.

## Troubleshooting

  * **"XML file not found":** Verify the file path exists and is accessible.
  * **"Error loading XML Levels":** Check XML syntax and ensure all `value` attributes are valid numbers.
  * **Lines not appearing:** Confirm `IsOverlay = true` and check if levels are within the visible price range.

## Changelog

  * **v1.0** - Initial release with full XML support, smart positioning, and auto-fit fix.
