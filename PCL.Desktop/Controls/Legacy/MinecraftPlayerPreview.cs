// Copyright (c) MUXUE1230. All rights reserved.
// Modifications Copyright (c) 2026 PCL N contributors.
// Licensed under the Apache License, Version 2.0.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PCL.Desktop.Localization;

namespace PCL.Desktop.Controls.Legacy;

/// <summary>
/// Camera angles supported by <see cref="MinecraftPlayerPreview"/>.
/// </summary>
public enum MinecraftPlayerView
{
    Isometric,
    Front,
    Back,
    Left,
    Right,
    Top,
    Bottom
}

/// <summary>
/// Cross-platform, full-body Minecraft skin preview. The default axonometric
/// view renders every body part as a cuboid and keeps the transparent second
/// skin layer on a separately expanded surface.
/// </summary>
public sealed class MinecraftPlayerPreview : Grid
{
    public const double PreferredAspectRatio = 0.65d;

    public static readonly StyledProperty<string> SkinAddressProperty =
        AvaloniaProperty.Register<MinecraftPlayerPreview, string>(
            nameof(SkinAddress),
            string.Empty);

    public static readonly StyledProperty<string> CapeAddressProperty =
        AvaloniaProperty.Register<MinecraftPlayerPreview, string>(
            nameof(CapeAddress),
            string.Empty);

    public static readonly StyledProperty<bool> IsSlimProperty =
        AvaloniaProperty.Register<MinecraftPlayerPreview, bool>(nameof(IsSlim));

    public static readonly StyledProperty<MinecraftPlayerView> ViewProperty =
        AvaloniaProperty.Register<MinecraftPlayerPreview, MinecraftPlayerView>(
            nameof(View),
            MinecraftPlayerView.Isometric);

    public static readonly StyledProperty<bool> ShowViewSwitcherProperty =
        AvaloniaProperty.Register<MinecraftPlayerPreview, bool>(
            nameof(ShowViewSwitcher),
            true);

    private static readonly MinecraftPlayerView[] ViewOrder =
    [
        MinecraftPlayerView.Isometric,
        MinecraftPlayerView.Front,
        MinecraftPlayerView.Back,
        MinecraftPlayerView.Left,
        MinecraftPlayerView.Right,
        MinecraftPlayerView.Top,
        MinecraftPlayerView.Bottom
    ];

    private readonly PreviewRenderer _renderer;
    private readonly MyIconButton _viewButton;
    private Bitmap? _skin;
    private Bitmap? _cape;
    private int _loadVersion;
    private bool _languageEventAttached;

    public MinecraftPlayerPreview()
    {
        ClipToBounds = true;

        _renderer = new PreviewRenderer(this)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapInterpolationMode(_renderer, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(_renderer, EdgeMode.Aliased);
        Children.Add(_renderer);

        _viewButton = new MyIconButton
        {
            Width = 26d,
            Height = 26d,
            Margin = new Thickness(4d),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            SvgIcon = "lucide/refresh-cw",
            LogoScale = 0.78d,
            Theme = MyIconButton.Themes.Black
        };
        _viewButton.Click += (_, _) => View = GetNextView(View);
        Children.Add(_viewButton);
        _viewButton.SetValue(Panel.ZIndexProperty, 10);

        this.GetObservable(SkinAddressProperty).Subscribe(address =>
        {
            _ = ReloadAsync();
        });
        this.GetObservable(CapeAddressProperty).Subscribe(address =>
        {
            _ = ReloadAsync();
        });
        this.GetObservable(IsSlimProperty).Subscribe(_ => _renderer.InvalidateVisual());
        this.GetObservable(ViewProperty).Subscribe(_ =>
        {
            UpdateViewButton();
            _renderer.InvalidateVisual();
        });
        this.GetObservable(ShowViewSwitcherProperty).Subscribe(show =>
        {
            _viewButton.IsVisible = show;
            _renderer.InvalidateVisual();
        });
        SizeChanged += (_, _) => UpdateViewButtonSize();
        AttachedToVisualTree += (_, _) =>
        {
            if (!_languageEventAttached)
            {
                AvaloniaLocalizationManager.LanguageChanged += OnLanguageChanged;
                _languageEventAttached = true;
            }

            UpdateViewButton();
            if ((_skin is null && !string.IsNullOrWhiteSpace(SkinAddress)) ||
                (_cape is null && !string.IsNullOrWhiteSpace(CapeAddress)))
            {
                _ = ReloadAsync();
            }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_languageEventAttached)
            {
                AvaloniaLocalizationManager.LanguageChanged -= OnLanguageChanged;
                _languageEventAttached = false;
            }

            Interlocked.Increment(ref _loadVersion);
            DisposeBitmaps();
        };
        UpdateViewButton();
    }

    public string SkinAddress
    {
        get => GetValue(SkinAddressProperty);
        set => SetValue(SkinAddressProperty, value);
    }

    public string CapeAddress
    {
        get => GetValue(CapeAddressProperty);
        set => SetValue(CapeAddressProperty, value);
    }

    public bool IsSlim
    {
        get => GetValue(IsSlimProperty);
        set => SetValue(IsSlimProperty, value);
    }

    public MinecraftPlayerView View
    {
        get => GetValue(ViewProperty);
        set => SetValue(ViewProperty, value);
    }

    public bool ShowViewSwitcher
    {
        get => GetValue(ShowViewSwitcherProperty);
        set => SetValue(ShowViewSwitcherProperty, value);
    }

    private void RenderPlayer(DrawingContext context, Rect bounds)
    {
        if (bounds.Width <= 0d || bounds.Height <= 0d)
            return;

        int skinScale = _skin is null ? 1 : Math.Max(1, _skin.PixelSize.Width / 64);
        bool modernSkin = _skin is not null &&
                          _skin.PixelSize.Height >= 64 * skinScale;
        List<RenderFace> faces = BuildSkinFaces(IsSlim, modernSkin);
        if (_cape is not null)
            AddCapeFaces(faces);

        Point3 camera = GetCamera(View);
        List<RenderFace> visibleFaces = faces
            .Where(face => Dot(face.Normal, camera) > 0.0001d)
            .OrderBy(face => Dot(face.Center, camera))
            .ToList();
        if (visibleFaces.Count == 0)
            return;

        ProjectedBounds projected = CalculateProjectedBounds(visibleFaces, View);
        double padding = Math.Clamp(Math.Min(bounds.Width, bounds.Height) * 0.055d, 2d, 12d);
        double availableWidth = Math.Max(1d, bounds.Width - padding * 2d);
        double availableHeight = Math.Max(1d, bounds.Height - padding * 2d);
        double scale = Math.Min(
            availableWidth / Math.Max(0.001d, projected.Width),
            availableHeight / Math.Max(0.001d, projected.Height));
        double offsetX = (bounds.Width - projected.Width * scale) / 2d -
                         projected.Left * scale;
        double offsetY = (bounds.Height - projected.Height * scale) / 2d -
                         projected.Top * scale;

        DrawGroundShadow(context, View, scale, offsetX, offsetY);
        foreach (RenderFace face in visibleFaces)
            DrawFace(context, face, skinScale, scale, offsetX, offsetY);
    }

    internal static MinecraftPlayerView GetNextView(MinecraftPlayerView current)
    {
        int index = Array.IndexOf(ViewOrder, current);
        return ViewOrder[(index + 1 + ViewOrder.Length) % ViewOrder.Length];
    }

    internal static int CountVisibleSkinFaces(
        MinecraftPlayerView view,
        bool isSlim,
        bool includeSecondLayer)
    {
        Point3 camera = GetCamera(view);
        return BuildSkinFaces(isSlim, includeSecondLayer)
            .Count(face => Dot(face.Normal, camera) > 0.0001d);
    }

    internal static ProjectedBounds CalculateProjectedSkinBounds(
        MinecraftPlayerView view,
        bool isSlim,
        bool includeSecondLayer)
    {
        Point3 camera = GetCamera(view);
        List<RenderFace> visible = BuildSkinFaces(isSlim, includeSecondLayer)
            .Where(face => Dot(face.Normal, camera) > 0.0001d)
            .ToList();
        return CalculateProjectedBounds(visible, view);
    }

    private static List<RenderFace> BuildSkinFaces(bool isSlim, bool modernSkin)
    {
        List<RenderFace> faces = [];
        double armWidth = isSlim ? 3d : 4d;

        AddCuboidFaces(
            faces,
            new Box3(-4d, 24d, -4d, 4d, 32d, 4d),
            HeadBase,
            TextureKind.Skin,
            Color.FromRgb(151, 164, 184),
            isOuterLayer: false);
        AddCuboidFaces(
            faces,
            new Box3(-4d, 12d, -2d, 4d, 24d, 2d),
            TorsoBase,
            TextureKind.Skin,
            Color.FromRgb(103, 119, 145),
            isOuterLayer: false);
        AddCuboidFaces(
            faces,
            new Box3(-4d - armWidth, 12d, -2d, -4d, 24d, 2d),
            RightArmBase(isSlim),
            TextureKind.Skin,
            Color.FromRgb(111, 126, 151),
            isOuterLayer: false);
        AddCuboidFaces(
            faces,
            new Box3(4d, 12d, -2d, 4d + armWidth, 24d, 2d),
            modernSkin ? LeftArmBase(isSlim) : RightArmBase(isSlim),
            TextureKind.Skin,
            Color.FromRgb(111, 126, 151),
            isOuterLayer: false);
        AddCuboidFaces(
            faces,
            new Box3(-4d, 0d, -2d, 0d, 12d, 2d),
            RightLegBase,
            TextureKind.Skin,
            Color.FromRgb(82, 96, 119),
            isOuterLayer: false);
        AddCuboidFaces(
            faces,
            new Box3(0d, 0d, -2d, 4d, 12d, 2d),
            modernSkin ? LeftLegBase : RightLegBase,
            TextureKind.Skin,
            Color.FromRgb(82, 96, 119),
            isOuterLayer: false);

        if (!modernSkin)
            return faces;

        AddCuboidFaces(
            faces,
            new Box3(-4d, 24d, -4d, 4d, 32d, 4d).Inflate(0.5d),
            HeadOuter,
            TextureKind.Skin,
            Colors.Transparent,
            isOuterLayer: true);
        AddCuboidFaces(
            faces,
            new Box3(-4d, 12d, -2d, 4d, 24d, 2d).Inflate(0.25d),
            TorsoOuter,
            TextureKind.Skin,
            Colors.Transparent,
            isOuterLayer: true);
        AddCuboidFaces(
            faces,
            new Box3(-4d - armWidth, 12d, -2d, -4d, 24d, 2d).Inflate(0.25d),
            RightArmOuter(isSlim),
            TextureKind.Skin,
            Colors.Transparent,
            isOuterLayer: true);
        AddCuboidFaces(
            faces,
            new Box3(4d, 12d, -2d, 4d + armWidth, 24d, 2d).Inflate(0.25d),
            LeftArmOuter(isSlim),
            TextureKind.Skin,
            Colors.Transparent,
            isOuterLayer: true);
        AddCuboidFaces(
            faces,
            new Box3(-4d, 0d, -2d, 0d, 12d, 2d).Inflate(0.25d),
            RightLegOuter,
            TextureKind.Skin,
            Colors.Transparent,
            isOuterLayer: true);
        AddCuboidFaces(
            faces,
            new Box3(0d, 0d, -2d, 4d, 12d, 2d).Inflate(0.25d),
            LeftLegOuter,
            TextureKind.Skin,
            Colors.Transparent,
            isOuterLayer: true);
        return faces;
    }

    private static void AddCapeFaces(List<RenderFace> faces)
    {
        FaceTextures cape = new(
            NegativeX: new TextureRegion(0, 1, 1, 16),
            PositiveX: new TextureRegion(11, 1, 1, 16),
            Top: new TextureRegion(1, 0, 10, 1),
            Bottom: new TextureRegion(11, 0, 10, 1),
            Front: new TextureRegion(1, 1, 10, 16),
            Back: new TextureRegion(12, 1, 10, 16));
        AddCuboidFaces(
            faces,
            new Box3(-5d, 6.5d, -2.8d, 5d, 22.5d, -2.25d),
            cape,
            TextureKind.Cape,
            Color.FromRgb(90, 104, 128),
            isOuterLayer: false);
    }

    private static void AddCuboidFaces(
        List<RenderFace> faces,
        Box3 box,
        FaceTextures textures,
        TextureKind textureKind,
        Color fallbackColor,
        bool isOuterLayer)
    {
        AddFace(
            faces,
            new Point3(box.MinX, box.MaxY, box.MinZ),
            new Point3(box.MinX, box.MaxY, box.MaxZ),
            new Point3(box.MinX, box.MinY, box.MinZ),
            new Point3(-1d, 0d, 0d),
            textures.NegativeX,
            textureKind,
            fallbackColor,
            isOuterLayer);
        AddFace(
            faces,
            new Point3(box.MaxX, box.MaxY, box.MaxZ),
            new Point3(box.MaxX, box.MaxY, box.MinZ),
            new Point3(box.MaxX, box.MinY, box.MaxZ),
            new Point3(1d, 0d, 0d),
            textures.PositiveX,
            textureKind,
            fallbackColor,
            isOuterLayer);
        AddFace(
            faces,
            new Point3(box.MinX, box.MaxY, box.MinZ),
            new Point3(box.MaxX, box.MaxY, box.MinZ),
            new Point3(box.MinX, box.MaxY, box.MaxZ),
            new Point3(0d, 1d, 0d),
            textures.Top,
            textureKind,
            fallbackColor,
            isOuterLayer);
        AddFace(
            faces,
            new Point3(box.MinX, box.MinY, box.MaxZ),
            new Point3(box.MaxX, box.MinY, box.MaxZ),
            new Point3(box.MinX, box.MinY, box.MinZ),
            new Point3(0d, -1d, 0d),
            textures.Bottom,
            textureKind,
            fallbackColor,
            isOuterLayer);
        AddFace(
            faces,
            new Point3(box.MinX, box.MaxY, box.MaxZ),
            new Point3(box.MaxX, box.MaxY, box.MaxZ),
            new Point3(box.MinX, box.MinY, box.MaxZ),
            new Point3(0d, 0d, 1d),
            textures.Front,
            textureKind,
            fallbackColor,
            isOuterLayer);
        AddFace(
            faces,
            new Point3(box.MaxX, box.MaxY, box.MinZ),
            new Point3(box.MinX, box.MaxY, box.MinZ),
            new Point3(box.MaxX, box.MinY, box.MinZ),
            new Point3(0d, 0d, -1d),
            textures.Back,
            textureKind,
            fallbackColor,
            isOuterLayer);
    }

    private static void AddFace(
        List<RenderFace> faces,
        Point3 topLeft,
        Point3 topRight,
        Point3 bottomLeft,
        Point3 normal,
        TextureRegion texture,
        TextureKind textureKind,
        Color fallbackColor,
        bool isOuterLayer)
    {
        Point3 bottomRight = topRight + bottomLeft - topLeft;
        faces.Add(new RenderFace(
            topLeft,
            topRight,
            bottomLeft,
            normal,
            (topLeft + topRight + bottomLeft + bottomRight) / 4d,
            texture,
            textureKind,
            fallbackColor,
            isOuterLayer));
    }

    private void DrawFace(
        DrawingContext context,
        RenderFace face,
        int skinScale,
        double scale,
        double offsetX,
        double offsetY)
    {
        Point p0 = ToCanvas(Project(face.TopLeft, View), scale, offsetX, offsetY);
        Point p1 = ToCanvas(Project(face.TopRight, View), scale, offsetX, offsetY);
        Point p3 = ToCanvas(Project(face.BottomLeft, View), scale, offsetX, offsetY);
        Matrix transform = new(
            p1.X - p0.X,
            p1.Y - p0.Y,
            p3.X - p0.X,
            p3.Y - p0.Y,
            p0.X,
            p0.Y);

        Bitmap? bitmap = face.TextureKind switch
        {
            TextureKind.Skin => _skin,
            TextureKind.Cape => _cape,
            _ => null
        };
        int textureScale = face.TextureKind == TextureKind.Cape && _cape is not null
            ? Math.Max(1, _cape.PixelSize.Width / 64)
            : skinScale;
        bool sourceValid = bitmap is not null &&
                           face.Texture.Right * textureScale <= bitmap.PixelSize.Width &&
                           face.Texture.Bottom * textureScale <= bitmap.PixelSize.Height;

        using (context.PushTransform(transform))
        {
            if (sourceValid && bitmap is not null)
            {
                Rect source = new(
                    face.Texture.X * textureScale,
                    face.Texture.Y * textureScale,
                    face.Texture.Width * textureScale,
                    face.Texture.Height * textureScale);
                context.DrawImage(bitmap, source, new Rect(0d, 0d, 1d, 1d));
            }
            else if (!face.IsOuterLayer)
            {
                context.FillRectangle(
                    new SolidColorBrush(face.FallbackColor),
                    new Rect(0d, 0d, 1d, 1d));
            }

            if (!face.IsOuterLayer)
            {
                Color shade = GetShade(face.Normal);
                if (shade.A > 0)
                {
                    context.FillRectangle(
                        new SolidColorBrush(shade),
                        new Rect(0d, 0d, 1d, 1d));
                }
            }
        }
    }

    private static void DrawGroundShadow(
        DrawingContext context,
        MinecraftPlayerView view,
        double scale,
        double offsetX,
        double offsetY)
    {
        if (view is MinecraftPlayerView.Top or MinecraftPlayerView.Bottom)
            return;

        Point center = ToCanvas(Project(new Point3(0d, -0.45d, 0d), view), scale, offsetX, offsetY);
        context.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(34, 0, 0, 0)),
            null,
            center,
            6.5d * scale,
            Math.Max(0.75d, 1.1d * scale));
    }

    private static Color GetShade(Point3 normal)
    {
        if (normal.Y > 0.5d)
            return Color.FromArgb(16, 255, 255, 255);
        if (normal.Y < -0.5d)
            return Color.FromArgb(58, 0, 0, 0);
        if (normal.Z < -0.5d)
            return Color.FromArgb(32, 0, 0, 0);
        if (Math.Abs(normal.X) > 0.5d)
            return Color.FromArgb(20, 0, 0, 0);
        return Colors.Transparent;
    }

    private static ProjectedBounds CalculateProjectedBounds(
        IReadOnlyList<RenderFace> faces,
        MinecraftPlayerView view)
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        foreach (RenderFace face in faces)
        {
            Include(face.TopLeft);
            Include(face.TopRight);
            Include(face.BottomLeft);
            Include(face.TopRight + face.BottomLeft - face.TopLeft);
        }

        return new ProjectedBounds(minX, minY, maxX, maxY);

        void Include(Point3 point)
        {
            Point projected = Project(point, view);
            minX = Math.Min(minX, projected.X);
            minY = Math.Min(minY, projected.Y);
            maxX = Math.Max(maxX, projected.X);
            maxY = Math.Max(maxY, projected.Y);
        }
    }

    private static Point Project(Point3 point, MinecraftPlayerView view) =>
        view switch
        {
            MinecraftPlayerView.Isometric =>
                new Point(
                    0.866025403784d * (point.X - point.Z),
                    0.5d * (point.X + point.Z) - point.Y),
            MinecraftPlayerView.Front => new Point(point.X, -point.Y),
            MinecraftPlayerView.Back => new Point(-point.X, -point.Y),
            MinecraftPlayerView.Left => new Point(-point.Z, -point.Y),
            MinecraftPlayerView.Right => new Point(point.Z, -point.Y),
            MinecraftPlayerView.Top => new Point(point.X, point.Z),
            MinecraftPlayerView.Bottom => new Point(point.X, -point.Z),
            _ => new Point(point.X, -point.Y)
        };

    private static Point3 GetCamera(MinecraftPlayerView view) =>
        view switch
        {
            MinecraftPlayerView.Isometric => new Point3(1d, 1d, 1d),
            MinecraftPlayerView.Front => new Point3(0d, 0d, 1d),
            MinecraftPlayerView.Back => new Point3(0d, 0d, -1d),
            MinecraftPlayerView.Left => new Point3(1d, 0d, 0d),
            MinecraftPlayerView.Right => new Point3(-1d, 0d, 0d),
            MinecraftPlayerView.Top => new Point3(0d, 1d, 0d),
            MinecraftPlayerView.Bottom => new Point3(0d, -1d, 0d),
            _ => new Point3(0d, 0d, 1d)
        };

    private static Point ToCanvas(Point point, double scale, double offsetX, double offsetY) =>
        new(point.X * scale + offsetX, point.Y * scale + offsetY);

    private static double Dot(Point3 left, Point3 right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private void UpdateViewButtonSize()
    {
        double shortest = Math.Min(Bounds.Width, Bounds.Height);
        double size = Math.Clamp(shortest * 0.2d, 20d, 28d);
        _viewButton.Width = size;
        _viewButton.Height = size;
        _viewButton.Margin = new Thickness(Math.Clamp(size * 0.16d, 3d, 5d));
    }

    private void UpdateViewButton()
    {
        string viewKey = View switch
        {
            MinecraftPlayerView.Isometric => "Appearance.Preview.View.Isometric",
            MinecraftPlayerView.Front => "Appearance.Preview.View.Front",
            MinecraftPlayerView.Back => "Appearance.Preview.View.Back",
            MinecraftPlayerView.Left => "Appearance.Preview.View.Left",
            MinecraftPlayerView.Right => "Appearance.Preview.View.Right",
            MinecraftPlayerView.Top => "Appearance.Preview.View.Top",
            MinecraftPlayerView.Bottom => "Appearance.Preview.View.Bottom",
            _ => "Appearance.Preview.View.Front"
        };
        string viewName = AvaloniaLocalizationManager.GetText(
            viewKey,
            View.ToString());
        string format = AvaloniaLocalizationManager.GetText(
            "Appearance.Preview.SwitchView",
            "当前视图：{0}。点击切换视图");
        _viewButton.ToolTip = string.Format(
            AvaloniaLocalizationManager.CurrentFormatCulture,
            format,
            viewName);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => UpdateViewButton();

    private async Task ReloadAsync()
    {
        int version = Interlocked.Increment(ref _loadVersion);
        string skinAddress = SkinAddress.Trim();
        string capeAddress = CapeAddress.Trim();
        Task<byte[]?> skinTask = string.IsNullOrWhiteSpace(skinAddress)
            ? Task.FromResult<byte[]?>(null)
            : MySkin.LoadSkinBytesAsync(skinAddress);
        Task<byte[]?> capeTask = string.IsNullOrWhiteSpace(capeAddress)
            ? Task.FromResult<byte[]?>(null)
            : MySkin.LoadSkinBytesAsync(capeAddress);

        byte[]?[] bytes;
        try
        {
            bytes = await Task.WhenAll(skinTask, capeTask).ConfigureAwait(false);
        }
        catch
        {
            bytes = [null, null];
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _loadVersion)
                return;

            Bitmap? nextSkin = CreateBitmap(bytes[0]);
            Bitmap? nextCape = CreateBitmap(bytes[1]);
            DisposeBitmaps();
            _skin = nextSkin;
            _cape = nextCape;
            _renderer.InvalidateVisual();
        });
    }

    private static Bitmap? CreateBitmap(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 64)
            return null;

        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            Bitmap bitmap = new(stream);
            if (bitmap.PixelSize.Width < 64 || bitmap.PixelSize.Height < 32)
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void DisposeBitmaps()
    {
        _skin?.Dispose();
        _cape?.Dispose();
        _skin = null;
        _cape = null;
    }

    private sealed class PreviewRenderer(MinecraftPlayerPreview owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            owner.RenderPlayer(context, Bounds);
        }
    }

    private static FaceTextures HeadBase => new(
        NegativeX: new TextureRegion(0, 8, 8, 8),
        PositiveX: new TextureRegion(16, 8, 8, 8),
        Top: new TextureRegion(8, 0, 8, 8),
        Bottom: new TextureRegion(16, 0, 8, 8),
        Front: new TextureRegion(8, 8, 8, 8),
        Back: new TextureRegion(24, 8, 8, 8));

    private static FaceTextures HeadOuter => new(
        NegativeX: new TextureRegion(32, 8, 8, 8),
        PositiveX: new TextureRegion(48, 8, 8, 8),
        Top: new TextureRegion(40, 0, 8, 8),
        Bottom: new TextureRegion(48, 0, 8, 8),
        Front: new TextureRegion(40, 8, 8, 8),
        Back: new TextureRegion(56, 8, 8, 8));

    private static FaceTextures TorsoBase => new(
        NegativeX: new TextureRegion(16, 20, 4, 12),
        PositiveX: new TextureRegion(28, 20, 4, 12),
        Top: new TextureRegion(20, 16, 8, 4),
        Bottom: new TextureRegion(28, 16, 8, 4),
        Front: new TextureRegion(20, 20, 8, 12),
        Back: new TextureRegion(32, 20, 8, 12));

    private static FaceTextures TorsoOuter => new(
        NegativeX: new TextureRegion(16, 36, 4, 12),
        PositiveX: new TextureRegion(28, 36, 4, 12),
        Top: new TextureRegion(20, 32, 8, 4),
        Bottom: new TextureRegion(28, 32, 8, 4),
        Front: new TextureRegion(20, 36, 8, 12),
        Back: new TextureRegion(32, 36, 8, 12));

    private static FaceTextures RightArmBase(bool slim) =>
        LimbTextures(40, 16, slim ? 3 : 4);

    private static FaceTextures RightArmOuter(bool slim) =>
        LimbTextures(40, 32, slim ? 3 : 4);

    private static FaceTextures LeftArmBase(bool slim) =>
        LimbTextures(32, 48, slim ? 3 : 4);

    private static FaceTextures LeftArmOuter(bool slim) =>
        LimbTextures(48, 48, slim ? 3 : 4);

    private static FaceTextures RightLegBase =>
        LimbTextures(0, 16, 4);

    private static FaceTextures RightLegOuter =>
        LimbTextures(0, 32, 4);

    private static FaceTextures LeftLegBase =>
        LimbTextures(16, 48, 4);

    private static FaceTextures LeftLegOuter =>
        LimbTextures(0, 48, 4);

    private static FaceTextures LimbTextures(
        int originX,
        int originY,
        int limbWidth)
    {
        return new FaceTextures(
            NegativeX: new TextureRegion(originX, originY + 4, 4, 12),
            PositiveX: new TextureRegion(originX + 4 + limbWidth, originY + 4, 4, 12),
            Top: new TextureRegion(originX + 4, originY, limbWidth, 4),
            Bottom: new TextureRegion(originX + 4 + limbWidth, originY, limbWidth, 4),
            Front: new TextureRegion(originX + 4, originY + 4, limbWidth, 12),
            Back: new TextureRegion(originX + 8 + limbWidth, originY + 4, limbWidth, 12));
    }

    private enum TextureKind
    {
        Skin,
        Cape
    }

    private readonly record struct Point3(double X, double Y, double Z)
    {
        public static Point3 operator +(Point3 left, Point3 right) =>
            new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        public static Point3 operator -(Point3 left, Point3 right) =>
            new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

        public static Point3 operator /(Point3 point, double divisor) =>
            new(point.X / divisor, point.Y / divisor, point.Z / divisor);
    }

    private readonly record struct Box3(
        double MinX,
        double MinY,
        double MinZ,
        double MaxX,
        double MaxY,
        double MaxZ)
    {
        public Box3 Inflate(double amount) =>
            new(
                MinX - amount,
                MinY - amount,
                MinZ - amount,
                MaxX + amount,
                MaxY + amount,
                MaxZ + amount);
    }

    private readonly record struct TextureRegion(
        int X,
        int Y,
        int Width,
        int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;
    }

    private readonly record struct FaceTextures(
        TextureRegion NegativeX,
        TextureRegion PositiveX,
        TextureRegion Top,
        TextureRegion Bottom,
        TextureRegion Front,
        TextureRegion Back);

    private readonly record struct RenderFace(
        Point3 TopLeft,
        Point3 TopRight,
        Point3 BottomLeft,
        Point3 Normal,
        Point3 Center,
        TextureRegion Texture,
        TextureKind TextureKind,
        Color FallbackColor,
        bool IsOuterLayer);
}

internal readonly record struct ProjectedBounds(
    double Left,
    double Top,
    double Right,
    double Bottom)
{
    public double Width => Right - Left;

    public double Height => Bottom - Top;
}
