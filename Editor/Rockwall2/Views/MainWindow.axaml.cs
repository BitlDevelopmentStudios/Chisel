using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Rockwall2.Editor.Common;
using Rockwall2.Editor.Common.Input;
using Rockwall2.Editor.Common.Utils;
using Rockwall2.Editor.Model.Utils;
using Rockwall2.Editor.Particles.Utils;
using System;
using System.Diagnostics;

namespace Rockwall2.Views;
public partial class MainWindow : Window
{
    public static MainWindow Instance;
    public MapEditor MapEditor => mapEditor;
    public Control GameView => MapEditor.GameView;

    private bool isWindowDragInEffect = false;
    private Point cursorDragStart = new(0, 0);
    public MainWindow()
    {
        if (Design.IsDesignMode)
        {
            InitMainWindow();
            return;
        }
        IsVisible = true;
        Instance = this;
    }
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        
        // dumb background thread... DIE!!
        // there must be a better way to do this, but its a weird recent bug of some lingering thread
        // and it's too close to release to be assed to scan everything I've added in the past month lol.
        Environment.Exit(0);
    }
    public void InitMainWindow()
    {
        InitializeComponent();

        if (!Design.IsDesignMode)
        {
            SoundDevice.Initialize();

            AddHandler(KeyDownEvent, (_, e) =>
            {
                KeyboardManager.OnKeyPressed(e.Key);
                if (e.Key == Key.Tab)
                    e.Handled = true;
            }, RoutingStrategies.Tunnel);
            KeyUp += (_, e) => KeyboardManager.OnKeyReleased(e.Key);

            LostFocus += (_, e) =>
            {
                KeyboardManager.ClearKeys();
                MouseManager.Clear();
            };
            GotFocus += (_, e) =>
            {
                KeyboardManager.ClearKeys();
                MouseManager.Clear();
            };

            PointerMoved += (_, e) => MouseManager.OnPointerMoved(e, this);
            PointerPressed += (_, e) => MouseManager.OnPointerPressed(e, this);
            PointerReleased += (_, e) => MouseManager.OnPointerReleased(e, this);
            PointerWheelChanged += (_, e) => MouseManager.OnScroll(e);

            BlenderDetector.Detect();
            MouseManager.TryEnableRawInput(this);

            EditorPrefs.LoadEditorPrefs();
        }

        // Close when main window closes
        this.Closed += (_, _) => modelEditor.MorphAnimator?.Close();
    }
    private void TitleBar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (isWindowDragInEffect)
        {
            Point currentCursorPosition = e.GetPosition(this);
            Point delta = currentCursorPosition - cursorDragStart;
            Position = this.PointToScreen(delta);
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState == WindowState.Maximized || WindowState == WindowState.FullScreen) return;
        isWindowDragInEffect = true;
        cursorDragStart = e.GetPosition(this);
    }

    private void TitleBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
        => isWindowDragInEffect = false;

    private void MapFileNew(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapTools.NewMap();
    }
    private void MapFileOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FileHandler.OpenMap();
    }
    private void MapFileSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FileHandler.SaveCurrentMap();
    }
    private void MapFileSaveAs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FileHandler.SaveMapAs();
    }
    private void MapFVisEnable(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapTools.FastVisEnabled = true;
    }
    private void MapFVisDisable(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapTools.FastVisEnabled = false;
    }
    private void MapLPU4(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapTools.LightmapRes = 4;
    }
    private void MapLPU8(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapTools.LightmapRes = 8;
    }
    private void MapLPU16(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapTools.LightmapRes = 16;
    }
    private void MapLPU32(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapTools.LightmapRes = 32;
    }
    private void MapExecBuild(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapTools.BuildMap(false);
    }
    private void MapExecBuildDebug(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapTools.BuildMap(true);
    }

    private async void MapPrefsOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var prefs = new MapPrefs();
        await prefs.ShowDialog(this);
    }

    private void ModelFileOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FileHandler.OpenModel();
    }
    private void ModelFileSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FileHandler.SaveModelAs();
    }
    private void ModelFileSaveAs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FileHandler.SaveModelAs();
    }
    private void ModelBlender(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ModelImporter.ImportFromBlender();
    }
    private void ModelFBXModel(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ModelImporter.ReimportFBXMesh();
    }
    private void ModelFBXAnimations(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ModelImporter.ReimportFBXAnimations();
    }
    private void ModelMorphAnimatorOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Close any existing instance first
        modelEditor.MorphAnimator?.Close();

        modelEditor.MorphAnimator = new MorphAnimator();
        modelEditor.MorphAnimator.Topmost = true;

        modelEditor.MorphAnimator.Show(this);
    }
    private void ModelChoreoDirectorOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        modelEditor.ToggleChoreoDirector();
    }

    private void PartNew(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ParticleEditorFileOpener.NewParticle();
    }
    private void PartOpen(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FileHandler.OpenParticle();
    }
    private void PartSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FileHandler.SaveParticleAs();
    }
}