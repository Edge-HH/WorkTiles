using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WorkTiles.Controls;

/// <summary>对后方实际内容进行高斯模糊，保留前景文字清晰度。</summary>
public sealed class BlurBackdropBrush : XamlCompositionBrushBase
{
    private CompositionEffectBrush? _effect;
    private CompositionBackdropBrush? _backdrop;
    private double _amount;
    private Windows.UI.Color _tint;

    public double Amount => _amount;

    public BlurBackdropBrush(double amount, Windows.UI.Color? tint = null)
    {
        _amount = Math.Clamp(amount, 0, 100);
        _tint = tint ?? Microsoft.UI.Colors.Transparent;
        if (tint is Windows.UI.Color color) FallbackColor = color;
    }

    /// <summary>拖动模糊或透明度时只改效果参数。换一支新画笔会让背景先闪成黑色。</summary>
    public void Update(double amount, Windows.UI.Color? tint = null)
    {
        _amount = Math.Clamp(amount, 0, 100);
        if (tint is Windows.UI.Color color)
        {
            _tint = color;
            FallbackColor = color;
        }
        ApplyProperties();
    }

    protected override void OnConnected()
    {
        if (CompositionBrush is not null) return;
        Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
        using GaussianBlurEffect effect = new()
        {
            Name = "Blur",
            BlurAmount = (float)_amount,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("backdrop")
        };
        // 颜色盖在模糊结果上；没有颜色时保持全透明，仍然保留完整的模糊源。
        using ColorSourceEffect tintEffect = new() { Name = "Tint", Color = _tint };
        using CompositeEffect composite = new()
        {
            Mode = Microsoft.Graphics.Canvas.CanvasComposite.SourceOver,
            Sources = { effect, tintEffect }
        };
        using CompositionEffectFactory factory = compositor.CreateEffectFactory(composite, ["Blur.BlurAmount", "Tint.Color"]);
        _effect = factory.CreateBrush();
        _backdrop = compositor.CreateBackdropBrush();
        _effect.SetSourceParameter("backdrop", _backdrop);
        ApplyProperties();
        CompositionBrush = _effect;
    }

    protected override void OnDisconnected()
    {
        _effect?.Dispose();
        _effect = null;
        CompositionBrush = null;
        _backdrop?.Dispose();
        _backdrop = null;
    }

    private void ApplyProperties()
    {
        if (_effect is null) return;
        _effect.Properties.InsertScalar("Blur.BlurAmount", (float)_amount);
        _effect.Properties.InsertColor("Tint.Color", _tint);
    }
}

/// <summary>
/// 桌面控制窗的颜色层：模糊窗口后方的内容，再叠上背景色。
/// 画在圆角背景上，因此跟随圆角，也跟随控件透明度一起隐藏。边缘用软采样，避免模糊向外扩出一圈。
/// </summary>
public sealed class DesktopGlassBrush : XamlCompositionBrushBase
{
    private CompositionEffectBrush? _effect;
    private CompositionBackdropBrush? _backdrop;
    private double _amount;
    private Windows.UI.Color _tint;

    public double Amount => _amount;

    public DesktopGlassBrush(double amount, Windows.UI.Color? tint = null)
    {
        _amount = Math.Clamp(amount, 0, 100);
        _tint = tint ?? Microsoft.UI.Colors.Transparent;
        if (tint is Windows.UI.Color color) FallbackColor = color;
    }

    /// <summary>拖动模糊或透明度时只改效果参数，避免重建桌面模糊时闪黑。</summary>
    public void Update(double amount, Windows.UI.Color? tint = null)
    {
        _amount = Math.Clamp(amount, 0, 100);
        if (tint is Windows.UI.Color color)
        {
            _tint = color;
            FallbackColor = color;
        }
        ApplyProperties();
    }

    protected override void OnConnected()
    {
        if (CompositionBrush is not null) return;
        Compositor compositor = CompositionTarget.GetCompositorForCurrentThread();
        using GaussianBlurEffect blur = new()
        {
            Name = "Blur",
            BlurAmount = (float)_amount,
            BorderMode = EffectBorderMode.Soft,
            Source = new CompositionEffectSourceParameter("backdrop")
        };
        using ColorSourceEffect tintEffect = new() { Name = "Tint", Color = _tint };
        using CompositeEffect composite = new()
        {
            Mode = Microsoft.Graphics.Canvas.CanvasComposite.SourceOver,
            Sources = { blur, tintEffect }
        };
        using CompositionEffectFactory factory = compositor.CreateEffectFactory(composite, ["Blur.BlurAmount", "Tint.Color"]);
        _effect = factory.CreateBrush();
        _backdrop = compositor.CreateBackdropBrush();
        _effect.SetSourceParameter("backdrop", _backdrop);
        ApplyProperties();
        CompositionBrush = _effect;
    }

    protected override void OnDisconnected()
    {
        _effect?.Dispose();
        _effect = null;
        CompositionBrush = null;
        _backdrop?.Dispose();
        _backdrop = null;
    }

    private void ApplyProperties()
    {
        if (_effect is null) return;
        _effect.Properties.InsertScalar("Blur.BlurAmount", (float)_amount);
        _effect.Properties.InsertColor("Tint.Color", _tint);
    }
}
