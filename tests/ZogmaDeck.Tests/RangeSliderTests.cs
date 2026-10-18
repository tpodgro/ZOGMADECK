using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using ZogmaDeck.Controls;

namespace ZogmaDeck.Tests;

public class RangeSliderTests
{
    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception(error.Message, error);
    }

    private static double Left(RangeSlider s, string field) =>
        Canvas.GetLeft((Thumb)typeof(RangeSlider).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(s)!);

    private sealed class Source : ViewModels.ObservableObject
    {
        private double _max = 1, _low = 0.8, _high = 1;
        public double Max { get => _max; set => Set(ref _max, value); }
        public double Low { get => _low; set => Set(ref _low, value); }
        public double High { get => _high; set => Set(ref _high, value); }
    }

    [Fact]
    public void Thumbs_FollowBoundValues_AfterMaximumChanges()
    {
        OnSta(() =>
        {
            var src = new Source();
            var s = new RangeSlider { DataContext = src };
            s.SetBinding(RangeSlider.MaximumProperty, new Binding(nameof(Source.Max)));
            s.SetBinding(RangeSlider.LowerValueProperty, new Binding(nameof(Source.Low)) { Mode = BindingMode.TwoWay });
            s.SetBinding(RangeSlider.UpperValueProperty, new Binding(nameof(Source.High)) { Mode = BindingMode.TwoWay });
            s.Measure(new Size(318, 40));
            s.Arrange(new Rect(0, 0, 318, 32));
            src.Max = 3;      // durée chargée après coup
            src.High = 2.1;
            s.UpdateLayout();
            Assert.Equal(0.8, s.LowerValue, 3);
            Assert.Equal(2.1, s.UpperValue, 3);
            Assert.Equal(0.8 / 3 * 300, Left(s, "_lower"), 1);
            Assert.Equal(2.1 / 3 * 300, Left(s, "_upper"), 1);
        });
    }

    [Fact]
    public void ValuesSetBeforeFirstLayout_ArePlacedCorrectly_AndNeverWrittenBack()
    {
        OnSta(() =>
        {
            var src = new Source { Max = 3, Low = 0.8, High = 2.1 };
            var writes = 0;
            src.PropertyChanged += (_, _) => writes++;
            var s = new RangeSlider { DataContext = src };
            s.SetBinding(RangeSlider.MaximumProperty, new Binding(nameof(Source.Max)));
            s.SetBinding(RangeSlider.LowerValueProperty, new Binding(nameof(Source.Low)) { Mode = BindingMode.TwoWay });
            s.SetBinding(RangeSlider.UpperValueProperty, new Binding(nameof(Source.High)) { Mode = BindingMode.TwoWay });
            s.Measure(new Size(318, 40));
            s.Arrange(new Rect(0, 0, 318, 32));
            s.UpdateLayout();
            Assert.Equal(0.8 / 3 * 300, Left(s, "_lower"), 1);
            Assert.Equal(2.1 / 3 * 300, Left(s, "_upper"), 1);
            Assert.Equal(0, writes);
            Assert.Equal(0.8, src.Low);
            Assert.Equal(2.1, src.High);
        });
    }
}
