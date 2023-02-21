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
using System.Windows.Media.Media3D;
using NinjaTrader.NinjaScript.Indicators.Gemify;
#endregion

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators.Gemify
{
    public enum OrderLineDecoratorDisplayValue
    {
        TICKS,
        CURRENCY,
        BOTH
    }

    public class OrderLineDecorator : Indicator
	{		
        // For simplicity, we'll bunch orders as either stops or targets
		protected enum OrderLineDecoratorOrderType
        {
            STOP,
            TARGET
        }

        class OrderTypeAndText
		{ 
			public OrderLineDecoratorOrderType orderType;
            public String text;
		}

		private Account gAccount;
		private AccountSelector gAccountSelector;
		private Dictionary<string, int> orderQtyTracker;
        private Dictionary<double, OrderTypeAndText> toRender;

        private SharpDX.DirectWrite.TextFormat textFormat;

		private bool IsDebug;
		private void Debug(String message)
		{
			if (IsDebug) Print(message);
		}

		protected override void OnStateChange()
		{
			Debug("OrderLineDecorator: >>>>>>> " + State);

			if (State == State.SetDefaults)
			{
				Description = @"Order Line Decorator";
				Name = "\"OrderLineDecorator\"";
				Calculate = Calculate.OnPriceChange;
				IsOverlay = true;
				DisplayInDataBox = true;
				DrawOnPricePanel = true;
				DrawHorizontalGridLines = true;
				DrawVerticalGridLines = true;
				PaintPriceMarkers = true;
				ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				//Disable this property if your indicator requires custom values that cumulate with each new market data event. 
				//See Help Guide for additional information.
				IsSuspendedWhileInactive = true;

				// Default values
				IsDebug = true;
                Font = new SimpleFont("Arial", 12);
				DisplayMode = OrderLineDecoratorDisplayValue.BOTH;

				StopFillBrush = Brushes.Maroon;
				TargetFillBrush = Brushes.DarkGreen;
				OutlineBrush = Brushes.AliceBlue;
				TextBrush = Brushes.White;

            }
			else if (State == State.Configure)
			{
				orderQtyTracker = new Dictionary<string, int>();
                toRender = new Dictionary<double, OrderTypeAndText>();

            }
			else if (State == State.DataLoaded)
			{
                textFormat = Font.ToDirectWriteTextFormat();
            }
		}

		protected override void OnBarUpdate()
		{
			// We only care about realtime positions
			if (State == State.Historical) return;

			// Get the account for which we're monitoring positions
			ChartControl.Dispatcher.InvokeAsync((Action)(() =>
			{
				gAccountSelector = Window.GetWindow(ChartControl.Parent).FindFirst("ChartTraderControlAccountSelector") as NinjaTrader.Gui.Tools.AccountSelector;
                gAccount = gAccountSelector.SelectedAccount;
			}));

			// Nothing to do if we can't find the selected account
			if (gAccount == null) return;

			// Reset order text and positions
			orderQtyTracker.Clear();
			toRender.Clear();

			// Process only if we have positions
			foreach (Position p in gAccount.Positions)
			{
				// If the position is in current instrument
				if (p.Instrument == Instrument && p.MarketPosition != MarketPosition.Flat)
				{
					// Get our position size and entry price
					double entryPrice = p.AveragePrice;
					double positionSize = p.Quantity;

					// Check every order in selected account
					foreach (Order order in gAccount.Orders)
					{
						// Ignore order if it's for a different instrument
						if (order.Instrument != Instrument) continue;

						// We're only concerned with "Accepted" / "Working" orders
						if ((order.OrderState == OrderState.Accepted || order.OrderState == OrderState.Working) &&
							// We're only concerned with Stop Loss and Target orders (ie, ignore scale-in orders)
							(p.MarketPosition == MarketPosition.Long && !order.IsLong || p.MarketPosition == MarketPosition.Short && !order.IsShort))
						{
							// Only considering stop price at this time
							double orderPrice = GetOrderPrice(order);
							if (orderPrice == 0)
							{
								Debug("Unsupported order type. [" + order.OrderType + "] Skipping.");
								continue;
							}
							string key = orderPrice + order.OrderType.ToString();

							// Attempt to count orders of the same type and same price 
							if (orderQtyTracker.ContainsKey(key))
							{
								orderQtyTracker[key] = orderQtyTracker[key] + order.Quantity;
							}
							else
							{
								orderQtyTracker.Add(key, order.Quantity);
							}

							// Use aggregated order quantity
							int orderQty = orderQtyTracker[key];

							// Calculate ticks and currency value from entry
							double priceDiff = (p.MarketPosition == MarketPosition.Long ? orderPrice - entryPrice : entryPrice - orderPrice);
							int ticks = (int)(priceDiff / TickSize);
							double currencyValue = priceDiff * Instrument.MasterInstrument.PointValue * orderQty;

							// Generate text for decoration
							string orderType = order.IsStopLimit || order.IsStopMarket ? "STOP" : "TARGET";
							string text = orderType + " (" + orderQty + ") " + (
                                (DisplayMode == OrderLineDecoratorDisplayValue.TICKS || DisplayMode == OrderLineDecoratorDisplayValue.BOTH ? ((order.IsStopLimit || order.IsStopMarket) && ticks > 0 ? "+" : "") + ticks + " ticks" : "") +
                                (DisplayMode == OrderLineDecoratorDisplayValue.BOTH ? " : " : "") + 
                                (DisplayMode == OrderLineDecoratorDisplayValue.CURRENCY || DisplayMode == OrderLineDecoratorDisplayValue.BOTH ? currencyValue.ToString("C2") : "")
								);

							// Store order type and text against order price. This will be picked up and rendered by the OnRender call
							toRender[orderPrice] = new OrderTypeAndText() { orderType = (order.IsStopLimit || order.IsStopMarket ? OrderLineDecoratorOrderType.STOP : OrderLineDecoratorOrderType.TARGET), text = text };
						}
					}
				}
			}

			// Request a refresh
            ForceRefresh();
        }

        private double GetOrderPrice(Order order)
		{
			double orderPrice = 0;

			// Only operates on Stop Limits, Stop Markets and Market if Touched - used as stops
			if (order.IsStopLimit || order.IsStopMarket || order.IsMarketIfTouched) orderPrice = order.StopPrice;
			// and Limit exits
			else if (order.IsLimit) orderPrice = order.LimitPrice;

			return orderPrice;

		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
			if (State == State.Historical || toRender.IsNullOrEmpty()) return;

			// TODO:
			// Need to figure out how to calculate the offset given the OrderDisplayBarLength property.
			float THIS_VAR_IS_DRIVING_ME_NUTS = 190;

            using (SharpDX.Direct2D1.Brush borderBrushDx = OutlineBrush.ToDxBrush(RenderTarget))
            using (SharpDX.Direct2D1.Brush stopBrushDx = StopFillBrush.ToDxBrush(RenderTarget))
            using (SharpDX.Direct2D1.Brush targetBrushDx = TargetFillBrush.ToDxBrush(RenderTarget))
            using (SharpDX.Direct2D1.Brush textBrushDx = TextBrush.ToDxBrush(RenderTarget))
            {
                foreach (KeyValuePair<double, OrderTypeAndText> kvp in toRender)
				{
					SharpDX.DirectWrite.TextLayout textLayout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, kvp.Value.text, textFormat, ChartPanel.X + ChartPanel.W, textFormat.FontSize);

                    float textWidth = textLayout.Metrics.Width;
					float textHeight = textLayout.Metrics.Height;

                    float x = (float)(ChartPanel.W - (ChartPanel.W * ((ChartControl.OwnerChart.ChartTrader.Properties.OrderDisplayBarLength)/100.0)) - textWidth - THIS_VAR_IS_DRIVING_ME_NUTS);
					int priceCoordinate = chartScale.GetYByValue(kvp.Key);
					float y = priceCoordinate - ((textHeight + 7) / 2);

                    SharpDX.Vector2 startPoint = new SharpDX.Vector2(x, y);
                    SharpDX.Vector2 upperTextPoint = new SharpDX.Vector2(startPoint.X + 4, startPoint.Y + 3);
                    SharpDX.Vector2 lineStartPoint = new SharpDX.Vector2(startPoint.X + textWidth + 9, priceCoordinate);
                    SharpDX.Vector2 lineEndPoint = new SharpDX.Vector2(ChartPanel.W, priceCoordinate);

                    SharpDX.RectangleF rect = new SharpDX.RectangleF(startPoint.X, startPoint.Y, textWidth + 8, textHeight + 6);
                    RenderTarget.FillRectangle(rect, kvp.Value.orderType == OrderLineDecoratorOrderType.STOP ? stopBrushDx : targetBrushDx);
                    RenderTarget.DrawRectangle(rect, borderBrushDx, 1);
					RenderTarget.DrawLine(lineStartPoint, lineEndPoint, borderBrushDx);

                    RenderTarget.DrawTextLayout(upperTextPoint, textLayout, textBrushDx, SharpDX.Direct2D1.DrawTextOptions.NoSnap);
					textLayout.Dispose();
				}

            }
        }

        #region Parameters
        [NinjaScriptProperty]
        [Display(Name = "Font", GroupName = "Parameters", Order = 100)]
        public SimpleFont Font
        { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Display", GroupName = "Parameters", Order = 200)]
        public OrderLineDecoratorDisplayValue DisplayMode
        { get; set; }


        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Stop Fill Color", Order = 100, GroupName = "Colors")]
        public Brush StopFillBrush
        { get; set; }

        [Browsable(false)]
        public string StopFillBrushSerializable
        {
            get { return Serialize.BrushToString(StopFillBrush); }
            set { StopFillBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Target Fill Color", Order = 200, GroupName = "Colors")]
        public Brush TargetFillBrush
        { get; set; }

        [Browsable(false)]
        public string TargetFillBrushSerializable
        {
            get { return Serialize.BrushToString(TargetFillBrush); }
            set { TargetFillBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Outline Color", Order = 300, GroupName = "Colors")]
        public Brush OutlineBrush
        { get; set; }

        [Browsable(false)]
        public string OutlineBrushSerializable
        {
            get { return Serialize.BrushToString(OutlineBrush); }
            set { OutlineBrush = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Text Color", Order = 400, GroupName = "Colors")]
        public Brush TextBrush
        { get; set; }

        [Browsable(false)]
        public string TextBrushSerializable
        {
            get { return Serialize.BrushToString(TextBrush); }
            set { TextBrush = Serialize.StringToBrush(value); }
        }

        #endregion
    }

}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private Gemify.OrderLineDecorator[] cacheOrderLineDecorator;
		public Gemify.OrderLineDecorator OrderLineDecorator(SimpleFont font, OrderLineDecoratorDisplayValue displayMode, Brush stopFillBrush, Brush targetFillBrush, Brush outlineBrush, Brush textBrush)
		{
			return OrderLineDecorator(Input, font, displayMode, stopFillBrush, targetFillBrush, outlineBrush, textBrush);
		}

		public Gemify.OrderLineDecorator OrderLineDecorator(ISeries<double> input, SimpleFont font, OrderLineDecoratorDisplayValue displayMode, Brush stopFillBrush, Brush targetFillBrush, Brush outlineBrush, Brush textBrush)
		{
			if (cacheOrderLineDecorator != null)
				for (int idx = 0; idx < cacheOrderLineDecorator.Length; idx++)
					if (cacheOrderLineDecorator[idx] != null && cacheOrderLineDecorator[idx].Font == font && cacheOrderLineDecorator[idx].DisplayMode == displayMode && cacheOrderLineDecorator[idx].StopFillBrush == stopFillBrush && cacheOrderLineDecorator[idx].TargetFillBrush == targetFillBrush && cacheOrderLineDecorator[idx].OutlineBrush == outlineBrush && cacheOrderLineDecorator[idx].TextBrush == textBrush && cacheOrderLineDecorator[idx].EqualsInput(input))
						return cacheOrderLineDecorator[idx];
			return CacheIndicator<Gemify.OrderLineDecorator>(new Gemify.OrderLineDecorator(){ Font = font, DisplayMode = displayMode, StopFillBrush = stopFillBrush, TargetFillBrush = targetFillBrush, OutlineBrush = outlineBrush, TextBrush = textBrush }, input, ref cacheOrderLineDecorator);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.Gemify.OrderLineDecorator OrderLineDecorator(SimpleFont font, OrderLineDecoratorDisplayValue displayMode, Brush stopFillBrush, Brush targetFillBrush, Brush outlineBrush, Brush textBrush)
		{
			return indicator.OrderLineDecorator(Input, font, displayMode, stopFillBrush, targetFillBrush, outlineBrush, textBrush);
		}

		public Indicators.Gemify.OrderLineDecorator OrderLineDecorator(ISeries<double> input , SimpleFont font, OrderLineDecoratorDisplayValue displayMode, Brush stopFillBrush, Brush targetFillBrush, Brush outlineBrush, Brush textBrush)
		{
			return indicator.OrderLineDecorator(input, font, displayMode, stopFillBrush, targetFillBrush, outlineBrush, textBrush);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.Gemify.OrderLineDecorator OrderLineDecorator(SimpleFont font, OrderLineDecoratorDisplayValue displayMode, Brush stopFillBrush, Brush targetFillBrush, Brush outlineBrush, Brush textBrush)
		{
			return indicator.OrderLineDecorator(Input, font, displayMode, stopFillBrush, targetFillBrush, outlineBrush, textBrush);
		}

		public Indicators.Gemify.OrderLineDecorator OrderLineDecorator(ISeries<double> input , SimpleFont font, OrderLineDecoratorDisplayValue displayMode, Brush stopFillBrush, Brush targetFillBrush, Brush outlineBrush, Brush textBrush)
		{
			return indicator.OrderLineDecorator(input, font, displayMode, stopFillBrush, targetFillBrush, outlineBrush, textBrush);
		}
	}
}

#endregion
