using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MetroidvaniaStudio.Automation;
using MoonSharp.Interpreter;

namespace MetroidvaniaStudio.Scripting;

public sealed record LuaScriptResult(string DocumentJson, string[] Logs, int OperationCount, bool Changed);

public sealed class LuaScriptException : Exception
{
    public LuaScriptException(string message) : base(message) { }
}

/// <summary>Builds one atomic automation batch using a restricted Lua 5.2 interpreter.</summary>
public static partial class LuaScriptRunner
{
    public const int MaximumSourceBytes = 64 * 1024;
    public const int MaximumInstructions = 1_000_000;
    public const int MaximumOperations = AutomationEngine.MaximumOperations;
    public const int MaximumStringLength = 65_536;
    public const long MaximumAllocatedBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan MaximumExecutionTime = TimeSpan.FromSeconds(5);

    public static LuaScriptResult Run(string documentJson, string source, int seed = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentJson);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > MaximumSourceBytes || Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes)
            throw new LuaScriptException("Script source exceeds 64 KiB.");
        if (documentJson.Length > AutomationEngine.MaximumDocumentBytes)
            throw new LuaScriptException("Map input exceeds the scripting size limit.");
        var document = AutomationEngine.Inspect(documentJson, cancellationToken);
        using var run = new Execution(documentJson, document, seed, cancellationToken);
        try
        {
            run.Execute(source);
            cancellationToken.ThrowIfCancellationRequested();
            using var batch = JsonDocument.Parse(JsonSerializer.Serialize(new { apiVersion = 1, operations = run.Operations }));
            var result = AutomationEngine.Apply(documentJson, batch.RootElement, cancellationToken);
            return new LuaScriptResult(result.DocumentJson, run.Logs.ToArray(), result.OperationCount, result.Changed);
        }
        catch (InterpreterException error)
        {
            throw new LuaScriptException(error.DecoratedMessage ?? error.Message);
        }
    }

    private sealed partial class Execution : IDisposable
    {
        private readonly Script script = new(CoreModules.Basic | CoreModules.Math | CoreModules.TableIterators | CoreModules.Bit32);
        private readonly JsonElement document;
        private readonly string documentJson;
        private AutomationTileQuery? tileQuery;
        private readonly CancellationToken cancellationToken;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private readonly long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        private readonly HashSet<string> ids = new(StringComparer.Ordinal);
        private readonly Table nullValue;
        private uint randomState;
        private int idSequence;
        private int jsonNodes;
        private long requestBytes = 64;
        private int logCharacters;
        public List<Dictionary<string, object?>> Operations { get; } = [];
        public List<string> Logs { get; } = [];

        public Execution(string documentJson, JsonElement document, int seed, CancellationToken cancellationToken)
        {
            this.document = document;
            this.documentJson = documentJson;
            this.cancellationToken = cancellationToken;
            randomState = unchecked((uint)seed);
            if (randomState == 0) randomState = 0x6d2b79f5;
            nullValue = new Table(script);
            script.Options.ScriptLoader = new DeniedLoader();
            script.Options.DebugPrint = Log;
            script.Options.DebugInput = _ => throw new LuaScriptException("Console input is unavailable.");
            script.Options.CheckThreadAccess = true;
            foreach (string unavailable in new[] { "os", "io", "require", "dofile", "load", "loadfile", "loadstring", "loadfilesafe", "loadsafe", "clr", "luanet", "debug", "package", "coroutine", "pcall", "xpcall", "setmetatable", "getmetatable", "collectgarbage", "_G", "_MOONSHARP" })
                script.Globals.Set(unavailable, DynValue.Nil);
            script.Globals.Set("print", Callback(args => { LogArguments(args); return DynValue.Nil; }));
            script.Globals.Set("_VERSION", DynValue.NewString("Lua 5.2"));
            script.Globals.Get("math").Table.Set("random", Callback(Random));
            script.Globals.Get("math").Table.Set("randomseed", DynValue.Nil);
            InstallLibraries();
            if (document.TryGetProperty("rooms", out var rooms))
                foreach (var room in rooms.EnumerateArray())
                {
                    if (room.TryGetProperty("roomId", out var id)) ids.Add(id.GetString() ?? "");
                    if (room.TryGetProperty("objects", out var objects))
                        foreach (var item in objects.EnumerateArray())
                            if (item.TryGetProperty("objectId", out id)) ids.Add(id.GetString() ?? "");
                }
            var studio = new Table(script);
            studio.Set("api_version", DynValue.NewNumber(1));
            studio.Set("null", DynValue.NewTable(nullValue));
            studio.Set("log", Callback(args => { LogArguments(args); return DynValue.Nil; }));
            studio.Set("document", Callback(args => Snapshot(document)));
            studio.Set("rooms", Callback(args => Snapshot(document.GetProperty("rooms"))));
            studio.Set("apply", Callback(args => Queue(null, args)));
            foreach (var group in new Dictionary<string, string[]>
            {
                ["room"] = ["add", "update", "move", "resize", "rotate", "duplicate", "delete"],
                ["tiles"] = ["paint", "erase", "rectangle", "fill"],
                ["properties"] = ["set"], ["camera"] = ["set"],
                ["object"] = ["add", "update", "delete"]
            })
            {
                var table = new Table(script);
                foreach (string action in group.Value)
                {
                    string operation = group.Key + "." + action;
                    table.Set(action, Callback(args => Queue(operation, args)));
                }
                studio.Set(group.Key, DynValue.NewTable(table));
            }
            studio.Get("tiles").Table.Set("get", Callback(TileQuery));
            studio.Set("rename", Callback(args => Queue("document.update", args)));
            script.Globals.Set("studio", DynValue.NewTable(studio));
        }

        public void Execute(string source)
        {
            CheckBudget();
            DynValue function = script.LoadString(source, null, "script");
            CheckBudget();
            var coroutine = script.CreateCoroutine(function).Coroutine;
            // A single instruction can concatenate strings. Check after every instruction so
            // exponential concatenation cannot bypass the allocation budget between yields.
            coroutine.AutoYieldCounter = 1;
            for (int instructions = 0; ; instructions++)
            {
                CheckBudget();
                if (instructions >= MaximumInstructions)
                    throw new LuaScriptException("Script instruction limit exceeded.");
                var value = coroutine.Resume();
                CheckBudget();
                if (value.Type != DataType.YieldRequest) break;
            }
        }

        private void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed > MaximumExecutionTime) throw new LuaScriptException("Script time limit exceeded.");
            if (GC.GetAllocatedBytesForCurrentThread() - allocationStart > MaximumAllocatedBytes)
                throw new LuaScriptException("Script allocation limit exceeded.");
        }

        private DynValue Callback(Func<CallbackArguments, DynValue> action) => DynValue.NewCallback((_, args) =>
        {
            CheckBudget();
            var result = action(args);
            CheckBudget();
            return result;
        });

        private DynValue Queue(string? operation, CallbackArguments args)
        {
            if (Operations.Count >= MaximumOperations) throw new LuaScriptException("Script operation limit exceeded.");
            ChargeRequest(128);
            if (args.Count != 1 || args[0].Type != DataType.Table)
                throw new LuaScriptException("Studio operations accept one table argument.");
            var data = ConvertValue(args[0], new HashSet<Table>(), 0) as Dictionary<string, object?>
                ?? throw new LuaScriptException("Operation arguments must use named fields.");
            if (operation is not null)
            {
                if (data.ContainsKey("op")) throw new LuaScriptException("Do not include op in a named Studio method.");
                data["op"] = operation;
            }
            else operation = data.GetValueOrDefault("op") as string ?? throw new LuaScriptException("Operation requires op.");
            string? createdId = null;
            if (operation is "room.add" or "room.duplicate" or "object.add")
            {
                if (!data.ContainsKey("id"))
                {
                    do { createdId = "script-" + (++idSequence); } while (!ids.Add(createdId));
                    data["id"] = createdId;
                }
                else if (data["id"] is string explicitId)
                {
                    ids.Add(explicitId);
                    createdId = explicitId;
                }
            }
            Operations.Add(data);
            return createdId is null ? DynValue.Nil : DynValue.NewString(createdId);
        }

        private object? ConvertValue(DynValue value, HashSet<Table> ancestors, int depth)
        {
            CheckBudget();
            ChargeRequest(32);
            if (++jsonNodes > 200_000 || depth > 32) throw new LuaScriptException("Operation data is too large or deeply nested.");
            switch (value.Type)
            {
                case DataType.Nil: case DataType.Void: return null;
                case DataType.Boolean: return value.Boolean;
                case DataType.String: return RequestText(value.String);
                case DataType.Number:
                    if (!double.IsFinite(value.Number)) throw new LuaScriptException("Operation numbers must be finite.");
                    return value.Number;
                case DataType.Table:
                    if (ReferenceEquals(value.Table, nullValue)) return null;
                    if (!ancestors.Add(value.Table)) throw new LuaScriptException("Operation tables cannot contain cycles.");
                    try
                    {
                        var pairs = value.Table.Pairs.ToArray();
                        if (pairs.Length > 0 && pairs.All(pair => pair.Key.Type == DataType.Number))
                        {
                            if (pairs.Length > 100_000) throw new LuaScriptException("Operation array is too large.");
                            var array = new object?[pairs.Length];
                            foreach (var pair in pairs)
                            {
                                double index = pair.Key.Number;
                                if (index < 1 || index > array.Length || index != Math.Truncate(index))
                                    throw new LuaScriptException("Operation arrays must have consecutive keys starting at 1.");
                                array[(int)index - 1] = ConvertValue(pair.Value, ancestors, depth + 1);
                            }
                            return array;
                        }
                        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                        foreach (var pair in pairs)
                        {
                            if (pair.Key.Type != DataType.String) throw new LuaScriptException("Operation tables cannot mix named and numeric keys.");
                            result.Add(RequestText(pair.Key.String), ConvertValue(pair.Value, ancestors, depth + 1));
                        }
                        return result;
                    }
                    finally { ancestors.Remove(value.Table); }
                default: throw new LuaScriptException("Operation values must be strings, numbers, booleans, tables, or studio.null.");
            }
        }

        private DynValue TileQuery(CallbackArguments args)
        {
            if (args.Count != 1 || args[0].Type != DataType.Table)
                throw new LuaScriptException("Tile queries accept one table argument.");
            Table fields = args[0].Table;
            foreach (var pair in fields.Pairs)
                if (pair.Key.Type != DataType.String || pair.Key.String is not ("roomId" or "layer" or "x" or "y" or "width" or "height"))
                    throw new LuaScriptException("Tile query contains an unknown field.");
            tileQuery ??= AutomationEngine.CreateTileQuery(documentJson, cancellationToken);
            var result = tileQuery.Query(Text(fields.Get("roomId")), Text(fields.Get("layer")),
                Integer(fields.Get("x")), Integer(fields.Get("y")), Integer(fields.Get("width")), Integer(fields.Get("height")), cancellationToken);
            return Snapshot(result);
        }

        private void ChargeRequest(long bytes)
        {
            requestBytes += bytes;
            if (requestBytes > AutomationEngine.MaximumRequestBytes)
                throw new LuaScriptException("Serialized operation data exceeds 8 MiB.");
        }

        private string RequestText(string value)
        {
            Bounded(value);
            // Count every occurrence, including reused strings, before the final serializer
            // can expand a small shared Lua table into a much larger JSON allocation.
            ChargeRequest(System.Text.Json.JsonEncodedText.Encode(value).EncodedUtf8Bytes.Length + 3L);
            return value;
        }

        private DynValue Snapshot(JsonElement element)
        {
            int remaining = 200_000;
            return FromJson(element, 0, ref remaining);
        }

        private DynValue FromJson(JsonElement value, int depth, ref int remaining)
        {
            CheckBudget();
            if (--remaining < 0 || depth > 32) throw new LuaScriptException("Requested snapshot is too large. Query studio.rooms() instead.");
            switch (value.ValueKind)
            {
                case JsonValueKind.String: return DynValue.NewString(Bounded(value.GetString()!));
                case JsonValueKind.Number: return DynValue.NewNumber(value.GetDouble());
                case JsonValueKind.True: return DynValue.True;
                case JsonValueKind.False: return DynValue.False;
                case JsonValueKind.Null: return DynValue.NewTable(nullValue);
                case JsonValueKind.Array:
                    var array = new Table(script);
                    int index = 1;
                    foreach (var item in value.EnumerateArray()) array.Set(index++, FromJson(item, depth + 1, ref remaining));
                    return DynValue.NewTable(array);
                case JsonValueKind.Object:
                    var table = new Table(script);
                    foreach (var item in value.EnumerateObject()) table.Set(item.Name, FromJson(item.Value, depth + 1, ref remaining));
                    return DynValue.NewTable(table);
                default: return DynValue.Nil;
            }
        }

        private DynValue Random(CallbackArguments args)
        {
            if (args.Count > 2) throw new LuaScriptException("math.random accepts zero, one, or two arguments.");
            randomState ^= randomState << 13;
            randomState ^= randomState >> 17;
            randomState ^= randomState << 5;
            double unit = randomState / 4294967296.0;
            if (args.Count == 0) return DynValue.NewNumber(unit);
            int minimum = args.Count == 1 ? 1 : Integer(args[0]);
            int maximum = Integer(args[args.Count - 1]);
            if (maximum < minimum) throw new LuaScriptException("Random interval is empty.");
            return DynValue.NewNumber(minimum + Math.Floor(unit * ((long)maximum - minimum + 1)));
        }

        private void LogArguments(CallbackArguments args)
        {
            var result = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                string value = Display(args[i]);
                if ((long)result.Length + value.Length + (i == 0 ? 0 : 1) > 4096)
                    throw new LuaScriptException("Script log limit exceeded.");
                if (i != 0) result.Append('\t');
                result.Append(value);
            }
            Log(result.ToString());
        }

        private void Log(string value)
        {
            Bounded(value);
            if (value.Length > 4096 || Logs.Count >= 128 || (logCharacters += value.Length) > 16_384)
                throw new LuaScriptException("Script log limit exceeded.");
            Logs.Add(value);
        }

        private static string Display(DynValue value) => Bounded(value.ToPrintString());
        private static string Bounded(string value) => value.Length <= MaximumStringLength
            ? value : throw new LuaScriptException("String exceeds 65536 characters.");
        private static string Text(DynValue value) => value.Type == DataType.String || value.Type == DataType.Number
            ? Bounded(value.CastToString()) : throw new LuaScriptException("Expected a string.");
        private static int Integer(DynValue value) => value.Type == DataType.Number && double.IsFinite(value.Number)
            && value.Number == Math.Truncate(value.Number) && value.Number >= int.MinValue && value.Number <= int.MaxValue
            ? (int)value.Number : throw new LuaScriptException("Expected a 32-bit integer.");
        public void Dispose() { }
    }

    private sealed class DeniedLoader : MoonSharp.Interpreter.Loaders.IScriptLoader
    {
        public object LoadFile(string file, Table globalContext) => throw new LuaScriptException("Script file access is unavailable.");
        public string ResolveFileName(string filename, Table globalContext) => throw new LuaScriptException("Script file access is unavailable.");
        public string ResolveModuleName(string modname, Table globalContext) => throw new LuaScriptException("Script modules are unavailable.");
    }
}
