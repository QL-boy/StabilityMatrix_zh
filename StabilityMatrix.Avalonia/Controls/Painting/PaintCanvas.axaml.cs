﻿using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.PanAndZoom;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Skia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DynamicData.Binding;
using SkiaSharp;
using StabilityMatrix.Avalonia.Controls.Models;
using StabilityMatrix.Avalonia.ViewModels.Controls;

namespace StabilityMatrix.Avalonia.Controls;

public class PaintCanvas : TemplatedControl
{
    private ConcurrentDictionary<long, PenPath> TemporaryPaths => ViewModel!.TemporaryPaths;

    private ImmutableList<PenPath> Paths
    {
        get => ViewModel!.Paths;
        set => ViewModel!.Paths = value;
    }

    private IDisposable? viewModelSubscription;

    private bool isPenDown;

    private PaintCanvasViewModel? ViewModel { get; set; }

    private SkiaCustomCanvas? MainCanvas { get; set; }

    static PaintCanvas()
    {
        AffectsRender<PaintCanvas>(BoundsProperty);
    }

    public SKImage GetCanvasSnapshot()
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)Bounds.Width, (int)Bounds.Height));
        using var canvas = surface.Canvas;

        RenderCanvasCore(canvas);

        return surface.Snapshot();
    }

    public void RefreshCanvas()
    {
        Dispatcher.UIThread.Post(() => MainCanvas?.InvalidateVisual(), DispatcherPriority.Render);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        MainCanvas = e.NameScope.Find<SkiaCustomCanvas>("PART_MainCanvas");

        Debug.Assert(MainCanvas != null);

        if (MainCanvas is not null)
        {
            // If we already have a BackgroundBitmap, scale MainCanvas to match
            if (DataContext is PaintCanvasViewModel { BackgroundImage: { } backgroundBitmap })
            {
                MainCanvas.Width = backgroundBitmap.Width;
                MainCanvas.Height = backgroundBitmap.Height;
            }

            MainCanvas.RenderSkia += OnRenderSkia;
            MainCanvas.PointerEntered += MainCanvas_OnPointerEntered;
            MainCanvas.PointerExited += MainCanvas_OnPointerExited;
        }

        var zoomBorder = e.NameScope.Find<ZoomBorder>("PART_ZoomBorder");
        if (zoomBorder is not null)
        {
            zoomBorder.ZoomChanged += (_, zoomEventArgs) =>
            {
                if (ViewModel is not null)
                {
                    ViewModel.CurrentZoom = zoomEventArgs.ZoomX;

                    UpdateCanvasCursor();
                }
            };

            if (ViewModel is not null)
            {
                ViewModel.CurrentZoom = zoomBorder.ZoomX;

                UpdateCanvasCursor();
            }
        }

        OnDataContextChanged(EventArgs.Empty);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is PaintCanvasViewModel viewModel)
        {
            // Set the remote actions
            viewModel.GetCanvasSnapshot = GetCanvasSnapshot;
            viewModel.RefreshCanvas = RefreshCanvas;

            viewModelSubscription?.Dispose();
            viewModelSubscription = viewModel
                .WhenPropertyChanged(vm => vm.BackgroundImage)
                .Subscribe(change =>
                {
                    if (MainCanvas is not null && change.Value is not null)
                    {
                        MainCanvas.Width = change.Value.Width;
                        MainCanvas.Height = change.Value.Height;
                        MainCanvas.InvalidateVisual();
                    }
                });

            ViewModel = viewModel;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsEnabledProperty)
        {
            var newIsEnabled = change.GetNewValue<bool>();

            if (!newIsEnabled)
            {
                isPenDown = false;
            }

            // On any enabled change, flush temporary paths
            if (!TemporaryPaths.IsEmpty)
            {
                Paths = Paths.AddRange(TemporaryPaths.Values);
                TemporaryPaths.Clear();
            }
        }
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        UpdateMainCanvasBounds();
    }

    private void HandlePointerEvent(PointerEventArgs e)
    {
        // Ignore if disabled
        if (!IsEnabled)
        {
            return;
        }

        if (e.RoutedEvent == PointerReleasedEvent && e.Pointer.Type == PointerType.Touch)
        {
            TemporaryPaths.TryRemove(e.Pointer.Id, out _);
            return;
        }

        e.Handled = true;

        // Must have this or stylus inputs lost after a while
        // https://github.com/AvaloniaUI/Avalonia/issues/12289#issuecomment-1695620412
        e.PreventGestureRecognition();

        if (DataContext is not PaintCanvasViewModel viewModel)
        {
            return;
        }

        var currentPoint = e.GetCurrentPoint(this);

        if (e.RoutedEvent == PointerPressedEvent)
        {
            // Ignore if mouse and not left button
            if (e.Pointer.Type == PointerType.Mouse && !currentPoint.Properties.IsLeftButtonPressed)
            {
                return;
            }

            isPenDown = true;

            HandlePointerMoved(e);
        }
        else if (e.RoutedEvent == PointerReleasedEvent)
        {
            if (isPenDown)
            {
                HandlePointerMoved(e);

                isPenDown = false;
            }

            if (TemporaryPaths.TryGetValue(e.Pointer.Id, out var path))
            {
                Paths = Paths.Add(path);
            }

            TemporaryPaths.TryRemove(e.Pointer.Id, out _);
        }
        else
        {
            // Moved event
            if (!isPenDown || currentPoint.Properties.Pressure == 0)
            {
                return;
            }

            HandlePointerMoved(e);
        }

        Dispatcher.UIThread.Post(() => MainCanvas?.InvalidateVisual(), DispatcherPriority.Render);
    }

    private void HandlePointerMoved(PointerEventArgs e)
    {
        if (DataContext is not PaintCanvasViewModel viewModel)
        {
            return;
        }

        // Use intermediate points to include past events we missed
        var points = e.GetIntermediatePoints(MainCanvas);

        Debug.WriteLine($"Points: {string.Join(",", points.Select(p => p.Position.ToString()))}");

        if (points.Count == 0)
        {
            return;
        }

        viewModel.CurrentPenPressure = points.FirstOrDefault().Properties.Pressure;

        // Get or create a temp path
        if (!TemporaryPaths.TryGetValue(e.Pointer.Id, out var penPath))
        {
            penPath = new PenPath
            {
                FillColor = viewModel.PaintBrushSKColor.WithAlpha((byte)(viewModel.PaintBrushAlpha * 255))
            };
            TemporaryPaths[e.Pointer.Id] = penPath;
        }

        // Add line for path
        // var cursorPosition = e.GetPosition(MainCanvas);
        // penPath.Path.LineTo(cursorPosition.ToSKPoint());

        // Add points
        foreach (var point in points)
        {
            var penPoint = new PenPoint(point.Position.X, point.Position.Y)
            {
                Pressure = point.Pointer.Type == PointerType.Mouse ? null : point.Properties.Pressure,
                Radius = viewModel.PaintBrushSize,
                IsPen = point.Pointer.Type == PointerType.Pen
            };

            penPath.Points.Add(penPoint);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        HandlePointerEvent(e);
        base.OnPointerPressed(e);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        HandlePointerEvent(e);
        base.OnPointerReleased(e);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        HandlePointerEvent(e);
        base.OnPointerMoved(e);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Update the bounds of the main canvas to match the background image
    /// </summary>
    private void UpdateMainCanvasBounds()
    {
        if (
            MainCanvas is null
            || DataContext is not PaintCanvasViewModel { BackgroundImage: { } backgroundBitmap }
        )
        {
            return;
        }

        // Set size if mismatch
        if (
            Math.Abs(MainCanvas.Width - backgroundBitmap.Width) > 0.1
            || Math.Abs(MainCanvas.Height - backgroundBitmap.Height) > 0.1
        )
        {
            MainCanvas.Width = backgroundBitmap.Width;
            MainCanvas.Height = backgroundBitmap.Height;
            MainCanvas.InvalidateVisual();
        }
    }

    private int lastCanvasCursorRadius;
    private Cursor? lastCanvasCursor;

    private void UpdateCanvasCursor()
    {
        if (MainCanvas is not { } canvas)
        {
            return;
        }

        var currentZoom = ViewModel?.CurrentZoom ?? 1;

        // Get brush size
        var currentBrushSize = Math.Max((ViewModel?.PaintBrushSize ?? 1) - 2, 1);
        var brushRadius = (int)Math.Ceiling(currentBrushSize * 2 * currentZoom);

        // Only update cursor if brush size has changed
        if (brushRadius == lastCanvasCursorRadius)
        {
            canvas.Cursor = lastCanvasCursor;
            return;
        }

        lastCanvasCursorRadius = brushRadius;

        var brushDiameter = brushRadius * 2;

        const int padding = 4;

        var canvasCenter = brushRadius + padding;
        var canvasSize = brushDiameter + padding * 2;

        using var cursorBitmap = new SKBitmap(canvasSize, canvasSize);

        using var cursorCanvas = new SKCanvas(cursorBitmap);
        cursorCanvas.Clear(SKColors.Transparent);
        cursorCanvas.DrawCircle(
            brushRadius + padding,
            brushRadius + padding,
            brushRadius,
            new SKPaint
            {
                Color = SKColors.Black,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f,
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
                IsDither = true,
                IsAntialias = true
            }
        );
        cursorCanvas.Flush();

        using var data = cursorBitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();

        var bitmap = WriteableBitmap.Decode(stream);

        canvas.Cursor = new Cursor(bitmap, new PixelPoint(canvasCenter, canvasCenter));

        lastCanvasCursor?.Dispose();
        lastCanvasCursor = canvas.Cursor;
    }

    private void MainCanvas_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        UpdateCanvasCursor();
    }

    private void MainCanvas_OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is SkiaCustomCanvas canvas)
        {
            canvas.Cursor = new Cursor(StandardCursorType.Arrow);
        }
    }

    private Point GetRelativePosition(Point pt, Visual? relativeTo)
    {
        if (VisualRoot is not Visual visualRoot)
            return default;
        if (relativeTo == null)
            return pt;

        return pt * visualRoot.TransformToVisual(relativeTo) ?? default;
    }

    public AsyncRelayCommand ClearCanvasCommand => new(ClearCanvasAsync);

    public async Task ClearCanvasAsync()
    {
        Paths = ImmutableList<PenPath>.Empty;
        TemporaryPaths.Clear();

        await Dispatcher.UIThread.InvokeAsync(() => MainCanvas?.InvalidateVisual());
    }

    private static void RenderPenPath(SKCanvas canvas, PenPath penPath, SKPaint paint)
    {
        if (penPath.Points.Count == 0)
        {
            return;
        }

        // Apply Color
        paint.Color = penPath.FillColor;

        if (penPath.IsErase)
        {
            paint.BlendMode = SKBlendMode.SrcIn;
            paint.Color = SKColors.Transparent;
        }

        // Defaults
        paint.IsDither = true;
        paint.IsAntialias = true;

        // Track if we have any pen points
        var hasPenPoints = false;

        // Can't use foreach since this list may be modified during iteration
        // ReSharper disable once ForCanBeConvertedToForeach
        for (var i = 0; i < penPath.Points.Count; i++)
        {
            var penPoint = penPath.Points[i];

            // Skip non-pen points
            if (!penPoint.IsPen)
            {
                continue;
            }

            hasPenPoints = true;

            var radius = penPoint.Radius;
            var pressure = penPoint.Pressure ?? 1;
            var thickness = pressure * radius * 2.5;

            // Draw path
            if (i < penPath.Points.Count - 1)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = (float)thickness;
                paint.StrokeCap = SKStrokeCap.Round;
                paint.StrokeJoin = SKStrokeJoin.Round;

                var nextPoint = penPath.Points[i + 1];
                canvas.DrawLine(
                    (float)penPoint.X,
                    (float)penPoint.Y,
                    (float)nextPoint.X,
                    (float)nextPoint.Y,
                    paint
                );
            }

            // Draw circles for pens
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawCircle((float)penPoint.X, (float)penPoint.Y, (float)thickness / 2, paint);
        }

        // Draw paths directly if we didn't have any pen points
        if (!hasPenPoints)
        {
            var point = penPath.Points[0];
            var thickness = point.Radius * 2;

            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = (float)thickness;
            paint.StrokeCap = SKStrokeCap.Round;
            paint.StrokeJoin = SKStrokeJoin.Round;

            var skPath = penPath.ToSKPath();
            canvas.DrawPath(skPath, paint);
        }
    }

    private void OnRenderSkia(SKCanvas canvas)
    {
        RenderCanvasCore(canvas, renderBackgroundFill: true, renderBackgroundImage: true);
    }

    private void RenderCanvasCore(
        SKCanvas canvas,
        bool renderBackgroundFill = false,
        bool renderBackgroundImage = false
    )
    {
        // Draw background color
        canvas.Clear(SKColors.Transparent);

        // Draw background image if set
        if (renderBackgroundImage && ViewModel?.BackgroundImage is { } backgroundImage)
        {
            canvas.DrawBitmap(backgroundImage, new SKPoint(0, 0));
        }

        // Draw any additional images
        foreach (var layerImage in ViewModel?.LayerImages ?? Enumerable.Empty<SKBitmap>())
        {
            canvas.DrawBitmap(layerImage, new SKPoint(0, 0));
        }

        using var paint = new SKPaint();

        // Draw the paths
        foreach (var penPath in TemporaryPaths.Values)
        {
            RenderPenPath(canvas, penPath, paint);
        }

        foreach (var penPath in Paths)
        {
            RenderPenPath(canvas, penPath, paint);
        }

        canvas.Flush();
    }
}
