#include "StudioTriggerManager.h"
#include <iostream>

using namespace MetroidvaniaStudio;

int main(int argc, char **argv)
{
    try
    {
        Require(argc == 2, "Pass the trigger fixture JSON.");
        const auto document = JsonReader(ReadText(std::filesystem::u8path(argv[1]))).Read();
        Room room;
        room.metadata = document.Get("rooms").Items().at(0);
        room.id = room.metadata.Get("id").Text();
        room.name = room.metadata.Get("name").Text();
        room.x = room.metadata.Get("x").Integer();
        room.y = room.metadata.Get("y").Integer();
        TriggerManager manager;
        manager.RegisterRoom(room);
        TriggerInfo info;
        int delivered = 0;
        manager.onTriggered = [&](const TriggerInfo &value)
        {
            ++delivered;
            if (value.objectId == "once")
            {
                TriggerInfo recursive;
                Require(!manager.TryRequest("once", recursive), "Reentrant once request.");
            }
        };
        Require(manager.TryGet("once", info) && info.once, "Query should not consume.");
        Require(manager.TryRequest("once", info) && info.roomX == -7 && info.roomY == 3 && info.event == TriggerEvent::Trigger200 && info.description == "Open gate", "Delivery payload.");
        Require(!manager.TryRequest("once", info), "Once delivers once.");
        Require(manager.TryRequest("repeat", info) && manager.TryRequest("repeat", info), "Repeat delivers repeatedly.");
        Require(manager.TryRequest("portal", info) && info.once && !manager.TryRequest("portal", info), "Portal always one-shot.");
        Require(!manager.TryRequest("legacy", info) && !manager.TryRequest("absent", info), "Unassigned requests reject.");
        room.x = 10;
        manager.RegisterRoom(room);
        Require(!manager.TryRequest("once", info) && manager.TryGet("once", info) && info.roomX == 10, "Reload preserves consumed state.");
        manager.UnregisterRoom(room.id);
        manager.RegisterRoom(room);
        Require(!manager.TryRequest("once", info), "Reenter retains once state.");
        auto other = room;
        other.id = "other";
        bool rejected = false;
        try { manager.RegisterRoom(other); } catch (const std::exception &) { rejected = true; }
        Require(rejected && manager.TryGet("once", info) && info.roomId == room.id, "Duplicate registration is atomic.");
        Require(manager.ResetOnce("once") && manager.TryRequest("once", info), "Reset reactivates.");
        manager.ResetAllOnce();
        Require(manager.TryRequest("portal", info), "Reset all reactivates.");
        for (int i = 1; i <= 200; ++i)
        {
            const auto number = std::to_string(i);
            Require(static_cast<int>(ParseTriggerEvent("Trigger" + std::string(3 - number.size(), '0') + number)) == i, "Stable enum numbers.");
        }
        for (const auto &value : {"old", "Trigger201", "Trigger1", "-1", "+1", "1.0", "999999999999999999"})
            Require(ParseTriggerEvent(value) == TriggerEvent::None, "Invalid event.");
        manager.Clear();
        Require(!manager.TryGet("once", info), "Clear removes state.");
        Require(delivered == 6, "Expected six successful callbacks.");
        std::cout << "Trigger requests passed: payload, 200 events, once, portal, reset, reentry and atomic duplicate rejection.\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
