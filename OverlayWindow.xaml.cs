using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ChestCounter;

/// <summary>Click-through, always-on-top counter in the top-left corner.</summary>
public partial class OverlayWindow : Window
{
    const double ShadowMargin = 18;
    static readonly Color Gold = Color.FromRgb(0xFF, 0xD5, 0x4F);
    static readonly Color Red = Color.FromRgb(0xFF, 0x6B, 0x6B);

    readonly DispatcherTimer topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer bannerTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly Brush labelBrush;
    int gameNumber = 1;

    public OverlayWindow()
    {
        InitializeComponent();
        labelBrush = CountLabel.Foreground;
        bannerTimer.Tick += (_, _) =>
        {
            bannerTimer.Stop();
            CountLabel.Text = $"GAME {gameNumber}";
            CountLabel.Foreground = labelBrush;
        };
        SourceInitialized += (_, _) => MakeClickThrough();
        // Games in borderless mode can steal the top spot; keep reclaiming it.
        topmostTimer.Tick += (_, _) => KeepOnTop();
        topmostTimer.Start();
    }

    IntPtr Handle => new WindowInteropHelper(this).Handle;

    void MakeClickThrough()
    {
        long ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    void KeepOnTop()
    {
        if (!IsVisible || Handle == IntPtr.Zero) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void ApplyLayout(AppSettings s)
    {
        RootScale.ScaleX = RootScale.ScaleY = s.Scale;
        Root.Opacity = s.Opacity;
        Left = s.OffsetX - ShadowMargin;
        Top = s.OffsetY - ShadowMargin;
    }

    public void SetCount(AppSettings s, CountChange change)
    {
        CountText.Text = s.Count.ToString();
        gameNumber = s.Games.Count + 1;

        StatsPanel.Visibility = s.Games.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AvgText.Text = s.Average.ToString("0.#");
        BestText.Text = s.Best.ToString();
        TotalText.Text = s.Total.ToString();

        if (change == CountChange.Bank)
        {
            GameRecord saved = s.Games[^1];
            CountLabel.Text = $"SAVED: {saved.Chests} IN GAME {s.Games.Count}";
            CountLabel.Foreground = new SolidColorBrush(Gold);
            bannerTimer.Stop();
            bannerTimer.Start();
        }
        else if (!bannerTimer.IsEnabled)
        {
            CountLabel.Text = $"GAME {gameNumber}";
        }

        switch (change)
        {
            case CountChange.Add: PlayAdd(); break;
            case CountChange.Remove: PlayRemove(); break;
            case CountChange.Reset: PlayReset(); break;
            case CountChange.Bank: PlayReset(); PlayGlow(); break;
        }
    }

    public sealed record FriendRow(string Name, string Detail, int Count, Brush Dot, double Opacity);

    static readonly Brush OnlineDot = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    static readonly Brush OfflineDot = new SolidColorBrush(Color.FromRgb(0x6B, 0x70, 0x80));

    public void SetFriends(IReadOnlyList<FriendInfo> friends)
    {
        var rows = new List<FriendRow>();
        foreach (FriendInfo f in friends)
        {
            string detail = $"GAME {f.Games + 1}" + (f.Games > 0 ? $" · AVG {f.Avg:0.#}" : "");
            rows.Add(new FriendRow(f.Name, detail, f.Count, f.Online ? OnlineDot : OfflineDot, f.Online ? 1.0 : 0.55));
        }
        FriendsList.ItemsSource = rows;
    }

    void PlayGlow() =>
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, Ms(1200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });

    static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));

    void Scale(ScaleTransform t, double from, int ms, IEasingFunction ease)
    {
        var a = new DoubleAnimation(from, 1, Ms(ms)) { EasingFunction = ease };
        t.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    void PlayAdd()
    {
        Scale(CountScale, 1.45, 420, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
        Scale(ChestScale, 1.22, 380, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });

        var wiggle = new DoubleAnimationUsingKeyFrames { Duration = Ms(460) };
        wiggle.KeyFrames.Add(new EasingDoubleKeyFrame(-12, KeyTime.FromPercent(0.18)));
        wiggle.KeyFrames.Add(new EasingDoubleKeyFrame(9, KeyTime.FromPercent(0.42)));
        wiggle.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromPercent(0.68)));
        wiggle.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        ChestRotate.BeginAnimation(RotateTransform.AngleProperty, wiggle);

        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, Ms(900))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });
        CountBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Gold, Colors.White, Ms(700)));
    }

    void PlayRemove()
    {
        Scale(CountScale, 0.8, 300, new QuadraticEase { EasingMode = EasingMode.EaseOut });
        CountBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Red, Colors.White, Ms(600)));
    }

    void PlayReset()
    {
        CountText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(350)));
        Scale(CountScale, 0.6, 350, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });
    }
}
