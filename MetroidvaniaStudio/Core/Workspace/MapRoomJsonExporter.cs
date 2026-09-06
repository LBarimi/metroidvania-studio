using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MetroidvaniaStudio
{
    /// <summary>Exports independent room documents without changing the source map or its history.</summary>
    public static class MapRoomJsonExporter
    {
        public const int MaximumFileNameLength = 120;
        // Planning keeps every room JSON string alive for the preview and atomic
        // export. Keep that aggregate no larger than one supported map file so
        // repeated global metadata cannot expand a small source into gigabytes.
        public const long MaximumAggregateUtf8Bytes = MapDocumentStore.MaximumFileBytes;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        public sealed class Entry
        {
            public string RoomId { get; }
            public string RoomName { get; }
            public string FileName { get; }
            public string Json { get; }
            internal int Utf8ByteCount { get; }

            internal Entry(string roomId, string roomName, string fileName, string json, int utf8ByteCount)
            {
                RoomId = roomId;
                RoomName = roomName;
                FileName = fileName;
                Json = json;
                Utf8ByteCount = utf8ByteCount;
            }
        }

        private sealed class Publication
        {
            public string Destination, Staged, Backup;
            public bool Published, HadOriginal;
            public byte[] WrittenHash, OriginalHash;
            public long WrittenLength, OriginalLength;
        }

        public static IReadOnlyList<Entry> Plan(MapDocument source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            try { source.Validate(); }
            catch (InvalidOperationException error)
            { throw new InvalidOperationException(MetroidvaniaStudioLocale.Format("roomJson.invalid_map", error.Message), error); }
            if (source.rooms.Count == 0) throw new InvalidOperationException(MetroidvaniaStudioLocale.Text("roomJson.empty_map"));

            var snapshot = source.Clone();
            List<MapRoom> rooms = snapshot.rooms;
            CheckSharedMetadataBudget(snapshot, rooms.Count);
            var entries = new List<Entry>(rooms.Count);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long aggregateBytes = 0;
            foreach (MapRoom room in rooms)
            {
                string stem = SafeStem(room.name);
                string fileName = UniqueName(stem, names);
                snapshot.rooms = new List<MapRoom> { room };
                string json = MapDocumentStore.Serialize(snapshot, true);
                int byteCount;
                try
                {
                    byteCount = Utf8.GetByteCount(json);
                    if (byteCount > MapDocumentStore.MaximumFileBytes)
                        throw new InvalidOperationException(MetroidvaniaStudioLocale.Format("roomJson.too_large", room.name));
                }
                catch (EncoderFallbackException error)
                { throw new InvalidOperationException(MetroidvaniaStudioLocale.Format("roomJson.invalid_text", room.name), error); }
                if (byteCount > MaximumAggregateUtf8Bytes - aggregateBytes)
                    throw AggregateSizeError();
                aggregateBytes += byteCount;
                entries.Add(new Entry(room.id, room.name, fileName, json, byteCount));
            }
            return entries.AsReadOnly();
        }

        public static string[] Export(MapDocument source, string directory, bool overwrite = false, string protectedSourcePath = null,
            Action<string> publishedCleanupWarning = null, Action<int, string> publicationBoundaryHook = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException(MetroidvaniaStudioLocale.Text("roomJson.choose_directory"), nameof(directory));
            return ExportPlanned(Plan(source), directory, overwrite, protectedSourcePath, publishedCleanupWarning,
                publicationBoundaryHook);
        }

        /// <summary>Publishes a previously validated plan without serializing every room a second time.</summary>
        public static string[] ExportPlanned(IReadOnlyList<Entry> entries, string directory, bool overwrite = false,
            string protectedSourcePath = null, Action<string> publishedCleanupWarning = null,
            Action<int, string> publicationBoundaryHook = null)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException(MetroidvaniaStudioLocale.Text("roomJson.choose_directory"), nameof(directory));
            Entry[] planned = CopyAndValidatePlan(entries);
            if (overwrite && !string.IsNullOrWhiteSpace(protectedSourcePath))
            {
                RejectDevicePath(directory);
                RejectDevicePath(protectedSourcePath);
            }
            string root = Path.GetFullPath(directory);
            string protectedPath = string.IsNullOrWhiteSpace(protectedSourcePath) ? null : Path.GetFullPath(protectedSourcePath);
            var publications = new List<Publication>(planned.Length);
            var destinations = new string[planned.Length];
            string staging = Path.Combine(root, ".map-room-export-" + Guid.NewGuid().ToString("N"));
            for (int i = 0; i < planned.Length; i++)
            {
                destinations[i] = Path.Combine(root, planned[i].FileName);
                publications.Add(new Publication
                {
                    Destination = destinations[i],
                    Staged = Path.Combine(staging, i.ToString(CultureInfo.InvariantCulture) + ".json"),
                    Backup = Path.Combine(staging, i.ToString(CultureInfo.InvariantCulture) + ".backup")
                });
            }
            // All document, name, source-path and collision checks precede directory creation.
            CheckDestinations(root, publications, overwrite, protectedPath);
            bool createdRoot = !Directory.Exists(root);
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(staging);
                for (int i = 0; i < planned.Length; i++)
                {
                    byte[] bytes = Utf8.GetBytes(planned[i].Json);
                    publications[i].WrittenHash = Hash(bytes);
                    publications[i].WrittenLength = bytes.LongLength;
                    using (var stream = new FileStream(publications[i].Staged, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 4096, FileOptions.WriteThrough))
                    { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    MapDocumentStore.Load(publications[i].Staged);
                }
                CaptureDestinationBaselines(root, publications, overwrite, protectedPath);
                for (int index = 0; index < publications.Count; index++)
                {
                    Publication file = publications[index];
                    publicationBoundaryHook?.Invoke(index, file.Destination);
                    if (file.HadOriginal)
                    {
                        // Same-volume replacement preserves the old bytes in staging until
                        // every publication succeeds. Never delete an original as a fallback.
                        File.Replace(file.Staged, file.Destination, file.Backup);
                        file.Published = true;
                        // The backup is the exact destination version displaced at
                        // the atomic publication boundary. It must still match the
                        // version confirmed after staging, or an external writer
                        // would be overwritten by an apparently successful export.
                        if (!Matches(file.Backup, file.OriginalHash, file.OriginalLength))
                            throw new IOException("The export destination changed before publication: " + file.Destination);
                    }
                    else
                    {
                        // A path absent from the confirmed baseline stays a
                        // create-new operation even when overwrite was requested.
                        // A concurrent creator therefore wins without data loss.
                        File.Move(file.Staged, file.Destination);
                        file.Published = true;
                    }
                }
            }
            catch (Exception error)
            {
                var failures = RollBack(publications);
                if (failures.Count > 0)
                {
                    failures.Insert(0, error);
                    throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.rollback_failed", staging,
                        string.Join("\n", failures.ConvertAll(item => item.Message))), new AggregateException(failures));
                }
                Exception cleanup = RemoveStaging(staging);
                if (createdRoot) RemoveEmptyRoot(root);
                if (cleanup != null)
                    throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.failed_cleanup", error.Message, staging, cleanup.Message),
                        new AggregateException(error, cleanup));
                throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.failed", error.Message), error);
            }
            Exception finalCleanup = RemoveStaging(staging);
            if (finalCleanup != null)
            {
                string warning = MetroidvaniaStudioLocale.Format("roomJson.saved_cleanup", planned.Length, staging, finalCleanup.Message);
                if (publishedCleanupWarning == null) throw new IOException(warning, finalCleanup);
                publishedCleanupWarning(warning);
            }
            return destinations;
        }

        private static void CheckSharedMetadataBudget(MapDocument snapshot, int roomCount)
        {
            snapshot.rooms = new List<MapRoom>();
            string sharedJson = MapDocumentStore.Serialize(snapshot, true);
            int sharedBytes;
            try { sharedBytes = Utf8.GetByteCount(sharedJson); }
            catch (EncoderFallbackException error)
            { throw new InvalidOperationException(MetroidvaniaStudioLocale.Text("roomJson.invalid_shared_text"), error); }
            // Every standalone room document contains this global envelope plus
            // a non-empty room object. Division avoids overflow on multiplication.
            if (roomCount > 0 && sharedBytes > MaximumAggregateUtf8Bytes / roomCount)
                throw AggregateSizeError();
        }

        private static Entry[] CopyAndValidatePlan(IReadOnlyList<Entry> entries)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            if (entries.Count == 0) throw new InvalidOperationException(MetroidvaniaStudioLocale.Text("roomJson.empty_map"));
            if (entries.Count > MapDocument.MaximumRoomCount) throw AggregateSizeError();
            var copy = new Entry[entries.Count];
            long aggregateBytes = 0;
            for (int index = 0; index < copy.Length; index++)
            {
                Entry entry = entries[index] ?? throw new ArgumentException("The room export plan contains a null entry.", nameof(entries));
                if (entry.Utf8ByteCount < 0 || entry.Utf8ByteCount > MaximumAggregateUtf8Bytes - aggregateBytes)
                    throw AggregateSizeError();
                aggregateBytes += entry.Utf8ByteCount;
                copy[index] = entry;
            }
            return copy;
        }

        private static InvalidOperationException AggregateSizeError()
            => new InvalidOperationException(MetroidvaniaStudioLocale.Text("roomJson.aggregate_too_large"));

        private static void CheckDestinations(string root, List<Publication> files, bool overwrite, string protectedPath)
        {
            if (overwrite && protectedPath != null)
            {
                RejectReparseAncestors(root);
                RejectReparseAncestors(protectedPath);
            }
            if (File.Exists(root)) throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.not_directory", root));
            foreach (Publication file in files)
            {
                if (protectedPath != null && string.Equals(file.Destination.Normalize(NormalizationForm.FormC),
                    protectedPath.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase))
                    throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.protected_source", file.Destination));
                if (Directory.Exists(file.Destination)) throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.not_file", file.Destination));
                if (File.Exists(file.Destination) && !overwrite)
                    throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.exists", file.Destination));
            }
        }

        private static void CaptureDestinationBaselines(string root, List<Publication> files, bool overwrite,
            string protectedPath)
        {
            CheckDestinations(root, files, overwrite, protectedPath);
            long aggregateBytes = 0;
            foreach (Publication file in files)
            {
                file.HadOriginal = false;
                file.OriginalHash = null;
                file.OriginalLength = 0;
                FileStream stream;
                try
                {
                    stream = new FileStream(file.Destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                        65536, FileOptions.SequentialScan);
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                using (stream)
                {
                    // This also closes the check/open race for no-overwrite
                    // exports: a destination created after CheckDestinations is
                    // rejected before any room is published.
                    if (!overwrite)
                        throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.exists", file.Destination));
                    long length = stream.Length;
                    if (length > MaximumAggregateUtf8Bytes - aggregateBytes)
                        throw new IOException("Existing export destinations exceed the 32 MiB comparison limit.");
                    aggregateBytes += length;
                    using (SHA256 algorithm = SHA256.Create()) file.OriginalHash = algorithm.ComputeHash(stream);
                    if (stream.Length != length)
                        throw new IOException("The export destination changed while it was being confirmed: " + file.Destination);
                    file.OriginalLength = length;
                    file.HadOriginal = true;
                }
            }
        }

        private static void RejectDevicePath(string path)
        {
            // Check the caller's spelling before GetFullPath can normalize device syntax.
            string windowsPath = path.Replace('/', '\\');
            if (windowsPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                || windowsPath.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.alias_device", path));
        }

        private static void RejectReparseAncestors(string path)
        {
            for (string current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                // A new export directory may not exist yet; its existing ancestors
                // must still be inspected. Other I/O failures cannot prove safety.
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                    || error is System.Security.SecurityException)
                { throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.alias_inspection_failed", current, error.Message), error); }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(MetroidvaniaStudioLocale.Format("roomJson.alias_reparse", current));
            }
        }

        private static List<Exception> RollBack(List<Publication> files)
        {
            var errors = new List<Exception>();
            for (int i = files.Count - 1; i >= 0; i--)
            {
                Publication file = files[i];
                try
                {
                    // Also handle a replacement that produced its backup before an I/O error.
                    if (file.HadOriginal && File.Exists(file.Backup))
                    {
                        if (File.Exists(file.Destination))
                        {
                            if (!Matches(file.Destination, file.WrittenHash, file.WrittenLength))
                            {
                                errors.Add(new IOException("The exported destination changed before rollback; its current bytes and the original backup were preserved: "
                                    + file.Destination));
                                continue;
                            }
                            string displaced = file.Staged + ".rollback";
                            File.Replace(file.Backup, file.Destination, displaced);
                            if (File.Exists(displaced))
                            {
                                if (Matches(displaced, file.WrittenHash, file.WrittenLength)) File.Delete(displaced);
                                else errors.Add(new IOException("The exported destination changed during rollback; the additional external bytes were preserved: "
                                    + displaced));
                            }
                        }
                        else File.Move(file.Backup, file.Destination);
                    }
                    else if (file.Published && !file.HadOriginal && File.Exists(file.Destination))
                    {
                        // Move first so the hash describes the exact version at
                        // the rollback boundary. Delete only our own publication.
                        string displaced = file.Staged + ".rollback";
                        File.Move(file.Destination, displaced);
                        if (Matches(displaced, file.WrittenHash, file.WrittenLength)) File.Delete(displaced);
                        else errors.Add(new IOException("A newly exported destination was changed externally; those bytes were preserved: "
                            + displaced));
                    }
                }
                catch (Exception error)
                { errors.Add(new IOException(MetroidvaniaStudioLocale.Format("roomJson.restore_failed", file.Destination, error.Message), error)); }
            }
            return errors;
        }

        private static byte[] Hash(byte[] bytes)
        {
            using (SHA256 algorithm = SHA256.Create()) return algorithm.ComputeHash(bytes);
        }

        private static bool Matches(string path, byte[] expected, long expectedLength)
        {
            if (expected == null || !File.Exists(path)) return false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 algorithm = SHA256.Create())
            {
                if (stream.Length != expectedLength) return false;
                byte[] actual = algorithm.ComputeHash(stream);
                if (actual.Length != expected.Length) return false;
                int difference = 0;
                for (int i = 0; i < actual.Length; i++) difference |= actual[i] ^ expected[i];
                return difference == 0;
            }
        }

        private static Exception RemoveStaging(string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    // A successful Windows delete can leave the empty directory
                    // present. Reuse the bounded retry before reporting success.
                    if (Directory.Exists(path)) throw new IOException("The temporary export folder is still present after cleanup.");
                    return null;
                }
                catch (Exception error) when ((error is IOException || error is UnauthorizedAccessException)
                    && attempt < 5)
                {
                    // ReplaceFile can briefly retain its internal rename after
                    // returning on Windows. Give that bounded transient window
                    // time to close before reporting a cleanup failure.
                    System.Threading.Thread.Sleep((attempt + 1) * 10);
                }
                catch (Exception error) { return error; }
            }
        }

        private static void RemoveEmptyRoot(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string SafeStem(string name)
        {
            var text = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char value = name[i];
                if (char.IsHighSurrogate(value) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                { text.Append(value); text.Append(name[++i]); }
                else text.Append(char.IsControl(value) || char.IsSurrogate(value) || "<>:\"/\\|?*".IndexOf(value) >= 0 ? '_' : value);
            }
            string stem = text.ToString().Normalize(NormalizationForm.FormC).TrimEnd(' ', '.');
            if (string.IsNullOrWhiteSpace(stem)) stem = "room";
            string device = stem.Split('.')[0].TrimEnd(' ');
            bool reserved = string.Equals(device, "CON", StringComparison.OrdinalIgnoreCase)
                || string.Equals(device, "PRN", StringComparison.OrdinalIgnoreCase)
                || string.Equals(device, "AUX", StringComparison.OrdinalIgnoreCase)
                || string.Equals(device, "NUL", StringComparison.OrdinalIgnoreCase)
                || device.Length == 4 && device[3] >= '1' && device[3] <= '9'
                    && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
            return reserved ? "_" + stem : stem;
        }

        private static string UniqueName(string stem, HashSet<string> names)
        {
            for (int number = 1; ; number++)
            {
                string suffix = number == 1 ? "" : " (" + number.ToString(CultureInfo.InvariantCulture) + ")";
                int available = MaximumFileNameLength - 5 - suffix.Length;
                int length = Math.Min(stem.Length, available);
                if (length > 0 && char.IsHighSurrogate(stem[length - 1])) length--;
                // Trimming a shortened name can reveal a reserved device name.
                string prefix = SafeStem(stem.Substring(0, length));
                if (prefix.Length > available)
                {
                    length = available;
                    if (char.IsHighSurrogate(prefix[length - 1])) length--;
                    prefix = prefix.Substring(0, length).TrimEnd(' ', '.');
                }
                string fileName = prefix + suffix + ".json";
                if (names.Add(fileName)) return fileName;
            }
        }
    }
}
