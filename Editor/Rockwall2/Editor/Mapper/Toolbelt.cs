using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DefaultUnDo;
using Microsoft.Xna.Framework;
using MsBox.Avalonia;
using Rockwall;
using Rockwall2.Editor.Common;
using Rockwall2.Editor.Common.Input;
using Rockwall2.Editor.Common.Toolbar;
using Rockwall2.Editor.Mapper.Toolbar;
using Rockwall2.Editor.Mapper.Tools;
using Rockwall2.Editor.Mapper.Utils;
using Rockwall2.Editor.Mapper.ViewportManagement;
using Rockwall2.Tools;
using Rockwall2.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Rockwall2.Editor.Mapper;

public static class Toolbelt
{
    public static string ActiveTexture = "tool_nodraw";
    public static IUnDoManager UndoManager = new UnDoManager();

    public static bool TreatBrushEntitiesAsGroups = true;
    public static bool EnableGroups = true;
    public static bool HideToolFaces = false;
    public static bool ShowFog = true;

    public static bool FlipSelectShift = false;
    public static bool FlipScrollShift = false;
    public static bool AutoSaveBeforeCompile = true;

    public static Tool BrushTool = new BrushTool();
    public static Tool ClipTool = new ClippingTool();
    public static Tool EntityTool = new EntityTool();
    public static Tool HintTool = new HintTool();
    public static Tool SelectionTool = new SelectionTool();
    public static Tool TranslateTool = new TranslateTool();
    public static Tool RotationTool = new RotateTool();
    public static Tool TextureTool = new TextureApplicationTool();
    public static Tool VertexTool = new VertexEditTool();

    public static Tool ActiveTool = SelectionTool;
    public static List<BrushBuilder> ActiveBuilders { get; private set; } = new();

    public static event Action<int>? ToolHotkeyPressed;

    private static Transformable highlightedObject;

    public static Transformable HighlightedObject
    {
        get
        {
            return highlightedObject;
        }
        set
        {
            highlightedObject = value;
        }
    }
    public static List<Transformable> SelectedObjects = new List<Transformable>();


    public static async void AddBuilder(BrushBuilder b)
    {
        ActiveBuilders.Add(b);
        b.Open();
        b.RegenerateGeometry();
        await new BuilderConfigWindow(b).ShowDialog(MainWindow.Instance);
    }
    public static void RemoveBuilder(BrushBuilder b)
    {
        ActiveBuilders.Remove(b);
    }

    public static void CreateUndoStateForAllSelectedObjects()
    {
        CreateUndoStateForSelectObjects(SelectedObjects);
    }
    public static void CreateUndoStateForSelectObjects(List<Transformable> affected)
    {
        TransformableState state = new TransformableState();
        state.affectedTransformables = new List<Transformable>(affected);
        state.deltaPositions = new List<Vector3>();

        for (int i = 0; i < affected.Count; i++)
        {
            var obj = affected[i];
            state.deltaPositions.Add(obj.StartPosition - obj.GetPosition());
        }

        UndoManager.DoOnUndo(() =>
        {
            for (int i = 0; i < state.affectedTransformables.Count; i++)
            {
                var obj = state.affectedTransformables[i];
                obj.Move(state.deltaPositions[i]);
            }
            for (int i = 0; i < state.affectedTransformables.Count; i++)
            {
                var obj = state.affectedTransformables[i];
                obj.PostMove();
            }
        });
    }
    public static void ShowTerrainQuadError()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _ = MessageBoxManager.GetMessageBoxStandard("Cannot Edit Terrain",
                "That edit would turn a terrain's source face into a non-quad. The change has been reverted.")
                .ShowAsPopupAsync(MainWindow.Instance);
        });
    }

    public static List<Transformable> DuplicateSelectedObjects()
    {
        var selected = SelectedObjects.ToList();
        var created = new List<Transformable>();
        var handledBrushes = new HashSet<BrushMoveable>();

        foreach (var group in selected.OfType<BrushMoveable>().GroupBy(b => MapTools.GetOwningEntity(b.brush)))
        {
            EntityReference targetEntity = null;
            foreach (var brushMoveable in group)
            {
                var dup = brushMoveable.Duplicate(targetEntity);
                if (targetEntity == null && group.Key != null && dup is BrushMoveable bm)
                    targetEntity = MapTools.GetOwningEntity(bm.brush);

                if (dup != null) created.Add(dup);
                handledBrushes.Add(brushMoveable);
            }
        }

        foreach (var obj in selected)
        {
            if (obj is BrushMoveable bm && handledBrushes.Contains(bm)) continue;
            var dup = obj.Duplicate();
            if (dup != null) created.Add(dup);
        }

        SelectedObjects = created;
        UndoManager.DoOnUndo(() => { foreach (var obj in created) obj.Delete(); MapTools.FinalizeDeletedObjects(); });

        return created;
    }

    public static void SwitchTool(Tool tool)
    {
        ActiveTool.OnDeselected();
        ActiveTool = tool;
        ActiveTool.OnSelected();
    }
    public static void ActivateHotbarSlot(int index)
    {
        Hotbar.Activate(index + 1);
    }

    public static BoundingBox GetSelectionBounds()
    {
        Vector3 min = Vector3.One * float.MaxValue;
        Vector3 max = Vector3.One * float.MinValue;

        // shouldnt be possible... crashin anyway...
        if (Toolbelt.SelectedObjects == null || Toolbelt.SelectedObjects.Count == 0) return new BoundingBox(min, max);

        foreach (var sel in Toolbelt.SelectedObjects)
        {
            BoundingBox box = new BoundingBox();

            float f = (8f / ViewportManager.Active.Zoom);

            if (sel is EntityMoveable entity)
            {
                box = new BoundingBox(Vector3.One * -0.25f + entity.GetPosition(), Vector3.One * 0.25f + entity.GetPosition());

                if (entity.entity >= MapTools.Entities.Length) continue;
                if (entity.entity < 0) continue;

                var id = Array.FindIndex(GlobalEditorData.EditorOverrides.overrides, o => o.name == MapTools.Entities[entity.entity].EntityName);
                if (id != -1)
                {
                    box = new BoundingBox(GlobalEditorData.EditorOverrides.overrides[id].boundsMin + entity.GetPosition(),
                                          GlobalEditorData.EditorOverrides.overrides[id].boundsMax + entity.GetPosition());
                }
            }
            if (sel is TerrainMoveable terrain)
            {
                if (terrain.terrain >= MapTools.Terrains.Length) continue;
                if (terrain.terrain < 0) continue;

                box = MapTools.Terrains[terrain.terrain].Bounds;
            }
            if (sel is BrushMoveable brush)
            {
                if (brush.brush >= MapTools.BrushBounds.Length) continue;
                if (brush.brush < 0) continue;

                box = MapTools.BrushBounds[brush.brush];
            }
            if (sel is FaceMoveable face)
            {
                if (face.brush >= MapTools.BrushBounds.Length) continue;
                if (face.brush < 0) continue;

                box = MapTools.BrushBounds[face.brush];
            }
            if (sel is BrushEdgeMoveable edge)
            {
                if (edge.brush >= MapTools.BrushBounds.Length) continue;
                if (edge.brush < 0) continue;

                var pa = MapTools.Brushes[edge.brush].Position + MapTools.Brushes[edge.brush].Vertices[edge.vertA];
                var pb = MapTools.Brushes[edge.brush].Position + MapTools.Brushes[edge.brush].Vertices[edge.vertB];

                var aa = Vector3.Min(pa, pb);
                var bb = Vector3.Max(pa, pb);

                box = new BoundingBox(Vector3.Min(aa - Vector3.One * f, aa + Vector3.One * f),
                                      Vector3.Max(bb - Vector3.One * f, bb + Vector3.One * f));
            }
            if (sel is BrushVertexMoveable vert)
            {
                if (vert.brush >= MapTools.BrushBounds.Length) continue;
                if (vert.brush < 0) continue;

                var pa = MapTools.Brushes[vert.brush].Position + MapTools.Brushes[vert.brush].Vertices[vert.vert];
                box = new BoundingBox(Vector3.Min(pa - Vector3.One * f, pa + Vector3.One * f),
                                      Vector3.Max(pa - Vector3.One * f, pa + Vector3.One * f));
            }
            if (sel is HintMoveable hintSel)
            {
                if (hintSel.hint >= MapTools.Hints.Length) continue;
                if (hintSel.hint < 0) continue;

                var pos = MapTools.Hints[hintSel.hint].Position;
                box = new BoundingBox(pos - Vector3.One * 0.25f, pos + Vector3.One * 0.25f);
            }

            min = Vector3.Min(box.Min, min);
            max = Vector3.Max(box.Max, max);
        }

        return new BoundingBox(min, max);
    }

    public static EditorGroup CreateGroupFromSelected()
    {
        List<Guid> members = new List<Guid>();

        foreach (var selection in SelectedObjects)
        {
            if (selection is BrushMoveable brushMoveable)
            {
                members.Add(brushMoveable.BrushId);
            }
            if (selection is FaceMoveable faceMoveable)
            {
                members.Add(faceMoveable.BrushId);
            }
            if (selection is BrushEdgeMoveable edgeMoveable)
            {
                members.Add(edgeMoveable.BrushId);
            }
            if (selection is BrushVertexMoveable vertexMoveable)
            {
                members.Add(vertexMoveable.BrushId);
            }

            if (selection is EntityMoveable entityMoveable)
            {
                members.Add(entityMoveable.EntityId);
            }

            if (selection is HintMoveable hintMoveable)
            {
                members.Add(hintMoveable.HintId);
            }
        }

        EditorGroup existingGroup = null;
        foreach (var member in members)
        {
            existingGroup = MapTools.GetOwningGroup(member);
            if (existingGroup != null) break;
        }

        if (existingGroup != null)
        {
            var lst = existingGroup.GroupMembers.ToList();
            foreach (var member in members)
            {
                if (!lst.Contains(member)) lst.Add(member);
            }
            existingGroup.GroupMembers = lst.ToArray();

            return existingGroup;
        }

        return new EditorGroup { GroupMembers = members.ToArray() };
    }

    public static void RemoveSelectedFromGroups()
    {
        foreach (var selection in SelectedObjects)
        {
            if (selection is BrushMoveable brushMoveable)
            {
                MapTools.RemoveFromParentGroup(brushMoveable.BrushId);
            }
            if (selection is FaceMoveable faceMoveable)
            {
                MapTools.RemoveFromParentGroup(faceMoveable.BrushId);
            }
            if (selection is BrushEdgeMoveable edgeMoveable)
            {
                MapTools.RemoveFromParentGroup(edgeMoveable.BrushId);
            }
            if (selection is BrushVertexMoveable vertexMoveable)
            {
                MapTools.RemoveFromParentGroup(vertexMoveable.BrushId);
            }

            if (selection is EntityMoveable entityMoveable)
            {
                MapTools.RemoveFromParentGroup(entityMoveable.EntityId);
            }

            if (selection is HintMoveable hintMoveable)
            {
                MapTools.RemoveFromParentGroup(hintMoveable.HintId);
            }
        }
    }
    public static void BreakSelectedGroups()
    {
        foreach (var selection in SelectedObjects)
        {
            if (selection is BrushMoveable brushMoveable)
            {
                MapTools.BreakParentGroup(brushMoveable.BrushId);
            }
            if (selection is FaceMoveable faceMoveable)
            {
                MapTools.BreakParentGroup(faceMoveable.BrushId);
            }
            if (selection is BrushEdgeMoveable edgeMoveable)
            {
                MapTools.BreakParentGroup(edgeMoveable.BrushId);
            }
            if (selection is BrushVertexMoveable vertexMoveable)
            {
                MapTools.BreakParentGroup(vertexMoveable.BrushId);
            }

            if (selection is EntityMoveable entityMoveable)
            {
                MapTools.BreakParentGroup(entityMoveable.EntityId);
            }

            if (selection is HintMoveable hintMoveable)
            {
                MapTools.BreakParentGroup(hintMoveable.HintId);
            }
        }
    }
    public static List<Transformable> GetFromGroup(EditorGroup group)
    {
        List<Transformable> objs = new();
        foreach(var member in group.GroupMembers)
        {
            if (!MapTools.GuidMapper.TryGetValue(member, out var pair)) continue;

            switch(pair.type)
            {
                case MapTools.ObjType.Brush:

                    objs.Add(new BrushMoveable(pair.index));

                    break;
                case MapTools.ObjType.Entity:

                    objs.Add(new EntityMoveable(pair.index));

                    break;
                case MapTools.ObjType.Terrain:

                    objs.Add(new TerrainMoveable(pair.index));

                    break;
                case MapTools.ObjType.Hint:

                    objs.Add(new HintMoveable(pair.index));

                    break;
            }
        }

        return objs;
    }

    public static void OpenHint(Hint hint, Action onClosed)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var headerBox = new TextBox
            {
                Watermark = "Header",
                Margin = new Avalonia.Thickness(0, 0, 0, 8),
                Text = hint.Header
            };

            var descriptionBox = new TextBox
            {
                Watermark = "Description",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 150,
                Margin = new Avalonia.Thickness(0, 0, 0, 8),
                Text = hint.Body
            };

            var okButton = new Button
            {
                Content = "OK",
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var panel = new StackPanel
            {
                Margin = new Avalonia.Thickness(12),
                Children =
                        {
                            new TextBlock { Text = "Header", FontWeight = FontWeight.Bold },
                            headerBox,
                            new TextBlock { Text = "Description", FontWeight = FontWeight.Bold },
                            descriptionBox,
                            okButton
                        }
            };

            var window = new Window
            {
                Title = "New Item",
                Width = 400,
                Height = 350,
                Content = panel,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            okButton.Click += (s, e) =>
            {
                var header = headerBox.Text;
                var description = descriptionBox.Text;

                hint.Header = header;
                hint.Body = description;

                window.Close();
            };

            window.Closed += (s, e) => onClosed();

            window.ShowDialog(MainWindow.Instance);
        });
    }

    public static IEnumerable<int> GetSelectedBrushIds()
    {
        return SelectedObjects.OfType<BrushMoveable>().Select(b => b.brush)
            .Concat(SelectedObjects.OfType<FaceMoveable>().Select(f => f.brush))
            .Distinct();
    }
    public static IEnumerable<Transformable> GetTransformablesFor2D()
    {
        var seenBrushes = new HashSet<int>();

        foreach (var obj in SelectedObjects)
        {
            if (obj is FaceMoveable face)
            {
                if (seenBrushes.Add(face.brush))
                    yield return new BrushMoveable(face.brush);
                continue;
            }

            yield return obj;
        }
    }
    public static void QuickCSGSubtract()
    {
        var brushes = GetSelectedBrushIds().ToList();
        if (brushes.Count == 0) return;

        var snapshotWorld = new AllBrushSnapshot(MapTools.Brushes);
        var snapshotEntity = new AllEntitySnapshot(MapTools.Entities);

        bool any = false;

        foreach(var id in brushes)
        {
            any |= CSGUtils.SubtractWithBrush(id);
        }
        MapTools.RecomputeAllBrushBounds();

        UndoManager.DoOnUndo(() =>
        {
            snapshotWorld.Restore();
            snapshotEntity.Restore();
            MapTools.RecomputeAllBrushBounds();
        });

        if (!any) UndoManager.Undo();
    }
    public static void QuickCSGIntersect()
    {
        var brushes = GetSelectedBrushIds().ToList();
        if (brushes.Count == 0) return;

        var snapshotWorld = new AllBrushSnapshot(MapTools.Brushes);
        var snapshotEntity = new AllEntitySnapshot(MapTools.Entities);

        bool any = false;

        foreach (var id in brushes)
        {
            any |= CSGUtils.IntersectWithBrush(id);
        }
        MapTools.RecomputeAllBrushBounds();

        UndoManager.DoOnUndo(() =>
        {
            snapshotWorld.Restore();
            snapshotEntity.Restore();
            MapTools.RecomputeAllBrushBounds();
        });

        if (!any) UndoManager.Undo();
    }
    public static void QuickCSGMerge()
    {
        var brushes = GetSelectedBrushIds().ToList();
        if (brushes.Count == 0) return;

        var snapshotWorld = new AllBrushSnapshot(MapTools.Brushes);
        var snapshotEntity = new AllEntitySnapshot(MapTools.Entities);

        bool any = CSGUtils.MergeBrushes(brushes.ToList());
        MapTools.RecomputeAllBrushBounds();

        UndoManager.DoOnUndo(() =>
        {
            snapshotWorld.Restore();
            snapshotEntity.Restore();
            MapTools.RecomputeAllBrushBounds();
        });

        if (!any) UndoManager.Undo();
    }
}