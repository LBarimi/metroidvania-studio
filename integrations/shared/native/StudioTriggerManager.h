#pragma once
#include "StudioDocument.h"
#include "StudioTriggerEvent.generated.h"
#include <functional>
#include <unordered_map>
#include <unordered_set>

namespace MetroidvaniaStudio
{
    struct TriggerInfo
    {
        std::string roomId, roomName, objectId, definition, description;
        int roomX = 0, roomY = 0;
        TriggerEvent event = TriggerEvent::None;
        bool once = false;
    };

    inline TriggerEvent ParseTriggerEvent(std::string value)
    {
        const auto first = value.find_first_not_of(" \t\r\n");
        if (first == std::string::npos)
            return TriggerEvent::None;
        value = value.substr(first, value.find_last_not_of(" \t\r\n") - first + 1);
        std::transform(value.begin(), value.end(), value.begin(), [](unsigned char c)
        {
            return c >= 'A' && c <= 'Z' ? static_cast<char>(c + ('a' - 'A')) : static_cast<char>(c);
        });
        if (value.compare(0, 7, "trigger") == 0)
        {
            value = value.substr(7);
            if (value.size() != 3)
                return TriggerEvent::None;
        }
        if (value.empty() || value.find_first_not_of("0123456789") != std::string::npos)
            return TriggerEvent::None;
        unsigned int number = 0;
        for (const char c : value)
        {
            number = number * 10 + static_cast<unsigned int>(c - '0');
            if (number > 200)
                return TriggerEvent::None;
        }
        return static_cast<TriggerEvent>(number);
    }

    class TriggerManager
    {
        std::unordered_map<std::string, TriggerInfo> entries;
        std::unordered_map<std::string, std::string> owners;
        std::unordered_set<std::string> consumed;

    public:
        std::function<void(const TriggerInfo &)> onTriggered;

        void RegisterRoom(const Room &room)
        {
            Require(!room.id.empty(), "Trigger room ID is required.");
            std::unordered_set<std::string> nextOwners;
            std::unordered_map<std::string, TriggerInfo> nextEntries;
            for (const auto &object : room.metadata.Get("objects").Items())
            {
                const auto id = object.Get("id").Text();
                const auto owner = owners.find(id);
                Require(!id.empty() && nextOwners.insert(id).second && (owner == owners.end() || owner->second == room.id),
                        "Placed object IDs must be nonempty and unique across registered rooms.");
                const auto definition = object.Get("definition").Text();
                auto lower = definition;
                std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c)
                {
                    return c >= 'A' && c <= 'Z' ? static_cast<char>(c + ('a' - 'A')) : static_cast<char>(c);
                });
                const bool portal = lower == "portal";
                if (object.Get("layer").Integer(2) != 3 && !portal)
                    continue;
                const auto property = [&](const std::string &key)
                {
                    for (const auto &value : object.Get("properties").Items())
                        if (value.Get("key").Text() == key)
                            return value.Get("value").Text();
                    return std::string();
                };
                auto once = property("once");
                std::transform(once.begin(), once.end(), once.begin(), [](unsigned char c)
                {
                    return c >= 'A' && c <= 'Z' ? static_cast<char>(c + ('a' - 'A')) : static_cast<char>(c);
                });
                const auto begin = once.find_first_not_of(" \t\r\n");
                once = begin == std::string::npos ? "" : once.substr(begin, once.find_last_not_of(" \t\r\n") - begin + 1);
                TriggerInfo info;
                info.roomId = room.id;
                info.roomName = room.name;
                info.roomX = room.x;
                info.roomY = room.y;
                info.objectId = id;
                info.definition = definition;
                info.event = ParseTriggerEvent(property("event"));
                info.once = portal || once == "true";
                info.description = property("desc");
                nextEntries.emplace(id, std::move(info));
            }
            UnregisterRoom(room.id);
            for (const auto &id : nextOwners)
                owners.emplace(id, room.id);
            for (auto &entry : nextEntries)
                entries.emplace(entry.first, std::move(entry.second));
        }

        void UnregisterRoom(const std::string &roomId)
        {
            for (auto it = owners.begin(); it != owners.end();)
            {
                if (it->second == roomId)
                {
                    entries.erase(it->first);
                    it = owners.erase(it);
                }
                else
                    ++it;
            }
        }

        bool TryGet(const std::string &id, TriggerInfo &info) const
        {
            const auto it = entries.find(id);
            info = it == entries.end() ? TriggerInfo{} : it->second;
            return it != entries.end();
        }

        bool TryRequest(const std::string &id, TriggerInfo &info)
        {
            if (!TryGet(id, info) || info.event == TriggerEvent::None || (info.once && !consumed.insert(id).second))
            {
                info = TriggerInfo{};
                return false;
            }
            const auto snapshot = info;
            const auto callback = onTriggered;
            if (callback)
                callback(snapshot);
            return true;
        }

        bool ResetOnce(const std::string &id)
        {
            return consumed.erase(id) != 0;
        }

        void ResetAllOnce()
        {
            consumed.clear();
        }

        void Clear()
        {
            entries.clear();
            owners.clear();
            consumed.clear();
        }
    };
}
