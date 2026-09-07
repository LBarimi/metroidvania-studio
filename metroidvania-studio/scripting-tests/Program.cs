using System.Diagnostics;
using System.Text.Json;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Scripting;

var tests = new (string Name, Action Body)[]
{
    ("deterministic connected rooms and layered painting", ConnectedRooms),
    ("read-only snapshots and deterministic random", Inspection),
    ("missing host capabilities cannot be reached", DeniedCapabilities),
    ("infinite loops stop within a bounded time", InfiniteLoop),
    ("cancellation interrupts a running script", Cancellation),
    ("native repeat and exponential concatenation are bounded", StringAllocation),
    ("table allocation and cyclic operation arguments are bounded", TableAllocation),
    ("source logs and operation counts are bounded", InputLimits),
    ("invalid final operations leave the input unchanged", AtomicFailure),
    ("bounded string and sequence helper semantics", Libraries),
    ("nested helper callbacks cannot escape VM budgeting", CallbackSafety),
    ("script syntax errors include a local line location", Syntax),
    ("published Lua examples execute through the shared API", Examples),
    ("duplicated room object IDs remain deterministic", DuplicateIds),
    ("reused values cannot amplify JSON past its budget", JsonAmplification),
    ("tile inspection is bounded and uses the input snapshot", TileInspection)
};
int failed = 0;
foreach (var test in tests)
{
    try { test.Body(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + error); }
}
Console.WriteLine($"Lua regression: {tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

static string Empty() => MapDocumentStore.Serialize(new MapDocument());
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static Exception Reject(string source, string? contains = null)
{
    try { LuaScriptRunner.Run(Empty(), source); }
    catch (Exception error)
    {
        if (contains is not null) Check(error.Message.Contains(contains, StringComparison.OrdinalIgnoreCase), error.Message);
        return error;
    }
    throw new Exception("Script unexpectedly succeeded.");
}
static void ConnectedRooms()
{
    const string source = """
        local left = studio.room.add { name = "Entry", x = 0, y = 0, width = 20, height = 12 }
        local right = studio.room.add { name = "Gallery", x = 20, y = 0, width = 30, height = 12 }
        for _, id in ipairs({left, right}) do
          studio.tiles.rectangle { roomId = id, layer = "foreground", x = 0, y = 0, width = 20, height = 2 }
        end
        studio.tiles.paint { roomId = right, layer = "foreground", cells = {{x=5,y=2,shape="bottomLeft"}} }
        studio.object.add { roomId = left, definitionId = "spawn", layer = "entities", x = 2, y = 2 }
        studio.properties.set { roomId = right, values = { region = "Gallery" } }
        studio.camera.set { ppu = 16, width = 320, height = 180 }
        print(left, right)
        """;
    string input = Empty();
    var first = LuaScriptRunner.Run(input, source);
    var second = LuaScriptRunner.Run(input, source);
    Check(first.DocumentJson == second.DocumentJson, "Same input must produce identical room/object IDs and JSON.");
    var map = MapDocumentStore.Deserialize(first.DocumentJson);
    Check(map.rooms.Count == 2 && map.rooms[1].x == map.rooms[0].width, "Rooms must share a boundary.");
    Check(map.rooms[0].foreground.Count == 40 && map.rooms[1].foreground.Count == 41, "Expected painted terrain and one slope.");
    Check(first.OperationCount == 8 && first.Changed && first.Logs.Single() == "script-1\tscript-2", "Result metadata changed.");
}
static void Inspection()
{
    var map = new MapDocument();
    map.rooms.Add(new MapRoom { id = "a", name = "Initial", x = 0, y = 0, width = 10, height = 10 });
    string input = MapDocumentStore.Serialize(map);
    const string source = """
        local rooms = studio.rooms()
        assert(rooms[1].roomId == "a")
        rooms[1].name = "Only a snapshot"
        assert(studio.rooms()[1].name == "Initial")
        assert(studio.document().apiVersion == 1)
        studio.room.update { roomId = "a", name = "Room " .. math.random(100, 999) }
        """;
    Check(LuaScriptRunner.Run(input, source, 12).DocumentJson == LuaScriptRunner.Run(input, source, 12).DocumentJson, "Seed must be reproducible.");
    Check(LuaScriptRunner.Run(input, source, 12).DocumentJson != LuaScriptRunner.Run(input, source, 900).DocumentJson, "Different seeds should select another value.");
    Check(MapDocumentStore.Deserialize(input).rooms[0].name == "Initial", "Input mutated.");
}
static void DeniedCapabilities()
{
    LuaScriptRunner.Run(Empty(), """
        assert(os == nil and io == nil and require == nil and dofile == nil)
        assert(load == nil and loadfile == nil and loadstring == nil and loadfilesafe == nil)
        assert(clr == nil and luanet == nil and debug == nil and package == nil)
        assert(coroutine == nil and pcall == nil and xpcall == nil)
        assert(setmetatable == nil and getmetatable == nil and collectgarbage == nil)
        assert(math.randomseed == nil and string.dump == nil and table.sort == nil)
        """);
    Reject("require('file')");
    Reject("studio.room.add.GetType()");
}
static void InfiniteLoop()
{
    var watch = Stopwatch.StartNew();
    Reject("while true do end", "limit");
    Check(watch.Elapsed < TimeSpan.FromSeconds(8), "Infinite script exceeded its deadline.");
}
static void Cancellation()
{
    using var cancel = new CancellationTokenSource();
    cancel.CancelAfter(25);
    try { LuaScriptRunner.Run(Empty(), "while true do end", cancellationToken: cancel.Token); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Running script did not honor cancellation.");
}
static void StringAllocation()
{
    Reject("local huge = string.rep('a', 2147483647)", "65536");
    Reject("local huge = 'a'; for i = 1, 50 do huge = huge .. huge end", "allocation");
    Reject("local huge = string.format('%1000000000s', 'a')");
    LuaScriptRunner.Run(Empty(), "assert(string.rep('', 2147483647) == '')");
}
static void TableAllocation()
{
    Reject("local data = {}; while true do data[#data + 1] = {} end", "limit");
    Reject("local data = {}; data.cycle = data; studio.properties.set { values = data }", "cycles");
    Reject("studio.tiles.paint {roomId='a',layer='foreground',cells={[1]={x=0,y=0},[4]={x=1,y=0}}}", "consecutive");
}
static void InputLimits()
{
    Reject(new string(' ', LuaScriptRunner.MaximumSourceBytes + 1), "source");
    Reject("for i = 1, 129 do print('line') end", "log limit");
    Reject("for i = 1, 1025 do studio.rename {name='Name'} end", "operation limit");
}
static void AtomicFailure()
{
    string input = Empty();
    try
    {
        LuaScriptRunner.Run(input, "local a=studio.room.add{x=0,y=0,width=10,height=10}; studio.tiles.rectangle{roomId=a,layer='foreground',x=20,y=0,width=2,height=2}");
    }
    catch (ArgumentException)
    {
        Check(MapDocumentStore.Deserialize(input).rooms.Count == 0, "A partial document was exposed.");
        return;
    }
    throw new Exception("Invalid operation did not reject the batch.");
}
static void Libraries()
{
    LuaScriptRunner.Run(Empty(), """
        assert(string.sub("rooms", 2, -2) == "oom")
        assert(string.rep("x", 3, "/") == "x/x/x")
        assert(string.upper("rooms") == "ROOMS")
        assert(string.reverse("abc") == "cba")
        local a,b=string.find("entry-room", "room"); assert(a==7 and b==10)
        local values = {"a", "c"}; table.insert(values, 2, "b")
        assert(table.concat(values, ",") == "a,b,c")
        assert(table.remove(values, 2) == "b" and #values == 2)
        studio.properties.set { values = { removed = studio.null } }
        """);
}
static void CallbackSafety()
{
    Reject("table.sort({2,1},function(a,b) while true do end end)");
    Reject("local f; f=function() return f() end; f()", "limit");
}
static void Syntax()
{
    Exception error = Reject("local a =\n?");
    Check(error.Message.Contains("script") || error.Message.Contains("2"), "Syntax location missing.");
}

static void Examples()
{
    string? root = AppContext.BaseDirectory;
    while (root is not null && !Directory.Exists(Path.Combine(root, "docs", "examples"))) root = Path.GetDirectoryName(root);
    Check(root is not null, "Example directory not found.");
    string path = Path.Combine(root!, "docs", "examples");
    var connected = LuaScriptRunner.Run(Empty(), File.ReadAllText(Path.Combine(path, "connected-rooms.lua")));
    Check(MapDocumentStore.Deserialize(connected.DocumentJson).rooms.Count == 2, "Connected example did not create rooms.");
    var platforms = LuaScriptRunner.Run(connected.DocumentJson, File.ReadAllText(Path.Combine(path, "seeded-platforms.lua")), 37);
    Check(platforms.Changed, "Platform example did not paint.");
    var regions = LuaScriptRunner.Run(platforms.DocumentJson, File.ReadAllText(Path.Combine(path, "region-properties.lua")));
    Check(regions.Changed && regions.OperationCount == 2, "Property example did not update both rooms.");
}
static void DuplicateIds()
{
    const string source = """
        local a = studio.room.add { x=0,y=0,width=10,height=10 }
        studio.object.add { roomId=a,layer="entities",definitionId="spawn",x=1,y=1 }
        studio.room.duplicate { roomId=a,x=10,y=0 }
        """;
    var first = LuaScriptRunner.Run(Empty(), source);
    var second = LuaScriptRunner.Run(Empty(), source);
    Check(first.DocumentJson == second.DocumentJson, "Duplicated object IDs were not repeatable.");
}

static void JsonAmplification()
{
    Reject("local large=string.rep(\"a\",65536); print(large,large,large,large)", "log limit");
    Reject("""
        local large = string.rep('x', 65536)
        local values = {}
        for i=1,1024 do values['property-' .. i] = large end
        studio.properties.set { values=values }
        """, "operation data");
}


static void TileInspection()
{
    var map = new MapDocument();
    var room = new MapRoom { id = "query-room", width = 80, height = 80 };
    room.foreground.Add(new MapCell { x = 1, y = 2, shape = TileShape.BottomLeft, material = "terrain" });
    map.rooms.Add(room);
    string input = MapDocumentStore.Serialize(map);
    var result = LuaScriptRunner.Run(input, """
        local query = { roomId="query-room", layer="foreground", x=0, y=0, width=4, height=4 }
        local first = studio.tiles.get(query)
        assert(#first.cells == 1 and first.cells[1].x == 1 and first.cells[1].y == 2)
        assert(first.cells[1].shape == "bottomLeft" and first.cells[1].materialId == "terrain")
        first.cells[1].x = 70
        studio.tiles.paint { roomId="query-room", layer="foreground", cells={{x=3,y=3}} }
        local second = studio.tiles.get(query)
        assert(#second.cells == 1 and second.cells[1].x == 1)
        """);
    Check(result.OperationCount == 1 && MapDocumentStore.Deserialize(result.DocumentJson).rooms[0].foreground.Count == 2, "Tile query changed queued edits.");
    try { LuaScriptRunner.Run(input, "studio.tiles.get{roomId='query-room',layer='foreground',x=0,y=0,width=65,height=65}"); }
    catch (ArgumentException error) when (error.Message.Contains("4096")) { return; }
    throw new Exception("An oversized tile query was accepted.");
}
