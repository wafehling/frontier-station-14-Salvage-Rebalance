using System;
using System.Collections.Generic;
using System.Linq;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Robust.Shared.ViewVariables;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client.Viewport
{
    /// <summary>
    ///     Viewport control that has a fixed viewport size and scales it appropriately.
    /// </summary>
    public sealed class ScalingViewport : Control, IViewportControl
    {
        [Dependency] private readonly IClyde _clyde = default!;
        [Dependency] private readonly IInputManager _inputManager = default!;

        // Internal viewport creation is deferred.
        private IClydeViewport? _viewport;
        private List<IClydeViewport> _lowerPorts = new();
        private IEye? _eye;
        private IEye[] _lowerEyes = new IEye[] {};
        private Vector2i _viewportSize;
        private int _curRenderScale;
        private ScalingViewportStretchMode _stretchMode = ScalingViewportStretchMode.Bilinear;
        private ScalingViewportRenderScaleMode _renderScaleMode = ScalingViewportRenderScaleMode.Fixed;
        private int _fixedRenderScale = 1;

        private readonly List<CopyPixelsDelegate<Rgba32>> _queuedScreenshots = new();

        public int CurrentRenderScale => _curRenderScale;

        /// <summary>
        ///     The eye to render.
        /// </summary>
        public IEye? Eye
        {
            get => _eye;
            set
            {
                _eye = value;

                if (_viewport != null)
                    _viewport.Eye = value;
            }
        }

        public IEye[] LowerEyes
        {
            get => _lowerEyes;
            set
            {
                var old = value;
                _lowerEyes = value;
                if (old.Length != value.Length)
                {
                    InvalidateViewport();
                    Logger.Debug("Eyes updated..");
                }


                foreach (var (eye, port) in _lowerEyes.Zip(_lowerPorts))
                {
                    port.Eye = eye;
                }
            }
        }

        /// <summary>
        ///     The size, in unscaled pixels, of the internal viewport.
        /// </summary>
        /// <remarks>
        ///     The actual viewport may have render scaling applied based on parameters.
        /// </remarks>
        public Vector2i ViewportSize
        {
            get => _viewportSize;
            set
            {
                _viewportSize = value;
                InvalidateViewport();
            }
        }

        // Do not need to InvalidateViewport() since it doesn't affect viewport creation.

        [ViewVariables(VVAccess.ReadWrite)] public Vector2i? FixedStretchSize { get; set; }

        [ViewVariables(VVAccess.ReadWrite)]
        public ScalingViewportStretchMode StretchMode
        {
            get => _stretchMode;
            set
            {
                _stretchMode = value;
                InvalidateViewport();
            }
        }

        [ViewVariables(VVAccess.ReadWrite)]
        public ScalingViewportRenderScaleMode RenderScaleMode
        {
            get => _renderScaleMode;
            set
            {
                _renderScaleMode = value;
                InvalidateViewport();
            }
        }

        [ViewVariables(VVAccess.ReadWrite)]
        public int FixedRenderScale
        {
            get => _fixedRenderScale;
            set
            {
                _fixedRenderScale = value;
                InvalidateViewport();
            }
        }

        public ScalingViewport()
        {
            IoCManager.InjectDependencies(this);
            RectClipContent = true;
        }

        protected override void KeyBindDown(GUIBoundKeyEventArgs args)
        {
            base.KeyBindDown(args);

            if (args.Handled)
                return;

            _inputManager.ViewportKeyEvent(this, args);
        }

        protected override void KeyBindUp(GUIBoundKeyEventArgs args)
        {
            base.KeyBindUp(args);

            if (args.Handled)
                return;

            _inputManager.ViewportKeyEvent(this, args);
        }

        protected override void Draw(DrawingHandleScreen handle)
        {
            EnsureViewportCreated();

            DebugTools.AssertNotNull(_viewport);

            _viewport!.Render();

            foreach (var viewport in _lowerPorts)
            {
                viewport.Render();
            }

            if (_queuedScreenshots.Count != 0)
            {
                var callbacks = _queuedScreenshots.ToArray();

                _viewport.RenderTarget.CopyPixelsToMemory<Rgba32>(image =>
                {
                    foreach (var callback in callbacks)
                    {
                        callback(image);
                    }
                });

                _queuedScreenshots.Clear();
            }

            var drawBox = GetDrawBox();
            var drawBoxGlobal = drawBox.Translated(GlobalPixelPosition);
            _viewport.RenderScreenOverlaysBelow(handle, this, drawBoxGlobal);
            foreach (var viewport in _lowerPorts.AsEnumerable().Reverse())
            {
                handle.DrawTextureRect(viewport.RenderTarget.Texture, drawBox);
            }
            handle.DrawTextureRect(_viewport.RenderTarget.Texture, drawBox);
            _viewport.RenderScreenOverlaysAbove(handle, this, drawBoxGlobal);
        }

        public void Screenshot(CopyPixelsDelegate<Rgba32> callback)
        {
            _queuedScreenshots.Add(callback);
        }

        // Draw box in pixel coords to draw the viewport at.
        private UIBox2i GetDrawBox()
        {
            DebugTools.AssertNotNull(_viewport);

            var vpSize = _viewport!.Size;
            var ourSize = (Vector2) PixelSize;

            if (FixedStretchSize == null)
            {
                var (ratioX, ratioY) = ourSize / vpSize;
                var ratio = Math.Min(ratioX, ratioY);

                var size = vpSize * ratio;
                // Size
                var pos = (ourSize - size) / 2;

                return (UIBox2i) UIBox2.FromDimensions(pos, size);
            }
            else
            {
                // Center only, no scaling.
                var pos = (ourSize - FixedStretchSize.Value) / 2;
                return (UIBox2i) UIBox2.FromDimensions(pos, FixedStretchSize.Value);
            }
        }

        private void RegenerateViewport()
        {
            DebugTools.AssertNull(_viewport);

            var vpSizeBase = ViewportSize;
            var ourSize = PixelSize;
            var (ratioX, ratioY) = ourSize / (Vector2) vpSizeBase;
            var ratio = Math.Min(ratioX, ratioY);
            var renderScale = 1;
            switch (_renderScaleMode)
            {
                case ScalingViewportRenderScaleMode.CeilInt:
                    renderScale = (int) Math.Ceiling(ratio);
                    break;
                case ScalingViewportRenderScaleMode.FloorInt:
                    renderScale = (int) Math.Floor(ratio);
                    break;
                case ScalingViewportRenderScaleMode.Fixed:
                    renderScale = _fixedRenderScale;
                    break;
            }

            // Always has to be at least one to avoid passing 0,0 to the viewport constructor
            renderScale = Math.Max(1, renderScale);

            _curRenderScale = renderScale;

            _viewport = _clyde.CreateViewport(
                ViewportSize * renderScale,
                new TextureSampleParameters
                {
                    Filter = StretchMode == ScalingViewportStretchMode.Bilinear,
                });
            _viewport.ClearColor = Color.Blue.WithAlpha(0.02f);

            _lowerPorts.Clear();
            for (var i = 0; i < _lowerEyes.Length; i++)
            {
                _lowerPorts.Add(_clyde.CreateViewport(
                    ViewportSize * renderScale,
                    new TextureSampleParameters
                    {
                        Filter = StretchMode == ScalingViewportStretchMode.Bilinear,
                    }));
                _lowerPorts[i].RenderScale = (renderScale, renderScale);
                _lowerPorts[i].ClearColor = Color.Blue.WithAlpha(0.02f);

                _lowerPorts[i].Eye = _lowerEyes[i];
                _lowerPorts[i].Eye!.Zoom =  _lowerPorts[i].Eye!.Zoom * (1.02f + i * 0.02f);
            }

            _viewport.RenderScale = (renderScale, renderScale);

            _viewport.Eye = _eye;
        }

        protected override void Resized()
        {
            base.Resized();

            InvalidateViewport();
        }

        private void InvalidateViewport()
        {
            _viewport?.Dispose();
            _viewport = null;
            foreach (var port in _lowerPorts)
            {
                port.Dispose();
            }
            _lowerPorts = new();
        }

        public MapCoordinates ScreenToMap(Vector2 coords)
        {
            if (_eye == null)
                return default;

            EnsureViewportCreated();

            var matrix = Matrix3.Invert(GetLocalToScreenMatrix());

            return _viewport!.LocalToWorld(matrix.Transform(coords));
        }

        public Vector2 WorldToScreen(Vector2 map)
        {
            if (_eye == null)
                return default;

            EnsureViewportCreated();

            var vpLocal = _viewport!.WorldToLocal(map);

            var matrix = GetLocalToScreenMatrix();

            return matrix.Transform(vpLocal);
        }

        public Matrix3 GetWorldToScreenMatrix()
        {
            EnsureViewportCreated();
            return _viewport!.GetWorldToLocalMatrix() * GetLocalToScreenMatrix();
        }

        public Matrix3 GetLocalToScreenMatrix()
        {
            EnsureViewportCreated();

            var drawBox = GetDrawBox();
            var scaleFactor = drawBox.Size / (Vector2) _viewport!.Size;

            if (scaleFactor.X == 0 || scaleFactor.Y == 0)
                // Basically a nonsense scenario, at least make sure to return something that can be inverted.
                return Matrix3.Identity;

            return Matrix3.CreateTransform(GlobalPixelPosition + drawBox.TopLeft, 0, scaleFactor);
        }

        private void EnsureViewportCreated()
        {
            if (_viewport == null || _lowerPorts.Count != _lowerEyes.Length)
            {
                RegenerateViewport();
            }

            DebugTools.AssertNotNull(_viewport);
        }
    }

    /// <summary>
    ///     Defines how the viewport is stretched if it does not match the size of the control perfectly.
    /// </summary>
    public enum ScalingViewportStretchMode
    {
        /// <summary>
        ///     Bilinear sampling is used.
        /// </summary>
        Bilinear = 0,

        /// <summary>
        ///     Nearest neighbor sampling is used.
        /// </summary>
        Nearest,
    }

    /// <summary>
    ///     Defines how the base render scale of the viewport is selected.
    /// </summary>
    public enum ScalingViewportRenderScaleMode
    {
        /// <summary>
        ///     <see cref="ScalingViewport.FixedRenderScale"/> is used.
        /// </summary>
        Fixed = 0,

        /// <summary>
        ///     Floor to the closest integer scale possible.
        /// </summary>
        FloorInt,

        /// <summary>
        ///     Ceiling to the closest integer scale possible.
        /// </summary>
        CeilInt
    }
}
