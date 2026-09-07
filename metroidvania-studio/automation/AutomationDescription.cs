using System.Text.Json;
using System.Text.Json.Nodes;

namespace MetroidvaniaStudio.Automation;

public static partial class AutomationEngine
{
    public static JsonElement Describe()
    {
        var operations = new JsonArray();
        JsonObject String(int maximum = 256) => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = maximum };
        JsonObject Integer(int minimum = int.MinValue, int maximum = int.MaxValue) => new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };
        JsonObject Number() => new() { ["type"] = "number" };
        JsonObject Bool(bool value) => new() { ["type"] = "boolean", ["default"] = value };
        JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
        JsonObject Group() => new() { ["type"] = "string", ["maxLength"] = 256, ["default"] = "" };
        JsonObject Properties() => new() { ["type"] = "object", ["propertyNames"] = String(), ["additionalProperties"] = new JsonObject
        { ["type"] = new JsonArray("string", "null"), ["maxLength"] = 65536 } };
        JsonObject Object(params (string Name, JsonNode Schema)[] fields) => new()
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject(fields.Select(field => new KeyValuePair<string, JsonNode?>(field.Name, field.Schema)))
        };
        JsonObject Point(bool shape)
        {
            var point = Object(("x", Integer(0, 1023)), ("y", Integer(0, 1023)));
            point["required"] = new JsonArray("x", "y");
            if (shape) point["properties"]!["shape"] = Enum("solid", "bottomLeft", "bottomRight", "topLeft", "topRight");
            return point;
        }
        JsonObject Nodes() => new() { ["type"] = "array", ["maxItems"] = 4096,
            ["items"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("x", "y"),
                ["properties"] = new JsonObject { ["x"] = Number(), ["y"] = Number() } } };
        void Add(string name, string summary, string[] required, params (string Name, JsonNode Schema)[] fields)
        {
            var schema = Object(fields);
            schema["properties"]!["op"] = new JsonObject { ["const"] = name };
            schema["required"] = new JsonArray(new[] { "op" }.Concat(required).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            operations.Add(new JsonObject { ["name"] = name, ["description"] = summary, ["schema"] = schema });
        }
        Add("room.add", "Create a room and resolve overlap against existing rooms.", new[] { "x", "y", "width", "height" },
            ("id", String()), ("name", String()), ("x", Integer()), ("y", Integer()), ("width", Integer(1, 1024)), ("height", Integer(1, 1024)));
        Add("room.update", "Change room name or visibility.", new[] { "roomId" }, ("roomId", String()), ("name", String()), ("visible", Bool(true)));
        Add("room.move", "Move to absolute tile coordinates; overlap resolves to the nearest free edge.", new[] { "roomId", "x", "y" },
            ("roomId", String()), ("x", Integer()), ("y", Integer()));
        Add("room.resize", "Resize with the editor's contact-only neighbor layout and terrain clamping.", new[] { "roomId", "width", "height" },
            ("roomId", String()), ("x", Integer()), ("y", Integer()), ("width", Integer(1, 1024)), ("height", Integer(1, 1024)), ("crop", Bool(false)), ("keepLocalContents", Bool(false)));
        Add("room.rotate", "Rotate a room and its contents by a quarter turn using the editor transform.", new[] { "roomId" }, ("roomId", String()), ("clockwise", Bool(true)));
        Add("room.duplicate", "Copy room contents with new room/object IDs at the requested position, resolving overlap.", new[] { "roomId", "x", "y" },
            ("roomId", String()), ("id", String()), ("name", String()), ("x", Integer()), ("y", Integer()));
        Add("room.delete", "Delete the explicit room and all its contents.", new[] { "roomId" }, ("roomId", String()));
        var tileCommon = new (string Name, JsonNode Schema)[]
        {
            ("roomId", String()), ("layer", Enum("foreground", "background")), ("materialId", String()), ("groupId", Group()),
            ("shape", Enum("solid", "bottomLeft", "bottomRight", "topLeft", "topRight"))
        };
        (string Name, JsonNode Schema)[] Tiles(params (string Name, JsonNode Schema)[] extra) =>
            tileCommon.Select(field => (field.Name, field.Schema.DeepClone())).Concat(extra).ToArray();
        foreach (string name in new[] { "tiles.paint", "tiles.erase" })
            Add(name, name == "tiles.paint" ? "Set explicit cells in a cached layer index." : "Erase explicit cells from a layer.", new[] { "roomId", "layer", "cells" },
                Tiles(("cells", new JsonObject { ["type"] = "array", ["maxItems"] = MaximumCellVisits, ["items"] = Point(true) })));
        Add("tiles.rectangle", "Paint or erase a filled rectangle inside the room.", new[] { "roomId", "layer", "x", "y", "width", "height" },
            Tiles(("x", Integer(0, 1023)), ("y", Integer(0, 1023)), ("width", Integer(1, 1024)), ("height", Integer(1, 1024)), ("erase", Bool(false))));
        Add("tiles.fill", "Flood-fill four-connected cells matching the origin's material, shape, and group.", new[] { "roomId", "layer", "x", "y" },
            Tiles(("x", Integer(0, 1023)), ("y", Integer(0, 1023)), ("erase", Bool(false))));
        (string Name, JsonNode Schema)[] ObjectFields() => new (string Name, JsonNode Schema)[]
        {
            ("roomId", String()), ("definitionId", String()), ("groupId", Group()), ("x", Number()), ("y", Number()),
            ("width", new JsonObject { ["type"] = "number", ["exclusiveMinimum"] = 0, ["default"] = 1 }),
            ("height", new JsonObject { ["type"] = "number", ["exclusiveMinimum"] = 0, ["default"] = 1 }),
            ("rotation", Number()), ("scaleX", Number()), ("scaleY", Number()), ("nodes", Nodes()), ("properties", Properties())
        };
        Add("object.add", "Create an object using a resource definition ID; dimensions are in tiles.", new[] { "roomId", "definitionId", "layer", "x", "y" },
            ObjectFields().Concat(new (string Name, JsonNode Schema)[] { ("id", String()), ("layer", Enum("entities", "triggers", "foregroundDecals", "backgroundDecals")) }).ToArray());
        Add("object.update", "Change the explicit object's transform, definition, group, nodes, or custom properties.", new[] { "roomId", "objectId" },
            ObjectFields().Concat(new (string Name, JsonNode Schema)[] { ("objectId", String()) }).ToArray());
        Add("object.delete", "Delete the explicit object in its room.", new[] { "roomId", "objectId" }, ("roomId", String()), ("objectId", String()));
        Add("properties.set", "Merge custom string properties; null removes a key. Omit roomId for document properties.", new[] { "values" },
            ("roomId", String()), ("values", Properties()));
        Add("document.update", "Rename the map document.", new[] { "name" }, ("name", String()));
        Add("camera.set", "Set source PPU and reference resolution in document properties.", new[] { "ppu", "width", "height" },
            ("ppu", Integer(1, 8192)), ("width", Integer(1, 16384)), ("height", Integer(1, 16384)));
        var request = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Metroidvania Studio automation batch",
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("apiVersion", "operations"),
            ["properties"] = new JsonObject
            {
                ["apiVersion"] = new JsonObject { ["const"] = ApiVersion },
                ["operations"] = new JsonObject { ["type"] = "array", ["maxItems"] = MaximumOperations,
                    ["items"] = new JsonObject { ["oneOf"] = new JsonArray(operations.Select(operation => operation!["schema"]!.DeepClone()).ToArray()) } }
            }
        };
        return JsonSerializer.SerializeToElement(new JsonObject
        {
            ["apiVersion"] = ApiVersion, ["formatVersion"] = MapDocument.CurrentFormatVersion,
            ["capabilities"] = new JsonArray("atomic-batch", "explicit-identities", "indexed-tiles", "room-layout", "objects", "custom-properties", "camera", "minimap-query", "bounded-tile-query", "cancellation"),
            ["limits"] = new JsonObject { ["operations"] = MaximumOperations, ["visitedCells"] = MaximumCellVisits,
                ["workUnits"] = MaximumWorkUnits, ["requestBytes"] = MaximumRequestBytes, ["documentBytes"] = MaximumDocumentBytes,
                ["queryCells"] = MaximumQueryCells, ["roomCount"] = MapDocument.MaximumRoomCount, ["roomDimension"] = MapDocument.MaximumRoomDimension,
                ["objectsPerRoom"] = MapDocument.MaximumObjectsPerRoom, ["nodesPerObject"] = MapDocument.MaximumNodesPerObject },
            ["coordinates"] = new JsonObject { ["tileSize"] = 16, ["roomOrigin"] = "bottom-left", ["positiveY"] = "up",
                ["roomPosition"] = "world tiles", ["cellPosition"] = "integer local tiles", ["objectPosition"] = "local tiles" },
            ["operations"] = operations, ["requestSchema"] = request
        });
    }
}
