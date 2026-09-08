# Objects and trigger events

The Objects palette contains **Spawn**, **Portal**, **Path**, and **Respawn point**. Portals are magenta. Use the Triggers layer to draw a rectangular event region. Previously placed legacy objects stay in existing maps.

Every placed object, trigger, and decal has a unique ID. The inspector displays it as a read-only value, and JSON exports retain it. Copying an object or a room assigns new IDs to the copies. Moving, resizing, saving and reopening keep the original IDs.

Room title handles and resize handles work on every layer. Locked rooms remain protected.

## Edit a trigger

1. Select the Triggers layer and drag an area.
2. Select its Event, from `Trigger001` through `Trigger200`.
3. Check Once if this event should be delivered only once per manager session.
4. Enter a Description and click Apply properties.

Descriptions appear at the center of objects and trigger regions in the map editor. They are hidden in Game view. A portal also has an event and description; portals always use Once.

`None` means unassigned. Old free-text event values remain in imported maps and are shown as existing events. Select a numbered event before requesting them through a runtime manager.

## Runtime contract

Engine packages include a trigger manager. Your game decides when to request an event, for example after its own collision check or interaction button. Importing a room does not connect collision callbacks to event delivery.

Each successful request supplies:

| Value | Meaning |
| --- | --- |
| Room ID and name | The authored room containing the object |
| Room X and Y | The room origin in tile coordinates, X right and Y up |
| Object ID | The unique ID of the trigger or portal |
| Definition | For example, `Area` or `Portal` |
| Event | Stable enum: `None = 0`, `Trigger001 = 1`, through `Trigger200 = 200` |
| Once | Whether repeated requests are blocked |
| Description | The authored description |

Room coordinates do not change with PPU or engine coordinate conversion. Object-local position and bounds remain available in the imported object data.

The map format remains version 2. Trigger settings use the existing properties array:

```json
"properties": [
  {"key":"event","value":"Trigger037"},
  {"key":"once","value":"true"},
  {"key":"desc","value":"North gate"}
]
```

Event numbers have the same meanings in all packages. The game assigns behavior to them; the studio does not implement transitions, spawning, quests, or save-game rules.

## Unity

An imported room exposes `StudioImportedRoom.Triggers`. Subscribe once and request an object's ID when your game decides it has activated:

```csharp
var manager = importedRoom.Triggers;
manager.TriggerRequested += info => HandleEvent(info);
bool delivered = manager.TryRequest(objectId, out var info);
```

`StudioPlacedObject.TryRequestTrigger(out info)` is a shortcut for its containing room. `StudioTriggerEvent` and `StudioTriggerInfo` are in `MetroidvaniaStudio.Integration`.

For multiple loaded rooms, create one `StudioTriggerManager` for the world and pass it as the optional fourth argument of `StudioRoomBuilder.Build`. This lets every room use the same subscription and one-shot state.

## Unreal Engine

An imported room actor exposes a **Trigger Manager** component. In Blueprint, bind **On Trigger Requested**, then call **Request Trigger** with the authored Object ID. The result is true only when the event was delivered. **Get Trigger** reads its information without consuming it.

The payload uses `FStudioTriggerInfo`; `RoomPosition` is an integer X/Y pair and `Event` is `EStudioTriggerEvent`. In C++, call `Room->TriggerManager->RequestTrigger(ObjectId)` after including `MetroidvaniaStudioTriggerManager.h`.

To centralize a world, add a Metroidvania Studio Trigger Manager component to a persistent actor and call **Register Room** with each loaded document JSON and room ID. Subscribe to that component and direct your requests to it. Both Unreal packages use this API.

## Godot

An imported room contains a `TriggerManager` node. Connect its signal once:

```gdscript
var manager = imported_room.get_node("TriggerManager")
manager.trigger_requested.connect(handle_event)
manager.request_trigger(object_id)

func handle_event(info: Dictionary) -> void:
    print(info.roomId, info.objectId, info.event, info.description)
```

The dictionary contains `roomId`, `roomName`, `roomX`, `roomY`, `objectId`, `definition`, `event`, `once` and `description`. Enum constants are available through `MetroidvaniaStudioTriggerEvent.Id`.

For a persistent world manager, create a `MetroidvaniaStudioTriggerManager`, call `register_room(room_dictionary)` for each room, and connect its `trigger_requested` signal. Authored registrations survive saving an imported scene.

## SDL

`SdlRoom.triggers` registers the loaded room. Use the manager after a successful Load:

```cpp
room.triggers.onTriggered = [](const MetroidvaniaStudio::TriggerInfo &info)
{
    HandleEvent(info);
};
MetroidvaniaStudio::TriggerInfo info;
bool delivered = room.triggers.TryRequest(objectId, info);
```

For several rooms, use one `MetroidvaniaStudio::TriggerManager` and call `RegisterRoom(room.data)` on each loaded room. Its callback receives the same information as the other engines. The manager is independent of rendering and does not poll SDL input or collision state.

## One-shot lifetime

A manager consumes a Once event before calling the subscriber, so a recursive request cannot deliver it twice. Queries do not consume anything. Unassigned, missing, or already consumed IDs return false without delivery. Portals use this rule even if an old JSON says `once=false`.

Re-registering or unregistering a room on the same manager preserves consumed IDs. Use `ResetOnce` / `reset_once` to reactivate one ID, or `ResetAllOnce` / `reset_all_once` for all IDs. `Clear` / `clear` removes registrations and consumed state. A new manager starts a new session; a game that needs persistence across scene changes should keep its world manager alive and save its own progression.

Duplicate object IDs across registered rooms are rejected before replacing that room's registrations. Request and subscription methods are intended for the game thread. Subscribe and unsubscribe according to your game's object lifetime.
