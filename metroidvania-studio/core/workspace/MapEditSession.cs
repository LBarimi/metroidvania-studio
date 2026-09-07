using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MetroidvaniaStudio.Primitives;

namespace MetroidvaniaStudio
{
    public enum MapEditChangeKind { None, Commit, Cancel, Undo, Redo, Reset, Save }

    /// <summary>Owns document transactions, bounded undo, and the saved revision.</summary>
    public sealed class MapEditSession
    {
        private const int HistoryLimit = 100;
        public const long DefaultHistoryByteLimit = 64L * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private sealed class Revision
        {
            public string label;
            public string json;
            public IMapIncrementalEdit incremental;
            public long bytes;
            public long sequence;
        }

        private readonly List<Revision> undo = new List<Revision>();
        private readonly List<Revision> redo = new List<Revision>();
        private readonly long historyByteLimit;
        private long undoBytes, redoBytes, nextSequence;
        private Revision pendingEdit;
        private string savedJson;

        public MapDocument Document { get; private set; }
        public int DocumentEpoch { get; private set; }
        public string FilePath { get; private set; }
        public string SavedJson => savedJson;
        /// <summary>The validated compact snapshot published by the last session change.</summary>
        public string CurrentJson { get; private set; }
        public bool IsDirty => FilePath == null || (IsEditing ? Snapshot() : CurrentJson) != savedJson;
        public bool CanUndo => undo.Count > 0;
        public bool CanRedo => redo.Count > 0;
        public long HistoryByteCount => undoBytes + redoBytes;
        /// <summary>Diagnostic counter used to guard gesture paths against accidental full-document snapshots.</summary>
        public long SnapshotSerializationCount { get; private set; }
        public bool IsEditing => pendingEdit != null;
        public MapEditChangeKind LastChangeKind { get; private set; }
        // A document can retain its identity across consecutive transactions.
        // Gesture owners must match the particular edit, including no-op edits.
        internal object ActiveEditToken => pendingEdit;
        public event Action Changed;

        public MapEditSession(MapDocument document, string filePath = null, string savedJson = null,
            long historyByteLimit = DefaultHistoryByteLimit)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (historyByteLimit <= 0) throw new ArgumentOutOfRangeException(nameof(historyByteLimit));
            this.historyByteLimit = historyByteLimit;
            Document = document.Clone();
            CurrentJson = Snapshot();
            FilePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
            // Recovery records may contain empty saved snapshots.
            this.savedJson = !string.IsNullOrEmpty(savedJson) ? MapDocumentStore.Serialize(MapDocumentStore.Deserialize(savedJson))
                : FilePath != null ? CurrentJson : null;
        }

        public void Execute(string label, Action<MapDocument> edit)
        {
            if (edit == null) throw new ArgumentNullException(nameof(edit));
            BeginEdit(label);
            try
            {
                edit(Document);
                EndEdit();
            }
            catch
            {
                if (IsEditing) CancelEdit();
                throw;
            }
        }

        /// <summary>Validated, single-use content prepared outside the editing lock.</summary>
        public sealed class PreparedSnapshot
        {
            internal readonly MapDocument document;
            internal int consumed;
            public string Json { get; }
            internal PreparedSnapshot(MapDocument document, string json)
            { this.document = document; Json = json; }
        }

        public static PreparedSnapshot PrepareSnapshot(string json)
        {
            MapDocument next = MapDocumentStore.Deserialize(json);
            return new PreparedSnapshot(next, MapDocumentStore.Serialize(next));
        }

        /// <summary>Replaces content as one undoable edit while preserving the saved file identity.</summary>
        public void ApplySnapshot(string label, string json) => ApplySnapshot(label, PrepareSnapshot(json));

        public void ApplySnapshot(string label, PreparedSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (IsEditing) throw new InvalidOperationException("Finish or cancel the current map edit first.");
            if (System.Threading.Interlocked.Exchange(ref snapshot.consumed, 1) != 0)
                throw new InvalidOperationException("A prepared snapshot can only be applied once.");
            if (snapshot.Json == CurrentJson) return;
            // CurrentJson is the validated committed state whenever no gesture is active.
            // Both states are already validated, so committing needs no full JSON pass.
            Revision before = CreateRevision(label, CurrentJson);
            Document = snapshot.document;
            CurrentJson = snapshot.Json;
            Clear(redo, ref redoBytes);
            Push(undo, ref undoBytes, before);
            NotifyChanged(MapEditChangeKind.Commit);
        }

        public void BeginEdit(string label)
        {
            if (IsEditing) throw new InvalidOperationException("Finish or cancel the current map edit first.");
            pendingEdit = CreateRevision(label, Snapshot());
        }

        /// <summary>
        /// Starts a gesture-owned edit whose journal can restore only the touched
        /// cells or objects. Generic callers continue to use <see cref="BeginEdit"/>.
        /// </summary>
        internal void BeginIncrementalEdit(string label, IMapIncrementalEdit incremental)
        {
            if (incremental == null) throw new ArgumentNullException(nameof(incremental));
            if (IsEditing) throw new InvalidOperationException("Finish or cancel the current map edit first.");
            pendingEdit = new Revision
            {
                label = string.IsNullOrWhiteSpace(label) ? "Edit map" : label,
                incremental = incremental,
                sequence = ++nextSequence
            };
        }

        public void EndEdit()
        {
            if (!IsEditing) return;
            string after;
            try
            {
                // Validate and enforce the durable document-size limit before the
                // pending snapshot is released. Any failure can still restore it.
                pendingEdit.incremental?.Complete(Document);
                if (pendingEdit.incremental != null && !pendingEdit.incremental.HasChanges)
                {
                    pendingEdit = null;
                    return;
                }
                after = Snapshot();
            }
            catch
            {
                CancelEdit();
                throw;
            }
            Revision before = pendingEdit;
            pendingEdit = null;
            if (before.incremental == null && after == before.json) return;
            if (before.incremental != null) before.bytes = before.incremental.ByteCount;
            CurrentJson = after;
            Clear(redo, ref redoBytes);
            Push(undo, ref undoBytes, before);
            NotifyChanged(MapEditChangeKind.Commit);
        }

        public void CancelEdit()
        {
            if (!IsEditing) return;
            Revision edit = pendingEdit;
            if (edit.incremental != null) edit.incremental.ApplyBefore(Document);
            else
            {
                CurrentJson = edit.json;
                Document = MapJson.FromJson<MapDocument>(CurrentJson);
            }
            pendingEdit = null;
            NotifyChanged(MapEditChangeKind.Cancel);
        }

        public void Undo()
        {
            EndEdit();
            if (!CanUndo) return;
            Revision previous = Pop(undo, ref undoBytes);
            if (previous.incremental != null)
            {
                string rollback = CurrentJson;
                try
                {
                    previous.incremental.ApplyBefore(Document);
                    CurrentJson = Snapshot();
                    Push(redo, ref redoBytes, previous);
                }
                catch
                {
                    Document = MapJson.FromJson<MapDocument>(rollback);
                    CurrentJson = rollback;
                    Push(undo, ref undoBytes, previous);
                    throw;
                }
            }
            else
            {
                Push(redo, ref redoBytes, CreateRevision(previous.label, Snapshot()));
                CurrentJson = previous.json;
                Document = MapJson.FromJson<MapDocument>(previous.json);
            }
            NotifyChanged(MapEditChangeKind.Undo);
        }

        public void Redo()
        {
            EndEdit();
            if (!CanRedo) return;
            Revision next = Pop(redo, ref redoBytes);
            if (next.incremental != null)
            {
                string rollback = CurrentJson;
                try
                {
                    next.incremental.ApplyAfter(Document);
                    CurrentJson = Snapshot();
                    Push(undo, ref undoBytes, next);
                }
                catch
                {
                    Document = MapJson.FromJson<MapDocument>(rollback);
                    CurrentJson = rollback;
                    Push(redo, ref redoBytes, next);
                    throw;
                }
            }
            else
            {
                Push(undo, ref undoBytes, CreateRevision(next.label, Snapshot()));
                CurrentJson = next.json;
                Document = MapJson.FromJson<MapDocument>(next.json);
            }
            NotifyChanged(MapEditChangeKind.Redo);
        }

        public void New(MapDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            MapDocument next = document.Clone();
            Reset(next, null, null);
        }

        /// <summary>Stops tracking a workspace file while preserving the current edit history.</summary>
        public void DetachFile()
        {
            if (IsEditing) throw new InvalidOperationException("Finish or cancel the current map edit first.");
            FilePath = null; savedJson = null;
            NotifyChanged(MapEditChangeKind.Save);
        }

        public void Save(string path)
            => SaveAndGetPersistedJson(path);

        /// <summary>Saves and returns the exact formatted JSON bytes' source text.</summary>
        public string SaveAndGetPersistedJson(string path)
            => SaveAndGetPersistedJson(path, (destination, json) => MapDocumentStore.SaveValidatedSnapshot(destination, json));

        /// <summary>
        /// Saves through a caller-provided atomic publisher, then marks the exact
        /// in-memory revision as saved only after publication succeeds.
        /// </summary>
        public string SaveAndGetPersistedJson(string path, Action<string, string> publish)
        {
            if (publish == null) throw new ArgumentNullException(nameof(publish));
            EndEdit();
            string current = Snapshot();
            string persisted = MapDocumentStore.Serialize(Document, true);
            publish(path, persisted);
            FilePath = Path.GetFullPath(path);
            CurrentJson = current;
            savedJson = current;
            NotifyChanged(MapEditChangeKind.Save);
            return persisted;
        }

        public void Load(string path)
        {
            // Parse and validate before replacing either the document or its history.
            MapDocument next = MapDocumentStore.Load(path);
            Load(next, path);
        }

        /// <summary>Publishes a document whose source bytes were validated by the caller.</summary>
        public void Load(MapDocument document, string path)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a map file path.", nameof(path));
            MapDocument next = document.Clone();
            string absolutePath = Path.GetFullPath(path);
            Reset(next, absolutePath, MapDocumentStore.Serialize(next));
        }

        private void Reset(MapDocument document, string path, string saved)
        {
            // Validate the replacement completely before publishing any path,
            // document, epoch, or history change.
            string current = MapDocumentStore.Serialize(document);
            DocumentEpoch++;
            Document = document;
            CurrentJson = current;
            FilePath = path;
            savedJson = saved;
            pendingEdit = null;
            Clear(undo, ref undoBytes);
            Clear(redo, ref redoBytes);
            NotifyChanged(MapEditChangeKind.Reset);
        }

        private void NotifyChanged(MapEditChangeKind kind)
        {
            LastChangeKind = kind;
            Changed?.Invoke();
        }

        private string Snapshot()
        {
            SnapshotSerializationCount++;
            return MapDocumentStore.Serialize(Document);
        }

        private Revision CreateRevision(string label, string json) => new Revision
        {
            label = string.IsNullOrWhiteSpace(label) ? "Edit map" : label,
            json = json,
            bytes = Utf8.GetByteCount(json),
            sequence = ++nextSequence
        };

        private void Push(List<Revision> history, ref long bytes, Revision revision)
        {
            history.Add(revision);
            bytes += revision.bytes;
            if (history.Count > HistoryLimit) RemoveOldest(history, ref bytes);
            while (HistoryByteCount > historyByteLimit)
            {
                if (undo.Count == 0) RemoveOldest(redo, ref redoBytes);
                else if (redo.Count == 0) RemoveOldest(undo, ref undoBytes);
                else if (undo[0].sequence <= redo[0].sequence) RemoveOldest(undo, ref undoBytes);
                else RemoveOldest(redo, ref redoBytes);
            }
        }

        private static Revision Pop(List<Revision> history, ref long bytes)
        {
            Revision result = history[history.Count - 1];
            history.RemoveAt(history.Count - 1);
            bytes -= result.bytes;
            return result;
        }

        private static void RemoveOldest(List<Revision> history, ref long bytes)
        {
            if (history.Count == 0) return;
            bytes -= history[0].bytes;
            history.RemoveAt(0);
        }

        private static void Clear(List<Revision> history, ref long bytes)
        {
            history.Clear();
            bytes = 0;
        }
    }
}
