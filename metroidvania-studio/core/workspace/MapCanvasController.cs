using System;
using System.Collections.Generic;
using System.Linq;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public enum MetroidvaniaStudioTool { Rooms, Placement, Selection, Brush, Rectangle, Bucket, Line, Circle, Ellipse }

    /// <summary>Editing gestures use tile coordinates; screen and Editor APIs belong to the view.</summary>
    public sealed partial class MapCanvasController
    {
        public MapEditSession Session { get; }
        public MapRoomEditing RoomEditor { get; }
        public MapObjectEditing ObjectEditor { get; }
        public Func<string, MapObjectDefinition> ResolveObjectDefinition { get; set; }
        public Func<string, TerrainTileSet> ResolveTerrainTileSet { get; set; }
        public Action ObjectPropertiesRequested { get; set; }
        public MapObject PlacementTemplate { get; set; }
        public bool UsesObjectSelection => !IsTileLayer && (Layer != MapLayer.All || !Selection.HasValue);
        public bool RoomPlacement { get; set; }
        public string ActiveGroupId { get; set; } = "";
        public string ActiveRoomId { get; set; }
        public MapRoom Room => Session.Document.rooms.Find(room => room.id == ActiveRoomId);
        private MetroidvaniaStudioTool tool = MetroidvaniaStudioTool.Brush;
        public MetroidvaniaStudioTool Tool
        {
            get => tool;
            set
            {
                if (tool == value) return;
                tool = value;
                Selection = null;
            }
        }
        public MapLayer Layer { get; set; } = MapLayer.ForegroundTiles;
        public TileShape Shape { get; set; }
        public string Material { get; set; } = "terrain";
        public string ObjectDefinition { get; set; } = "Spawn";
        public int BrushSize { get; set; } = 1;
        /// <summary>Maximum raster points one captured paint gesture may visit.</summary>
        public int MaximumPaintWork { get; set; } = int.MaxValue;
        public bool Filled { get; set; } = true;
        public RectInt? Selection { get; private set; }
        public int Revision { get; private set; }
        public bool IsDragging { get; private set; }
        public bool IsTileLayer => Layer == MapLayer.ForegroundTiles || Layer == MapLayer.BackgroundTiles;
        public readonly HashSet<MapLayer> HiddenLayers = new HashSet<MapLayer>();
        public readonly HashSet<MapLayer> LockedLayers = new HashSet<MapLayer>();
        private Vector2Int start, last;
        private bool erasing, movingSelection;
        private string gestureRoomId;
        private string gestureGroupId;
        private MapLayer gestureLayer;
        private MetroidvaniaStudioTool gestureTool;
        private MetroidvaniaStudioTool gestureSelectedTool;
        private MapDocument gestureDocument;
        private object gestureEditToken;
        private bool ownsGestureEdit;
        private MapTileIncrementalEdit gestureTileEdit;
        private MapObjectIncrementalEdit gestureObjectEdit;
        private List<MapCell> paintingCells;
        private Dictionary<Vector2Int, int> paintingIndices;
        private int gesturePaintWork;
        private MapSelectionClipboard selectionClipboard;
        private MapDocument layerGroupIndexDocument;
        private MapLayerGroupIndex layerGroupIndex;
        private readonly Dictionary<(string Member, string Selected), MapLayerGroupAccess> layerGroupAccess =
            new Dictionary<(string Member, string Selected), MapLayerGroupAccess>();

        public MapCanvasController(MapEditSession session)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            ActiveRoomId = session.Document.rooms.Count > 0 ? session.Document.rooms[0].id : null;
            RoomEditor = new MapRoomEditing(session);
            ObjectEditor = new MapObjectEditing(this);
            if (ActiveRoomId != null) RoomEditor.Select(ActiveRoomId);
            Session.Changed += OnChanged;
        }

        private void OnChanged()
        {
            layerGroupIndexDocument = null;
            layerGroupIndex = null;
            layerGroupAccess.Clear();
            Revision++;
            if (Session.LastChangeKind == MapEditChangeKind.Undo || Session.LastChangeKind == MapEditChangeKind.Redo
                || Session.LastChangeKind == MapEditChangeKind.Reset) Selection = null;
            if (IsDragging && (!ReferenceEquals(gestureDocument, Session.Document) || ownsGestureEdit && !OwnsGestureEdit()))
                ForgetGesture();
            if (Room == null) ActiveRoomId = Session.Document.rooms.Count > 0 ? Session.Document.rooms[0].id : null;
            if (!string.IsNullOrEmpty(ActiveGroupId) && !Session.Document.layerGroups.Any(g => g.id == ActiveGroupId && g.layer == Layer))
            { ActiveGroupId = ""; Selection = null; }
        }

        public void Dispose() { Session.Changed -= OnChanged; ObjectEditor.Dispose(); RoomEditor.Dispose(); }

        public void SelectRoom(string id)
        {
            CancelGesture();
            ActiveRoomId = id;
            if (id == null) RoomEditor.Clear(); else RoomEditor.Select(id);
            Selection = null;
            ObjectEditor.Clear();
        }

        public void BeginGesture(Vector2Int cell, bool erase = false)
            => BeginGesture(cell, erase, false);

        /// <summary>Erase continuously with the brush size without changing the selected tool.</summary>
        public void BeginEraseGesture(Vector2Int cell)
        {
            if (IsTileLayer) BeginGesture(cell, true, true);
        }

        private void BeginGesture(Vector2Int cell, bool erase, bool brushErase)
        {
            // Reject foreign pending work before changing capture, selection or
            // calling cancellation paths for this controller's previous gesture.
            if (Session.IsEditing && !OwnsGestureEdit())
                throw new InvalidOperationException("Finish the active map edit before starting a canvas gesture.");
            try { BeginGestureCore(cell, erase, brushErase); }
            catch { CancelGesture(); throw; }
        }

        private void BeginGestureCore(Vector2Int cell, bool erase, bool brushErase)
        {
            if (IsDragging) CancelGesture();
            var room = Room;
            if (room == null || !room.visible || room.locked || LockedLayers.Contains(Layer) || HiddenLayers.Contains(Layer)
                || MapLayerGroups.IsLocked(Session.Document, ActiveGroupId) || !MapLayerGroups.IsVisible(Session.Document, ActiveGroupId)) return;
            if (Layer == MapLayer.All && Tool != MetroidvaniaStudioTool.Selection && Tool != MetroidvaniaStudioTool.Rooms) return;
            start = last = cell;
            erasing = erase;
            gestureRoomId = room.id;
            gestureGroupId = ActiveGroupId;
            gestureLayer = Layer;
            gestureSelectedTool = Tool;
            // Object placement stays selected when returning to tiles. On tile layers
            // it must paint the held stroke, not just stamp its final cell on release.
            gestureTool = brushErase || IsTileLayer && Tool == MetroidvaniaStudioTool.Placement ? MetroidvaniaStudioTool.Brush : Tool;
            gestureDocument = Session.Document;
            movingSelection = gestureTool == MetroidvaniaStudioTool.Selection && Selection.HasValue && Selection.Value.Contains(cell);
            IsDragging = true;
            if (gestureTool == MetroidvaniaStudioTool.Selection) return;
            if (gestureTool == MetroidvaniaStudioTool.Rooms) return;
            if (IsTileLayer)
            {
                gestureTileEdit = new MapTileIncrementalEdit(room, Layer);
                Session.BeginIncrementalEdit(erase ? "Erase tiles" : "Paint " + gestureTool, gestureTileEdit);
            }
            else
            {
                gestureObjectEdit = new MapObjectIncrementalEdit(room);
                Session.BeginIncrementalEdit("Place object", gestureObjectEdit);
            }
            gestureEditToken = Session.ActiveEditToken;
            ownsGestureEdit = true;
            if (gestureTool == MetroidvaniaStudioTool.Brush && IsTileLayer) Paint(MapBrushGeometry.Expand(new[] { cell }, BrushSize));
            else if (gestureTool == MetroidvaniaStudioTool.Bucket && IsTileLayer)
            {
                MapCell target = room.GetCell(Layer, cell.x, cell.y);
                string targetMaterial = target?.material;
                var occupancy = Cells(room, Layer).ToDictionary(c => new Vector2Int(c.x, c.y));
                Paint(MapBrushGeometry.FloodFill(cell, new RectInt(0, 0, room.width, room.height), p =>
                    occupancy.TryGetValue(p, out var candidate) ? candidate.material == targetMaterial && CanEditMember(Layer, candidate.groupId) : targetMaterial == null));
            }
        }

        public void DragGesture(Vector2Int cell)
        {
            try { DragGestureCore(cell); }
            catch { CancelGesture(); throw; }
        }

        /// <summary>
        /// Applies an already-rasterized continuous path with one brush expansion
        /// and one tile-index pass. This is used by external-editor batches so a
        /// fast pointer stroke does not allocate and repaint a full brush square
        /// once per browser event.
        /// </summary>
        public void DragGesturePath(IReadOnlyList<Vector2Int> cells)
        {
            if (cells == null) throw new ArgumentNullException(nameof(cells));
            try
            {
                if (!IsDragging || cells.Count == 0) return;
                if (!ReferenceEquals(gestureDocument, Session.Document) || ownsGestureEdit && !OwnsGestureEdit()
                    || !ownsGestureEdit && Session.IsEditing) { CancelGesture(); return; }
                if (Room == null || !Room.visible || Room.locked || ActiveRoomId != gestureRoomId || Layer != gestureLayer
                    || Tool != gestureSelectedTool || ActiveGroupId != gestureGroupId
                    || !CanEditMember(Layer, ActiveGroupId)) { CancelGesture(); return; }
                if (cells[0] != last)
                    throw new ArgumentException("A batched path must begin at the gesture's current cell.", nameof(cells));
                if (gestureTool == MetroidvaniaStudioTool.Brush && IsTileLayer)
                    Paint(MapBrushGeometry.ExpandContinuousPath(cells, BrushSize));
                last = cells[cells.Count - 1];
            }
            catch { CancelGesture(); throw; }
        }

        private void DragGestureCore(Vector2Int cell)
        {
            if (!IsDragging) return;
            if (!ReferenceEquals(gestureDocument, Session.Document) || ownsGestureEdit && !OwnsGestureEdit()
                || !ownsGestureEdit && Session.IsEditing) { CancelGesture(); return; }
            if (Room == null || !Room.visible || Room.locked || ActiveRoomId != gestureRoomId || Layer != gestureLayer
                || Tool != gestureSelectedTool || ActiveGroupId != gestureGroupId
                || !CanEditMember(Layer, ActiveGroupId)) { CancelGesture(); return; }
            if (gestureTool == MetroidvaniaStudioTool.Brush && IsTileLayer)
                Paint(MapBrushGeometry.Expand(MapBrushGeometry.Line(last, cell), BrushSize));
            last = cell;
        }

        public IEnumerable<Vector2Int> Preview(Vector2Int cell)
        {
            Vector2Int a = IsDragging ? start : cell;
            switch (IsDragging ? gestureTool : Tool)
            {
                case MetroidvaniaStudioTool.Rectangle: return MapBrushGeometry.Rectangle(a, cell, Filled);
                case MetroidvaniaStudioTool.Line: return MapBrushGeometry.ExpandContinuousPath(MapBrushGeometry.Line(a, cell), BrushSize);
                case MetroidvaniaStudioTool.Circle: return MapBrushGeometry.Circle(a, cell, Filled);
                case MetroidvaniaStudioTool.Ellipse: return MapBrushGeometry.Ellipse(a, cell, Filled);
                default: return MapBrushGeometry.Expand(new[] { cell }, BrushSize);
            }
        }

        public void EndGesture(Vector2Int cell)
        {
            if (!IsDragging) return;
            DragGesture(cell);
            if (!IsDragging) return;
            try
            {
                if (gestureTool == MetroidvaniaStudioTool.Selection)
                {
                    if (movingSelection) MoveSelection(cell - start);
                    else SelectArea(Bounds(start, cell));
                }
                else if (gestureTool == MetroidvaniaStudioTool.Rooms)
                {
                    Vector2Int delta = cell - start;
                    if (delta != Vector2Int.zero)
                        RoomEditor.MoveSelected(delta);
                }
                else
                {
                    if (IsTileLayer && gestureTool != MetroidvaniaStudioTool.Brush && gestureTool != MetroidvaniaStudioTool.Bucket)
                        Paint(Preview(cell));
                    else if (gestureTool == MetroidvaniaStudioTool.Placement && !IsTileLayer && Room.Contains(cell.x, cell.y))
                    {
                        var area = Bounds(start, cell);
                        var item = new MapObject { definition = ObjectDefinition, layer = Layer, groupId = ActiveGroupId,
                            x = area.x, y = area.y, width = area.width, height = area.height };
                        gestureObjectEdit.Add(Room.objects.Count, item);
                        Room.objects.Add(item);
                    }
                    if (OwnsGestureEdit()) Session.EndEdit();
                }
            }
            catch { if (OwnsGestureEdit()) Session.CancelEdit(); throw; }
            finally { ForgetGesture(); Revision++; }
        }

        public void CancelGesture()
        {
            if (ObjectEditor.IsDragging) ObjectEditor.CancelMove();
            if (RoomEditor.IsDragging) RoomEditor.CancelMove();
            bool owns = OwnsGestureEdit();
            ForgetGesture();
            if (owns) Session.CancelEdit();
            Revision++;
        }

        private bool OwnsGestureEdit() => ownsGestureEdit && ReferenceEquals(gestureDocument, Session.Document)
            && gestureEditToken != null && ReferenceEquals(gestureEditToken, Session.ActiveEditToken);

        private void ForgetGesture()
        {
            IsDragging = false;
            ownsGestureEdit = false;
            gestureDocument = null;
            gestureEditToken = null;
            gestureTileEdit = null;
            gestureObjectEdit = null;
            paintingCells = null;
            paintingIndices = null;
            gesturePaintWork = 0;
        }

        public void FinishGesture()
        {
            if (ObjectEditor.IsDragging) ObjectEditor.EndMove();
            if (RoomEditor.IsDragging) RoomEditor.EndMove();
            if (IsDragging) EndGesture(last);
        }

        private void Paint(IEnumerable<Vector2Int> points)
        {
            var room = Room;
            if (Shape < TileShape.Solid || Shape > TileShape.TopRight) throw new ArgumentOutOfRangeException(nameof(Shape));
            var cells = Cells(room, Layer);
            // Build the index once per gesture. Keep the serialized list live so the
            // canvas and exports see pending edits without copying the whole layer.
            if (paintingCells != cells || paintingIndices == null || paintingIndices.Count != cells.Count)
            {
                paintingCells = cells;
                paintingIndices = new Dictionary<Vector2Int, int>(cells.Count);
                for (int i = 0; i < cells.Count; i++) paintingIndices.Add(new Vector2Int(cells[i].x, cells[i].y), i);
            }
            bool painted = false;
            foreach (var point in points)
            {
                if (++gesturePaintWork > MaximumPaintWork)
                    throw new InvalidOperationException($"One paint gesture cannot traverse more than {MaximumPaintWork} cells.");
                if (!room.Contains(point.x, point.y)) continue;
                bool found = paintingIndices.TryGetValue(point, out int index);
                MapCell existing = found ? cells[index] : null;
                if (found && !CanEditMember(Layer, existing.groupId)) continue;
                if (erasing)
                {
                    if (!found) continue;
                    int tail = cells.Count - 1;
                    gestureTileEdit.RemoveSwap(index, existing, cells[tail]);
                    if (index != tail)
                    {
                        MapCell moved = cells[tail];
                        cells[index] = moved;
                        paintingIndices[new Vector2Int(moved.x, moved.y)] = index;
                    }
                    cells.RemoveAt(tail);
                    paintingIndices.Remove(point);
                }
                else
                {
                    painted = true;
                    string group = existing?.groupId ?? ActiveGroupId;
                    if (found && existing.shape == Shape && existing.material == Material && existing.groupId == group) continue;
                    var replacement = new MapCell { x = point.x, y = point.y, shape = Shape, material = Material, groupId = group };
                    if (found)
                    {
                        gestureTileEdit.Replace(index, existing, replacement);
                        cells[index] = replacement;
                    }
                    else
                    {
                        gestureTileEdit.Add(cells.Count, replacement);
                        paintingIndices.Add(point, cells.Count);
                        cells.Add(replacement);
                    }
                }
            }
            if (painted) MapRoomTheme.Apply(room, ResolveTerrainTileSet?.Invoke(Material));
            Revision++;
        }

        public void Pick(Vector2Int cell)
        {
            if (!IsTileLayer) return;
            MapCell tile = Room?.GetCell(Layer, cell.x, cell.y);
            if (tile == null) return;
            Shape = tile.shape;
            Material = tile.material;
            ActiveGroupId = tile.groupId;
        }

        public void SelectArea(RectInt area)
        {
            if (Room == null) return;
            int x = MathEx.Clamp(area.xMin, 0, Room.width), y = MathEx.Clamp(area.yMin, 0, Room.height);
            int right = MathEx.Clamp(area.xMax, 0, Room.width), top = MathEx.Clamp(area.yMax, 0, Room.height);
            Selection = right > x && top > y ? new RectInt(x, y, right - x, top - y) : (RectInt?)null;
            if (!IsTileLayer && Selection.HasValue) ObjectEditor.SelectArea(new Rect(x, y, right - x, top - y));
            else ObjectEditor.Clear();
        }

        public void SelectAll()
        {
            if (!IsTileLayer && Layer != MapLayer.All) { Selection = null; ObjectEditor.SelectAll(); }
            else if (Room != null) SelectArea(new RectInt(0, 0, Room.width, Room.height));
        }
        public void PasteAt(Vector2 at)
        {
            PasteClipboardAt(at);
        }
        public void Deselect() { Selection = null; ObjectEditor.Clear(); }

        private MapRoom FindRoom(MapDocument document) => document.rooms.Find(room => room.id == ActiveRoomId);
        public static List<MapCell> Cells(MapRoom room, MapLayer layer)
        {
            if (layer == MapLayer.ForegroundTiles) return room.foreground;
            if (layer == MapLayer.BackgroundTiles) return room.background;
            throw new ArgumentException("A tile layer is required.", nameof(layer));
        }
        public static RectInt Bounds(Vector2Int a, Vector2Int b) => new RectInt(MathEx.Min(a.x, b.x), MathEx.Min(a.y, b.y), MathEx.Abs(a.x - b.x) + 1, MathEx.Abs(a.y - b.y) + 1);
    }
}
